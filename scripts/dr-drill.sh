#!/usr/bin/env bash
# DR drill (fault injection): repeatedly stop the primary Container App, measure how long
# Azure Front Door takes to serve users from the standby region, then restart the primary
# and measure failback. Also records DNS, TCP, TLS and time-to-first-byte for every probe.
#
# Requires: az (logged in by the AzureCLI@2 task), curl, jq, python3, awk (all present on ubuntu-latest).

set -uo pipefail   # no "-e": failed probes are expected during the drill

ITERATIONS="${ITERATIONS:-5}"
OUT="${OUT_DIR:-drill-output}"
API_VERSION="${API_VERSION:-2026-01-01}"
: "${HOST:?HOST is required}"
: "${PRIMARY_APP:?PRIMARY_APP is required}"
: "${PRIMARY_RG:?PRIMARY_RG is required}"
: "${PRIMARY_REGION:?PRIMARY_REGION is required}"
: "${STANDBY_REGION:?STANDBY_REGION is required}"
: "${DRILL_EMAIL:?DRILL_EMAIL is required}"
: "${DRILL_PASSWORD:?DRILL_PASSWORD is required}"

URL="https://$HOST"
SUB=$(az account show --query id -o tsv)
BASE="/subscriptions/$SUB/resourceGroups/$PRIMARY_RG/providers/Microsoft.App/containerApps/$PRIMARY_APP"
JAR=$(mktemp)
STOPPED=0
ITER=0
mkdir -p "$OUT"

echo "iteration,epoch,http_code,region,session_created_in,dns_s,tcp_s,tls_s,ttfb_s,total_s" > "$OUT/probes.csv"
echo "iteration,outage_s,failover_from_stop_s,failed_probes,session_survived,failback_s,failback_errors,status" > "$OUT/results.csv"

now()        { date +%s.%N; }
elapsed_gt() { awk -v a="$(now)" -v b="$1" -v l="$2" 'BEGIN{exit !(a-b>l)}'; }
diff_s()     { awk -v a="$1" -v b="$2" 'BEGIN{printf "%.1f", a-b}'; }

app_state() {
  az containerapp show --name "$PRIMARY_APP" --resource-group "$PRIMARY_RG" \
     --query properties.provisioningState -o tsv 2>/dev/null || echo "Unknown"
}

# Wait until Azure is not busy with another operation on the app (avoids conflict errors).
wait_settled() {
  local limit="$1" t0 s
  t0=$(now)
  while :; do
    s=$(app_state)
    case "$s" in Succeeded|Failed|Canceled|Unknown) return 0 ;; esac
    elapsed_gt "$t0" "$limit" && return 1
    sleep 5
  done
}

# Short status line for the log, to help diagnose "operation in progress" conflicts.
app_status() {
  az containerapp show --name "$PRIMARY_APP" --resource-group "$PRIMARY_RG" \
     --query "[properties.provisioningState, properties.runningStatus]" -o tsv 2>/dev/null | tr '\t\n' '  ' || true
}

# Call the stop or start API. If Azure answers with a conflict (another operation still running),
# keep retrying every 20 s for up to 15 minutes. The az error text stays visible in the log.
lifecycle() {
  local action="$1" t0 n=0
  t0=$(now)
  while :; do
    n=$((n + 1))
    wait_settled 60 || true
    if az rest --method POST --url "$BASE/$action?api-version=$API_VERSION" --only-show-errors >/dev/null; then
      return 0
    fi
    echo "The $action call failed (attempt $n, $(diff_s "$(now)" "$t0") s so far). App state: $(app_status). Retrying in 20 s..."
    elapsed_gt "$t0" 900 && return 1
    sleep 20
  done
}
stop_primary()  { lifecycle stop; }
start_primary() { lifecycle start; }

# Safety net: never leave the primary stopped if the script exits early or is cancelled.
cleanup() {
  if [ "$STOPPED" = "1" ]; then
    echo "Cleanup: starting the primary app again"
    start_primary || echo "WARNING: could not start the primary app. Start it manually in the portal."
  fi
}
trap cleanup EXIT INT TERM

# One request to /api/me through Front Door, using the session cookie.
# Sets: CODE, REGION (empty when the app did not answer), CREATED, PT (timestamp).
probe() {
  local body m
  body=$(mktemp)
  m=$(curl -s -o "$body" -b "$JAR" --max-time 5 \
        -w '%{http_code},%{time_namelookup},%{time_connect},%{time_appconnect},%{time_starttransfer},%{time_total}' \
        "$URL/api/me" 2>/dev/null) || true
  [ -z "$m" ] && m="000,0,0,0,0,0"
  CODE="${m%%,*}"
  REGION=$(jq -r '.thisRequestServedBy // .servedBy // empty' "$body" 2>/dev/null || true)
  CREATED=$(jq -r '.sessionCreatedInRegion // empty' "$body" 2>/dev/null || true)
  PT=$(now)
  rm -f "$body"
  echo "$ITER,$PT,$CODE,$REGION,$CREATED,${m#*,}" >> "$OUT/probes.csv"
}

# Probe once a second until the wanted region has answered N times in a row.
wait_for_region() {
  local want="$1" need="$2" limit="$3" got=0 t0
  t0=$(now)
  while :; do
    probe
    if [ "$REGION" = "$want" ]; then got=$((got + 1)); else got=0; fi
    [ "$got" -ge "$need" ] && return 0
    elapsed_gt "$t0" "$limit" && return 1
    sleep 1
  done
}

# Create the test account if needed, then sign in to get a fresh session cookie.
login() {
  local payload
  payload=$(jq -n --arg e "$DRILL_EMAIL" --arg p "$DRILL_PASSWORD" '{email:$e,password:$p}')
  curl -s -o /dev/null --max-time 8 -X POST "$URL/api/auth/register" \
       -H 'Content-Type: application/json' -d "$payload" || true
  : > "$JAR"
  curl -s -o /dev/null --max-time 8 -c "$JAR" -X POST "$URL/api/auth/login" \
       -H 'Content-Type: application/json' -d "$payload" || true
}

FAILURES=0
record_fail() { FAILURES=$((FAILURES + 1)); echo "$ITER,,,,,,,$1" >> "$OUT/results.csv"; echo "Iteration $ITER FAILED: $1"; }

for ITER in $(seq 1 "$ITERATIONS"); do
  echo
  echo "=== Iteration $ITER of $ITERATIONS ==="

  # 1 and 2. Start only from a settled state. After a failback, Front Door's edge locations take a
  # while to agree that the primary is healthy again. Starting a new failure too early gives
  # misleadingly short outages (the standby is already answering some requests before the stop).
  # So: 30 consecutive answers from the primary, then a 10-second baseline that must be clean.
  READY=0
  for TRY in 1 2 3; do
    if ! wait_for_region "$PRIMARY_REGION" 30 600; then
      break
    fi
    login
    probe
    if [ "$CODE" != "200" ] || [ "$CREATED" != "$PRIMARY_REGION" ]; then
      echo "Sign-in check failed (HTTP $CODE, session created in '$CREATED'). Trying again..."
      sleep 10
      continue
    fi
    echo "Session created in $CREATED. Baseline for 10 seconds..."
    BASE_BAD=0
    for _ in 1 2 3 4 5 6 7 8 9 10; do
      probe
      [ "$REGION" != "$PRIMARY_REGION" ] && BASE_BAD=$((BASE_BAD + 1))
      sleep 1
    done
    if [ "$BASE_BAD" -eq 0 ]; then READY=1; break; fi
    echo "Baseline not clean: $BASE_BAD of 10 responses did not come from $PRIMARY_REGION. Waiting 30 s and trying again ($TRY of 3)..."
    sleep 30
  done
  if [ "$READY" != "1" ]; then
    record_fail "Front Door was not stably serving from the primary before the drill"
    continue
  fi

  # 3. Inject the failure: stop the primary Container App.
  wait_settled 180 || true     # do not let a busy app distort the timing
  T_STOP=$(now)
  if ! stop_primary; then
    record_fail "stop command failed (check permissions and API_VERSION)"
    continue
  fi
  STOPPED=1
  echo "Primary stopped. Probing every second until the standby answers..."

  # Recovery means FIVE standby answers in a row, not just the first one. A single early answer can come
  # from Front Door retrying one in-flight request, while later requests would still fail.
  T_FAIL=""; T_STANDBY=""; FAILED=0; SESSION_OK="no"; RUN=0; RUN_START=""; RUN_SESSION="no"
  while :; do
    probe
    if [ -z "$REGION" ]; then
      FAILED=$((FAILED + 1))
      [ -z "$T_FAIL" ] && T_FAIL="$PT"
    fi
    if [ "$REGION" = "$STANDBY_REGION" ]; then
      RUN=$((RUN + 1))
      if [ "$RUN" -eq 1 ]; then
        RUN_START="$PT"
        RUN_SESSION="no"
        if [ "$CODE" = "200" ] && [ "$CREATED" = "$PRIMARY_REGION" ]; then RUN_SESSION="yes"; fi
      fi
      if [ "$RUN" -ge 5 ]; then
        T_STANDBY="$RUN_START"
        SESSION_OK="$RUN_SESSION"
        break
      fi
    else
      RUN=0
    fi
    elapsed_gt "$T_STOP" 300 && break
    sleep 1
  done

  # 4. Restore the primary and measure failback.
  # Let Azure finish the stop operation first; starting while it is still busy returns HTTP 409.
  # This waiting happens before the timer starts, so it is not counted as failback time.
  wait_settled 300 || true
  T_START=$(now)
  if ! start_primary; then
    record_fail "start command failed (primary may still be stopped)"
    break
  fi
  STOPPED=0

  if [ -z "$T_STANDBY" ]; then
    record_fail "standby did not answer within 300 s"
    continue
  fi

  echo "Primary started. Probing until it serves again..."
  FB_FIRST=""; FB_ERRORS=0; GOT=0
  while :; do
    probe
    [ -z "$REGION" ] && FB_ERRORS=$((FB_ERRORS + 1))
    if [ "$REGION" = "$PRIMARY_REGION" ]; then
      [ -z "$FB_FIRST" ] && FB_FIRST="$PT"
      GOT=$((GOT + 1))
    else
      GOT=0
    fi
    [ "$GOT" -ge 3 ] && break
    elapsed_gt "$T_START" 600 && break
    sleep 1
  done

  if [ -z "$FB_FIRST" ]; then
    record_fail "primary did not serve again within 600 s"
    continue
  fi

  OUTAGE="0.0"
  [ -n "$T_FAIL" ] && OUTAGE=$(diff_s "$T_STANDBY" "$T_FAIL")
  FROM_STOP=$(diff_s "$T_STANDBY" "$T_STOP")
  FB_S=$(diff_s "$FB_FIRST" "$T_START")

  echo "$ITER,$OUTAGE,$FROM_STOP,$FAILED,$SESSION_OK,$FB_S,$FB_ERRORS,ok" >> "$OUT/results.csv"
  echo "Iteration $ITER: outage=${OUTAGE}s  stop-to-standby=${FROM_STOP}s  failed probes=$FAILED  session survived=$SESSION_OK  failback=${FB_S}s  failback errors=$FB_ERRORS"

  echo "Cooling down for 30 seconds..."
  sleep 30
done

echo
echo "================ SUMMARY ================"
python3 - "$OUT" <<'PY'
import csv, statistics as st, sys, collections

out = sys.argv[1]
rows = list(csv.DictReader(open(f"{out}/results.csv")))
ok = [r for r in rows if r["status"] == "ok"]
lines = [f"Iterations: {len(rows)}   successful: {len(ok)}   failed: {len(rows) - len(ok)}"]

def stats(name, key):
    v = [float(r[key]) for r in ok]
    if not v:
        return
    sd = st.stdev(v) if len(v) > 1 else 0.0
    lines.append(f"{name:<38} n={len(v)} min={min(v):.1f} mean={st.mean(v):.1f} "
                 f"median={st.median(v):.1f} max={max(v):.1f} sd={sd:.1f}")

stats("Outage (first failure to standby OK) s", "outage_s")
stats("Stop command to standby OK s", "failover_from_stop_s")
stats("Failback (start command to primary) s", "failback_s")

if ok:
    surv = sum(1 for r in ok if r["session_survived"] == "yes")
    lines.append(f"Session survived the failover: {surv} of {len(ok)}")
    lines.append("Failed probes during failover (mean): "
                 + f"{st.mean(int(r['failed_probes']) for r in ok):.1f}")

agg = collections.defaultdict(list)
for r in csv.DictReader(open(f"{out}/probes.csv")):
    if r["http_code"] in ("200", "401") and r["region"]:
        agg[r["region"]].append(r)

lines.append("")
lines.append("Connection timings seen by the test agent, per serving region (seconds, mean):")
for region, rs in sorted(agg.items()):
    avg = lambda k: st.mean(float(x[k]) for x in rs)
    lines.append(f"  {region:<16} n={len(rs):<5} dns={avg('dns_s'):.3f} tcp={avg('tcp_s'):.3f} "
                 f"tls={avg('tls_s'):.3f} ttfb={avg('ttfb_s'):.3f} total={avg('total_s'):.3f}")

text = "\n".join(lines)
print(text)
open(f"{out}/summary.txt", "w").write(text + "\n")
PY

# Fail the pipeline if any iteration failed, so a partial drill is never mistaken for a pass.
if ! grep -q ',ok$' "$OUT/results.csv"; then
  echo "No iteration completed successfully."
  exit 1
fi
if [ "$FAILURES" -gt 0 ]; then
  echo "$FAILURES iteration(s) failed. The successful ones are still in results.csv."
  exit 1
fi
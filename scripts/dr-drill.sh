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

stop_primary()  { az rest --method POST --url "$BASE/stop?api-version=$API_VERSION"  --only-show-errors >/dev/null; }
start_primary() { az rest --method POST --url "$BASE/start?api-version=$API_VERSION" --only-show-errors >/dev/null; }

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

record_fail() { echo "$ITER,,,,,,,$1" >> "$OUT/results.csv"; echo "Iteration $ITER FAILED: $1"; }

for ITER in $(seq 1 "$ITERATIONS"); do
  echo
  echo "=== Iteration $ITER of $ITERATIONS ==="

  # 1. Start from a healthy state: Front Door must be serving from the primary.
  if ! wait_for_region "$PRIMARY_REGION" 3 600; then
    record_fail "primary not serving before the drill"
    continue
  fi

  # 2. Create a session while the primary is serving.
  login
  probe
  if [ "$CODE" != "200" ] || [ "$CREATED" != "$PRIMARY_REGION" ]; then
    record_fail "sign-in failed (HTTP $CODE, session created in '$CREATED')"
    continue
  fi
  echo "Session created in $CREATED. Baseline for 5 seconds..."
  for _ in 1 2 3 4 5; do probe; sleep 1; done

  # 3. Inject the failure: stop the primary Container App.
  T_STOP=$(now)
  if ! stop_primary; then
    record_fail "stop command failed (check permissions and API_VERSION)"
    continue
  fi
  STOPPED=1
  echo "Primary stopped. Probing every second until the standby answers..."

  T_FAIL=""; T_STANDBY=""; FAILED=0; SESSION_OK="no"
  while :; do
    probe
    if [ -z "$REGION" ]; then
      FAILED=$((FAILED + 1))
      [ -z "$T_FAIL" ] && T_FAIL="$PT"
    fi
    if [ "$REGION" = "$STANDBY_REGION" ]; then
      T_STANDBY="$PT"
      if [ "$CODE" = "200" ] && [ "$CREATED" = "$PRIMARY_REGION" ]; then SESSION_OK="yes"; fi
      break
    fi
    elapsed_gt "$T_STOP" 300 && break
    sleep 1
  done

  # 4. Restore the primary and measure failback.
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

# Fail the pipeline if no iteration completed, so an empty drill is never mistaken for a pass.
if ! grep -q ',ok$' "$OUT/results.csv"; then
  echo "No iteration completed successfully."
  exit 1
fi
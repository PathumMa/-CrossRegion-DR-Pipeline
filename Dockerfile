# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj first and restore separately to take advantage of Docker layer caching
COPY DemoApi.csproj .
RUN dotnet restore "DemoApi.csproj"

# Copy the rest of the source and publish
COPY . .
RUN dotnet publish "DemoApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run on plain HTTP inside the container; TLS is terminated upstream
# (Azure Container Apps / Front Door), not inside the container itself.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DemoApi.dll"]

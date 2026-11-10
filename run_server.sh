#!/usr/bin/env bash
# Build and run the WorldBox Multiplayer server from a checkout (Linux, macOS, Git Bash).
# Settings come from src/Server/appsettings.json, a .env file next to the binary or in
# the current directory, and env vars (env wins). See deploy/server.env.example.
set -euo pipefail
cd "$(dirname "$0")"

# No .NET telemetry.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

dotnet build src/Server/WorldboxMP.Server.csproj -c Release -v m
exec dotnet src/Server/bin/Release/net10.0/WorldboxMP.Server.dll

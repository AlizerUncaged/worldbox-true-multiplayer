#!/usr/bin/env bash
# Build and run the WorldBox Multiplayer server from a checkout (Linux, macOS, Git Bash).
# Settings: src/Server/appsettings.json (defaults), then appsettings.Local.json in this
# folder (git-ignored, your overrides/secrets), then WBMP_* environment variables.
set -euo pipefail
cd "$(dirname "$0")"

# No .NET telemetry.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

dotnet build src/Server/WorldboxMP.Server.csproj -c Release -v m
exec dotnet src/Server/bin/Release/net10.0/WorldboxMP.Server.dll

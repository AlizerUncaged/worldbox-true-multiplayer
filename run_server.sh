#!/usr/bin/env bash
# Runs the WorldBox Multiplayer server on 0.0.0.0:9191 (the public endpoint the
# Release client defaults to: old-server.example.invalid:9191).
#
# The listen socket already binds ANY ip (0.0.0.0); this script pins the PORT via
# the WBMP_PORT env override. The Mongo connection string comes from
# src/Server/appsettings.json (or override with WBMP_MONGO before running).
set -euo pipefail
cd "$(dirname "$0")"

export WBMP_PORT=9191
# Run on whatever modern .NET runtime the box has, even if newer than net8.0.
export DOTNET_ROLL_FORWARD=LatestMajor

dotnet build src/Server/WorldboxMP.Server.csproj -c Release -v m
exec dotnet src/Server/bin/Release/net8.0/WorldboxMP.Server.dll

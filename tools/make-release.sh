#!/usr/bin/env bash
# Build every release artifact into out/release/:
#   WorldBoxMultiplayer-<version>.zip               the mod (what the installer downloads)
#   WorldBoxMultiplayer-Installer.exe                the one-click installer (Windows)
#   WorldBoxMultiplayer-Server-<version>-linux-x64.tar.gz / -win-x64.zip   self-contained servers
# Needs: .NET SDK 10, Python 3, a WorldBox install with NeoModLoader (WORLDBOX_DIR).
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
: "${WORLDBOX_DIR:?set WORLDBOX_DIR to your WorldBox folder}"

OUT=out/release
rm -rf "$OUT" && mkdir -p "$OUT"

# Mod (obfuscated, version-stamped ship folder)
dotnet build src/Client/WorldboxMP.Client.csproj -c Release -v q -nologo
PUB=$(ls -td src/Client/bin/Publish/*/ | head -1)
VERSION=$(basename "$PUB")
python - "$PUB" "$OUT/WorldBoxMultiplayer-$VERSION.zip" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src).replace(os.sep, "/"))
PY

# Installer
dotnet build installer/WorldboxMP.Installer.csproj -c Release -v q -nologo
cp installer/bin/Release/WorldBoxMultiplayer-Installer.exe "$OUT/"

# Servers (self-contained: no .NET needed on the host)
for rid in linux-x64 win-x64; do
  dotnet publish src/Server/WorldboxMP.Server.csproj -c Release -r "$rid" --self-contained true -o "out/server-$rid" -v q -nologo
  if [ "$rid" = linux-x64 ]; then
    tar -C "out/server-$rid" -czf "$OUT/WorldBoxMultiplayer-Server-$VERSION-$rid.tar.gz" .
  else
    python - "out/server-$rid" "$OUT/WorldBoxMultiplayer-Server-$VERSION-$rid.zip" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src).replace(os.sep, "/"))
PY
  fi
done

echo "$VERSION" > "$OUT/VERSION"
ls -la "$OUT"

#!/usr/bin/env bash
# Build the server and (re)deploy it to a Linux x64 box running systemd. The build is
# self-contained (.NET 10 ships inside it), so the box needs no .NET install and any
# other .NET apps on it are left alone.
#
#   deploy/deploy.sh root@your.server.ip [ssh options...]
#
# First run creates the `wbmp` user, /etc/worldbox-multiplayer/appsettings.json (from
# deploy/appsettings.server.json) and the systemd unit. Later runs just swap the binaries
# and restart; your settings file and the SQLite database are never touched.
set -euo pipefail
cd "$(dirname "$0")/.."

# No .NET telemetry, anywhere.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

TARGET="${1:?usage: deploy/deploy.sh user@host [ssh options...]}"; shift
SSH=(ssh "$@" "$TARGET")

rm -rf out/linux
dotnet publish src/Server/WorldboxMP.Server.csproj -c Release -r linux-x64 --self-contained true -o out/linux
tar -C out/linux -czf out/wbmp-server.tgz .

"${SSH[@]}" 'mkdir -p /tmp/wbmp-deploy'
scp "$@" out/wbmp-server.tgz deploy/worldbox-multiplayer.service deploy/appsettings.server.json "$TARGET:/tmp/wbmp-deploy/"

"${SSH[@]}" 'set -e
  id wbmp >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin wbmp
  mkdir -p /etc/worldbox-multiplayer
  [ -f /etc/worldbox-multiplayer/appsettings.json ] || install -m 640 -g wbmp /tmp/wbmp-deploy/appsettings.server.json /etc/worldbox-multiplayer/appsettings.json
  install -m 644 /tmp/wbmp-deploy/worldbox-multiplayer.service /etc/systemd/system/worldbox-multiplayer.service
  rm -rf /opt/worldbox-multiplayer.new && mkdir -p /opt/worldbox-multiplayer.new
  tar -xzf /tmp/wbmp-deploy/wbmp-server.tgz -C /opt/worldbox-multiplayer.new
  rm -rf /opt/worldbox-multiplayer.old
  [ -d /opt/worldbox-multiplayer ] && mv /opt/worldbox-multiplayer /opt/worldbox-multiplayer.old
  mv /opt/worldbox-multiplayer.new /opt/worldbox-multiplayer
  chown -R root:root /opt/worldbox-multiplayer
  chmod +x /opt/worldbox-multiplayer/WorldboxMP.Server
  systemctl daemon-reload
  systemctl enable worldbox-multiplayer >/dev/null 2>&1
  systemctl restart worldbox-multiplayer
  rm -rf /tmp/wbmp-deploy
  sleep 3
  systemctl --no-pager --lines=8 status worldbox-multiplayer'

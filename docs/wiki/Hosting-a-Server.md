# Hosting a Server

The server is a single program (.NET 10). Accounts live in SQLite by default, so there is
nothing else to install. One port number carries both the game (UDP) and a small REST api
(TCP): **open both protocols** in your firewall.

## Quick start

Download `WorldBoxMultiplayer-Server-<version>-<platform>` from the
[releases](https://github.com/AlizerUncaged/worldbox-true-multiplayer/releases) (self-contained:
no .NET needed), extract it and run `WorldboxMP.Server` (`WorldboxMP.Server.exe` on Windows).

From source:

```bash
./run_server.sh                     # builds and runs
# or
dotnet publish src/Server/WorldboxMP.Server.csproj -c Release -o out
cd out && ./WorldboxMP.Server
```

It opens the lobbies, listens on **UDP 40441** for the game and serves
`/api/v1/version` and `/api/v1/health` on **TCP 40441**.

## Settings

Standard .NET configuration, later sources win:

1. `appsettings.json` next to the server: defaults, every key documented. Don't put secrets here.
2. `appsettings.Local.json` next to the server or in the working directory: your overrides
   and secrets (git-ignored). Only include the keys you change.
3. The file named by the `WBMP_SETTINGS` environment variable (deployments use
   `/etc/worldbox-multiplayer/appsettings.json`).
4. Environment variables: `WBMP_` prefix, `__` for nesting, e.g. `WBMP_Port=50001`,
   `WBMP_Admin__Password=...`, `WBMP_Rooms__Lobbies__0=Main`.

Example `appsettings.Local.json`:

```json
{
  "Port": 50001,
  "Rooms": { "Lobbies": [ "Main", "Creative", "Wars" ], "MaxPlayersPerRoom": 24 },
  "Admin": { "Enabled": true, "Password": "a-long-random-password" }
}
```

| Key | Default | Meaning |
|---|---|---|
| `Port` | `40441` | Shared port: UDP game traffic, TCP REST api |
| `LatestVersion`, `MinimumVersion` | empty | Advertised for the in-game update notice |
| `Database:Provider` | `sqlite` | `sqlite` or `mongodb` |
| `Database:ConnectionString` | local `.db` file | SQLite path or MongoDB URI |
| `Rooms:Lobbies` | `Lobby, Sandbox, Chaos` | Always-open lobbies (ids `0000`, `0001`, ...) |
| `Rooms:MaxPlayersPerRoom` | `16` | Per-room cap, `0` = none |
| `Rooms:MaxRooms` | `200` | Cap on player-made rooms, `0` = none |
| `Admin:*` | off | See [Admin Panel](Admin-Panel) |
| `Https:CertificatePath`, `Https:CertificatePassword` | empty | PFX certificate: HTTPS for the api and admin panel |

Security notes: passwords are stored with PBKDF2-SHA256 (100k iterations, per-user salt).
"Keep me signed in" tokens are random and only their SHA-256 is stored. Players are known
to each other by a random id, never by database ids, and IP addresses are never sent to
players. There are deliberately no login lockouts or rate limits; moderate with bans.

## Linux server with systemd

```bash
deploy/deploy.sh root@your.server.ip [ssh options]
```

- Builds a self-contained Linux binary and installs it to `/opt/worldbox-multiplayer`.
- Creates a `wbmp` service user and a hardened systemd unit (`worldbox-multiplayer`).
- On the **first** run, installs `/etc/worldbox-multiplayer/appsettings.json` from
  `deploy/appsettings.server.json` (mode 640, root:wbmp). Put your port, admin password and
  other settings there. Later deploys never touch it, nor the database in
  `/var/lib/worldbox-multiplayer`.

```bash
sudo nano /etc/worldbox-multiplayer/appsettings.json
sudo systemctl restart worldbox-multiplayer
journalctl -u worldbox-multiplayer -f
```

## Pointing the mod at your server

Players can enter your address under **Server settings** in the game. To ship a build that
connects to your server by default, build the client with your address:

```bash
dotnet build src/Client/WorldboxMP.Client.csproj -c Release -p:MainServerSource=http://your.host:40441
```

or put `<MainServerSource>` in a git-ignored `src/Client/WorldboxMP.Client.csproj.user`.
The scheme picks the default port (`https` 443, `http` 80); an explicit `:port` wins.

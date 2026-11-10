<p align="center">
  <img src="docs/images/hero.png" alt="WorldBox Multiplayer" width="100%">
</p>

<p align="center">
  <b>Real-time multiplayer for <a href="https://www.superworldbox.com/">WorldBox</a>.</b><br>
  One living world, many gods. Build, burn and bless it together.
</p>

<p align="center">
  <img alt="Status: beta" src="https://img.shields.io/badge/status-beta-ff4fb4">
  <img alt="NeoModLoader" src="https://img.shields.io/badge/mod%20loader-NeoModLoader-3d7bfa">
  <img alt="Server: .NET 10" src="https://img.shields.io/badge/server-.NET%2010-7c4dff">
  <img alt="Protocol WBMP/4" src="https://img.shields.io/badge/protocol-WBMP%2F4-22c1ee">
</p>

---

## What it is

WorldBox Multiplayer puts several players in the same world at the same time. Every
creature, building, kingdom, war, culture, fire, storm and god power is mirrored to
the whole room as it happens. You see each other's god hands moving over the map.

- **Server lobbies and private rooms.** Hop into an always-open lobby, or create your own
  room with a name, an optional password and a 4-character code to share.
- **One world, one owner.** The player who holds the world is its **Owner**; their
  simulation is the truth and everyone else plays inside it. When the Owner leaves,
  ownership passes on automatically.
- **Full mirroring.** Spawns, deaths, movement, combat, terrain, buildings, cities,
  kingdoms, cultures, languages, religions, clans, families, armies, wars, plots, laws,
  weather, eras, projectiles and more (see [docs/SYNC.md](docs/SYNC.md)).
- **Joins that just work.** New players receive the world as a compressed snapshot with a
  progress bar, then stay in sync from the live stream. Changes made while they load are
  held and replayed, so nothing goes missing.
- **Stays connected.** "Keep me signed in" logs you back in on every launch, and a dropped
  connection reconnects and rejoins your room by itself.
- **Moderation.** Room admins can kick; the Owner can promote and demote admins. Server
  operators get a web admin panel with live players, rooms, bans and announcements.

<p align="center"><img src="docs/images/logo.png" alt="" width="360"></p>

## Play

You need WorldBox on Steam with **experimental mode** enabled.

1. **Install NeoModLoader.** Download `NeoModLoader.dll` from the
   [latest release](https://github.com/WorldBoxOpenMods/ModLoader/releases/latest) and put it in
   `<WorldBox>/worldbox_Data/StreamingAssets/mods/`. Start the game once so it sets itself up.
2. **Install the mod.** Copy the release folder into `<WorldBox>/Mods/WorldBoxMultiplayer/`
   (it contains the mod DLLs, LiteNetLib, `mod.json` and `icon.png`).
3. **Launch WorldBox.** A **True Multiplayer** tab appears in the toolbar. **F9** toggles the
   multiplayer window at any time.
4. **Register or log in**, then join a server lobby, create a room, or enter a friend's code.

The mod connects to the official server out of the box. To use another server, open
**Server settings** in the window (or the toolbar) and enter its address.

## Host your own server

The server is a single .NET 10 program. It stores accounts in SQLite by default, so there
is nothing else to install.

### Quick start (any OS)

```bash
./run_server.sh
```

or build it yourself:

```bash
dotnet publish src/Server/WorldboxMP.Server.csproj -c Release -o out
cd out && ./WorldboxMP.Server        # or: dotnet WorldboxMP.Server.dll
```

It opens the lobbies, listens for game traffic on **UDP 40441**, and serves
`/api/v1/version` and `/api/v1/health` on **TCP 40441** (same number, no conflict).

### Linux box with systemd

```bash
deploy/deploy.sh root@your.server.ip
```

This builds a self-contained Linux binary (no .NET needed on the box), installs it to
`/opt/worldbox-multiplayer`, creates a `wbmp` service user and a hardened systemd unit, and
writes `/etc/worldbox-multiplayer.env` from `.env.example` on the first run. Later runs only swap the binaries
and restart; your settings and database are never touched. .NET telemetry is switched off
in every script and in the service.

### Settings

Settings come from, in order of priority: environment variables, a `.env` file next to the
server (or in the working directory), then `appsettings.json`. The full list with comments
is in [`.env.example`](.env.example); copy it to `.env` and fill it in (never commit it).

| Variable | Default | What it does |
| --- | --- | --- |
| `WBMP_PORT` | `40441` | Shared port: UDP for the game, TCP for the REST api |
| `WBMP_DATABASE_BRAND` | `sqlite` | `sqlite` or `mongodb` |
| `WBMP_CONNECTION_STRING` | local `.db` file | Database connection string |
| `WBMP_LOBBIES` | `Lobby;Sandbox;Chaos` | Always-open server lobbies (ids `0000`, `0001`, ...) |
| `WBMP_MAX_PLAYERS` | `16` | Player cap per room (`0` = no cap) |
| `WBMP_MAX_ROOMS` | `200` | Cap on player-made rooms (`0` = no cap) |
| `WBMP_LATEST_VERSION` / `WBMP_MIN_VERSION` | empty | Drives the in-game update notice |
| `WBMP_CERT_PFX` / `WBMP_CERT_PASS` | empty | PFX certificate: serves the api and admin panel over HTTPS |

Accounts are stored with PBKDF2-SHA256 (100k iterations, per-user salt). "Keep me signed in"
uses random session tokens; only their SHA-256 is stored. Players are identified externally
by a random id, never by database ids, and IP addresses are never shown to players.

### Admin panel

An optional web panel for server operators: who is online and where, every room and lobby,
accounts, bans (account or IP, timed or permanent), password resets, "sign out everywhere",
and on-screen announcements to every player. Light and dark themes included.

```ini
WBMP_ADMIN_ENABLED=true
WBMP_ADMIN_PORT=50002
WBMP_ADMIN_BIND=127.0.0.1      # 0.0.0.0 to expose it; put it behind TLS if you do
WBMP_ADMIN_USER=admin
WBMP_ADMIN_PASSWORD=change-me-please   # seeds the first admin; change it later in the panel
```

The password only seeds the first admin. To force it again, start once with
`WBMP_ADMIN_PASSWORD_RESET=true`. The panel is plain HTTP unless `WBMP_CERT_PFX` is set, so
either bind it to `127.0.0.1` and use an SSH tunnel, or put it behind a TLS reverse proxy.

## Build the mod

Requirements: .NET SDK 10, Python 3 on PATH, and a WorldBox install with NeoModLoader run
at least once (it generates the reference assemblies the build compiles against).

Tell the build where WorldBox is, either by keeping the repo at
`<WorldBox>/Projects/Worldbox-Multiplayer`, or with an environment variable:

```bash
export WORLDBOX_DIR="/path/to/steamapps/common/worldbox"

# Debug: builds and deploys straight into <WorldBox>/Mods/WorldBoxMultiplayer
dotnet build src/Client/WorldboxMP.Client.csproj -c Debug

# Release: obfuscated, version-stamped ship folder in src/Client/bin/Publish/beta-<version>/
dotnet build src/Client/WorldboxMP.Client.csproj -c Release
```

Debug builds connect to `127.0.0.1:40441`. Release builds use the server baked in from
[`src/Client/.env.example`](src/Client/.env.example) (`MAIN_SERVER_SOURCE`). Forks copy it to
`src/Client/.env` (git-ignored), point it at their own server and rebuild.

The admin panel's front end lives in [`admin-ui/`](admin-ui) (Tailwind). After editing it,
run `npm install && npm run build` there; the output is embedded into the server.

### Local test loop

1. Run a local server (SQLite default).
2. Create an empty `.dev` file in the WorldBox folder. Debug clients then sign in as
   `testp1`, `testp2`, ... one per running instance.
3. Start WorldBox twice. Each instance opens a developer REST bridge on `localhost:8750`,
   `8751`, ... to drive it from scripts: `/state`, `/joinroom?id=`, `/spawn`, `/meta`
   (king vs follower comparison), `/actors`, `/shot` (screenshot), and more in
   [`src/Client/Dev/DevRestServer.cs`](src/Client/Dev/DevRestServer.cs).

## Repository layout

```
src/
  Shared/   WorldboxMP.Shared   (netstandard2.0)  packet contracts used by both sides
  Server/   WorldboxMP.Server   (net10.0)         game server, REST api, admin panel, accounts
  Client/   WorldboxMP.Client   (net48 NML mod)   the in-game mod
admin-ui/   admin panel front end (Tailwind), built into the server
deploy/     systemd unit and one-command deploy script (settings template: .env.example)
docs/       architecture, sync coverage, event hook catalog, images
```

## For other modders

Want your mod to work inside multiplayer rooms? [PLUGINS.md](PLUGINS.md) documents a small
public API (`WbmpApi`) with role checks and named sync channels routed through the same
owner-authoritative relay.

## How it works

One player per room owns the world and runs the real simulation. Everyone else runs a
passive replica: local AI decisions are suppressed and the owner streams movement, actions,
combat, terrain and structural changes. Other players' god powers travel to the owner as
intents, run there, and are mirrored back to everyone. The details are in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/SYNC.md](docs/SYNC.md).

## License

[MIT](LICENSE). © 2026 ALIZER.

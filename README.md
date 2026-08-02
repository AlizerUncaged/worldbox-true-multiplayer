# WorldBox Multiplayer

Real-time multiplayer for [WorldBox](https://www.superworldbox.com/), built on
[NeoModLoader](https://github.com/WorldBoxOpenMods/ModLoader). Players share one
living world: god powers, spawns, terrain, fire, weather, wars, projectiles, and
every creature are mirrored to the whole room in real time, with accounts,
shareable room codes, and a king/admin/player hierarchy.

> Status: **beta.** The full mirroring layer is live (34+ mirror kinds,
> king-authoritative), with chunked world transfers, automatic resync on world
> changes, and an in-game update notice. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## How it works (one paragraph)

One player in each room is the **God King** - their simulation is the truth.
Everyone else runs a passive replica: local AI decisions are suppressed and the
king streams movement, actions, combat, terrain, and structural changes. A new
player receives the whole world as a compressed snapshot (chunked, with a
progress bar for big maps) and then stays in sync from the live stream. Follower
god powers are sent to the king as intents, executed there, and mirrored back to
everyone.

## Repository layout

```
Worldbox-Multiplayer.slnx
src/
  Shared/   WorldboxMP.Shared   (netstandard2.0)  - packet contracts shared by both sides
  Server/   WorldboxMP.Server   (net8.0 console)  - game server + REST api + accounts
  Client/   WorldboxMP.Client   (net48 NML mod)   - the in-game mod
docs/
  ARCHITECTURE.md   - the design, the mirror model, the protocol, the hooks
```

## Requirements

- **To play:** WorldBox (Steam) with *experimental mode* enabled, plus
  [NeoModLoader](https://github.com/WorldBoxOpenMods/ModLoader).
- **To build:** .NET SDK 8+, and Python 3 on PATH (two small build scripts:
  version stamping and config baking).
- **To run a server:** the .NET 8+ runtime. Nothing else - the default database
  is a local SQLite file.

## Run a server (zero config)

```bash
dotnet publish src/Server/WorldboxMP.Server.csproj -c Release -o out
cd out
./WorldboxMP.Server.exe          # Windows
dotnet WorldboxMP.Server.dll     # any OS
```

That is a complete instance: it creates `worldbox-multiplayer-local.db` (SQLite)
next to the exe, opens the permanent room `0000` ("Lobby"), listens for game
traffic on **UDP 40441**, and serves the REST api on **TCP 40441**
(`/api/v1/version`, `/api/v1/health`). The same port number carries both - UDP
and TCP do not conflict.

Configuration lives in `appsettings.json` next to the exe (env vars override):

```jsonc
{
  "ConnectionString": "Data Source=worldbox-multiplayer-local.db",
  "DatabaseBrand": "sqlite",        // "sqlite" or "mongodb"
  "Port": 40441,
  "LatestVersion": "",              // advertised to clients for the update notice
  "MinimumVersion": ""
}
```

| Env var | Meaning |
| --- | --- |
| `WBMP_CONNECTION_STRING` | database connection string |
| `WBMP_DATABASE_BRAND` | `sqlite` or `mongodb` |
| `WBMP_PORT` | shared UDP+TCP port |
| `WBMP_LATEST_VERSION` | version advertised at `/api/v1/version` |
| `WBMP_CERT_PFX` / `WBMP_CERT_PASS` | PFX certificate: turns the api into HTTPS |

Production notes: to bind 443 on Linux without root, grant the binary
`setcap 'cap_net_bind_service=+ep'`. No iptables needed. Accounts are stored
with PBKDF2-SHA256 (100k iterations, per-user salt); players are identified
externally by a random Guid (`id4`), never by database ids.

## Build and install the client mod

The client compiles against the game's own assemblies via relative paths, so the
repo is designed to live inside the game folder at
`<WorldBox>/Projects/Worldbox-Multiplayer`.

```bash
# Debug: builds AND deploys straight into <WorldBox>/Mods/WorldBoxMultiplayer
dotnet build src/Client/WorldboxMP.Client.csproj -c Debug

# Release: obfuscated, version-stamped ship build in src/Client/bin/Publish/beta-<version>/
dotnet build src/Client/WorldboxMP.Client.csproj -c Release
```

To install a published build, copy the whole `beta-<version>` folder into the
game as `<WorldBox>/Mods/WorldBoxMultiplayer/` (five files: the two mod DLLs,
LiteNetLib, `mod.json`, and the easter-egg asset). Launch WorldBox with NML and
experimental mode on - a "True Multiplayer" toolbar tab appears; the manager
window also toggles with **F9**. Register or log in, then create a room (you
become its God King) or join one with a 4-letter code.

## Pointing the client at YOUR server

`src/Client/.env` (committed, **build-time only** - nothing is read from disk at
runtime) holds the single source of truth:

```
MAIN_SERVER_SOURCE=https://multiplayer-worlbox.alizer.xyz
```

The scheme picks the shared port (`https` = 443, `http` = 80, an explicit
`:port` wins), and that one origin is used for both the game connection (UDP)
and the REST api (TCP). Fork the repo, change this line, rebuild - your build
now defaults to your instance. Debug builds always default to
`127.0.0.1:40441` for the local loop, and players can point any build at any
server from the in-game settings dialog (stored in PlayerPrefs).

## Local dev loop

1. Run the server (sqlite default is perfect for this).
2. Create a `.dev` file in the WorldBox root: Debug clients then auto-register
   and log in as `testp1`, `testp2`, ... per instance.
3. Build the client in Debug (auto-deploys) and start WorldBox twice: the first
   instance gets a developer REST bridge on `localhost:8750`, the second on
   `8751` (`/state`, `/createroom`, `/joinroom?id=`, `/resync`, `/spawn`,
   `/newworld`, and more - see `src/Client/Dev/DevRestServer.cs`).

## For other modders

Want your mod to work inside multiplayer rooms? See [PLUGINS.md](PLUGINS.md):
a small public API (`WbmpApi`) with role checks and named sync channels routed
through the same king-authoritative relay.

## License

Private project. © ALIZER.

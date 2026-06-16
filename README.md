# WorldBox Multiplayer

Real-time multiplayer for [WorldBox](https://www.superworldbox.com/), built on
[NeoModLoader](https://github.com/WorldBoxOpenMods/ModLoader). Players share one
living world: god powers, spawns, terrain edits, and creatures are mirrored to
everyone in real time, with rooms, accounts, and a moderation hierarchy.

> Status: **early foundation.** Networking, accounts, rooms, and the in-game
> manager UI are in place. The world-mirroring layer is scaffolded and being
> built out — see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and the roadmap below.

## Repository layout

```
Worldbox-Multiplayer.slnx
src/
  Shared/   WorldboxMP.Shared   (netstandard2.0)  — packet contracts shared by both sides
  Server/   WorldboxMP.Server   (net8.0 console)  — bare-metal game server + MongoDB
  Client/   WorldboxMP.Client   (net48 NML mod)   — the in-game mod
docs/
  ARCHITECTURE.md   — the design, the mirror model, the protocol, the hooks
```

- **Transport:** [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (reliable/ordered + sequenced channels).
- **Server endpoint (dev):** `127.0.0.1:40441`.
- **Database:** MongoDB (`wmulti`). Player accounts only; rooms are in-memory.
  External identity is a random **`id4`** (Guid v4), never the enumerable Mongo `ObjectId`.

## Build

Requires the .NET SDK (8+).

```bash
# everything
dotnet build Worldbox-Multiplayer.slnx -c Release

# or individually
dotnet build src/Server/WorldboxMP.Server.csproj -c Release
dotnet build src/Client/WorldboxMP.Client.csproj -c Release
```

The client references the local WorldBox/NML assemblies via relative paths
(`worldbox_Data/...`), so it builds in place inside the game's `Projects/` folder.

## Run the server

```bash
dotnet run --project src/Server/WorldboxMP.Server.csproj
# or run the built exe; override config via env:
#   WBMP_MONGO=<connection string>   WBMP_PORT=40441
```

It connects to MongoDB, ensures the permanent room **`0000`** ("Lobby"), and
listens on UDP `40441`.

## Deploy the client mod

Copy the client build output into a WorldBox mods folder, e.g.
`worldbox_Data/StreamingAssets/mods/WorldBoxMultiplayer/` (or `<game>/Mods/...`):

- `WorldboxMP.Client.dll`
- `WorldboxMP.Shared.dll`
- `LiteNetLib.dll`
- `mod.json`
- (optional) `icons/*.png` to override the placeholder tab/button icons

Then launch WorldBox, enable the mod, and load a world. A **7th toolbar tab**
("Multiplayer") appears; its buttons open the manager window (also toggled with
**F9**). Register/login, then create or join a room.

## Roadmap

- [x] Solution + shared protocol + server + client foundation
- [x] Accounts (register/login, PBKDF2), `id4` external identity
- [x] Rooms: 4-char shareable ids, hierarchy (king/admin/player), passwords, public/unlisted, permanent room `0000`
- [x] In-game 7th tab + manager UI (login, room create/join, roster + moderation, logs)
- [ ] Mirroring: intercept & broadcast god powers / spawns / terrain
- [ ] Follower world sync: suppress local AI, apply authoritative state, entity-id remap
- [ ] Full-world snapshot transfer on join (`SaveManager` zip)
- [ ] Live player cursors (floating hand + name)
- [ ] Pause/speed lockstep across the room

## License

Private project. © ALIZER.

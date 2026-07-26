# Architecture

This document is the source of truth for how WorldBox Multiplayer is designed,
the WorldBox/NML internals it relies on (verified by decompiling the game), and
the plan for the world-mirroring layer.

## 1. Topology

```
        ┌─────────────────────────────────────────────┐
        │            WorldboxMP.Server (net8)          │
        │  LiteNetLib :40441   ·   MongoDB (accounts)  │
        │  Rooms (in-memory)   ·   Auth · Routing      │
        └───────▲───────────────────────▲─────────────┘
                │ reliable/ordered        │
     ┌──────────┴─────────┐    ┌──────────┴──────────┐
     │  King client (host) │   │  Player clients      │
     │  authoritative world│   │  followers (no sim)  │
     └─────────────────────┘   └──────────────────────┘
```

- **Server** is a dumb-but-authoritative *router + account/room authority*. It
  does **not** simulate WorldBox. It owns identity, rooms, permissions, and
  message fan-out.
- **King client** (the room creator) **runs the actual game** and is the
  authority for world state. Its machine does all simulation.
- **Player clients** are **followers**: their local simulation is suppressed and
  they render whatever the king's machine broadcasts (via the server).

This "authoritative host" model is the key decision (per the project brief): we
do **not** re-simulate WorldBox on the server. The server relays; the king
simulates; followers display.

### Why route through the server instead of P2P
- One stable account/room/permission authority.
- No NAT/relay problem between players (only each client ↔ server).
- The server can persist accounts, enforce moderation, and (later) hand off the
  "king" role if the host leaves, by promoting another client to authority and
  re-syncing from its snapshot.

## 2. Identity & accounts

- `players` collection in Mongo. Internal `_id` is an `ObjectId` and is **never**
  exposed. The external id is **`id4`** - a Guid v4 - used for all room
  membership, moderation targets, cursor ownership, etc. (Random, non-enumerable.)
- Passwords: PBKDF2-SHA256, 100k iterations, per-user salt.
- Unique indexes on `username_lower` and `id4`.

## 3. Rooms

- In-memory (`RoomManager`). Only accounts persist.
- **Shareable id:** 4-char uppercase alphanumeric (`A–Z0–9`), generated randomly
  with collision retry (36⁴ ≈ 1.68M ids). Room **`0000`** ("Lobby") is permanent.
- **Hierarchy:** `King` (creator; exactly one) → `Admin` → `Player`.
  - Admins: kick players, (later) set laws.
  - King: everything, plus kick/promote/demote admins.
  - King succession on leave: an admin is promoted, else the next member; an
    empty non-permanent room is destroyed.
- **Passwords:** optional, ≤16 chars, any characters (stored as SHA-256 in memory).
- **Visibility:** `Public` (listed) or `Unlisted` (join by code only).

## 4. Protocol (`WorldboxMP.Shared`)

All packets are `INetSerializable` DTOs routed by LiteNetLib's
`NetPacketProcessor` (it keys packets by a hash of the type's full name, so both
sides agree automatically because the types live in the shared assembly).

| Packet | Dir | Purpose |
|--------|-----|---------|
| `RegisterRequest` / `LoginRequest` → `AuthResponse` | C↔S | accounts |
| `CreateRoomRequest` / `JoinRoomRequest` / `LeaveRoomRequest` | C→S | room ops |
| `RoomJoined` / `RoomError` | S→C | room op results |
| `RoomListRequest` → `RoomListResponse` | C↔S | public room list |
| `RoomRoster` | S→C | full roster, pushed on every change |
| `ModerationRequest` | C→S | kick / promote / demote |
| `CursorUpdate` | C↔S | floating-hand cursors (sequenced) |
| `LogListRequest` → `LogListResponse` | C↔S | server log feed |
| `MirrorMessage` | C↔S | **generic world-mirror envelope** (see §6) |
| `WorldSnapshot` | S→C | full-world zip for a joining player |

`MirrorMessage { byte Kind; string SenderId4; byte[] Payload }` is intentionally
generic so the world-sync layer can add entity kinds/fields without minting new
packet types. `Kind` is a `MirrorKind` (GodPower, SpawnUnit, Terrain,
EntityState, SimControl).

## 5. Verified WorldBox / NML internals

Confirmed by decompiling the **real** `Assembly-CSharp.dll` (the publicized DLL
lies about accessibility - the real one is the source of truth). `pub` = directly
callable; `int` = internal/private, reach via Harmony/reflection.

### Action pipeline (observe → broadcast, and re-apply)
| Concern | Hook / call | Acc. |
|---|---|---|
| God power at tile | `PlayerControl.clickedFinal(Vector2Int, GodPower, bool pTrack)` | `int` |
| Spawn unit | `ActorManager.spawnNewUnit(string assetId, WorldTile, …)` (`World.world.units`) | `pub` |
| Terrain | `MapAction.terraformTile(WorldTile, TileType, TopTileType, …)` | `pub` |
| Tile lookup | `World.world.GetTile(int x, int y)` | `pub` |
| God power asset | `AssetManager.powers.get(id)`; delegate fields `GodPower.click_*_action` | `pub` |

Re-apply on the king: call the same APIs (`clickedFinal` via AccessTools with
`pTrack:false`; `spawnNewUnit`/`terraformTile` directly).

### Entity identity & movement (follower suppression)
- Identity: `Actor.id` (`long`, `BaseSystemData.id`). Lookup `World.world.units.get(long)` (`pub`).
  **IDs are allocated locally (`MapStats.getNextId`) → collide across machines →
  the server/king must own id assignment and clients remap.**
- Position: `BaseSimObject.current_position` (`Vector2`, `pub`),
  `Actor.next_step_position` (`pub`), `current_tile` (`pub`).
- Suppress on followers (Harmony Prefix returning false):
  `Actor.b6_updateAI(float)` and `Actor.u10_checkSmoothMovement(float)` (both `pub`).
- Apply server state: set `current_position`/`next_step_position`; for tiles use
  internal `setCurrentTilePosition`/`spawnOn` via AccessTools.

### World snapshot (join transfer)
- Build: `SaveManager.currentWorldToSavedMap()` → `SavedMap.toZip()` (`byte[]`, zlib JSON). `pub`.
- Apply: `SaveManager.loadMapFromBytes(byte[])`. `pub`. (Heavy, SmoothLoader-staged full reload.)

### Simulation control (lockstep)
- Pause: `Config.paused` (`pub static bool`). Read this, not the internal `MapBox.isPaused()`.
- Speed: `Config.setWorldSpeed(string id)` / `Config.time_scale_asset` (`pub`).
- Note: the game resets `Config.paused = false` on world load - reapply replicated state after a load.

### UI (tab + windows + icons) - all NML, `pub`
- Tab: `NeoModLoader.General.UI.Tab.TabManager.CreateTab(name, titleKey, descKey, Sprite icon)`.
- Buttons: `NeoModLoader.General.PowerButtonCreator.CreateSimpleButton(id, UnityAction, icon)` +
  `AddButtonToTab(button, tab)`.
- Icons: `SpriteTextureLoader.addSprite(pathId, png)` / `getSprite(pathId)`, or
  `NeoModLoader.General.Sprites.LoadSprite(path)`. We also generate placeholder
  sprites at runtime and prefer a PNG from the mod's `icons/` folder if present.
- Text/inputs: built from code with uGUI; `OT.InitializeCommonText(Text)` applies the game font.

## 6. The mirroring plan (next milestones)

**Intent → authority → broadcast.** A non-king player never mutates the shared
world directly; it sends an **intent**. The king executes it and the resulting
state is broadcast to everyone.

1. **Capture (all clients):** Harmony-observe `clickedFinal` / `spawnNewUnit` /
   `terraformTile`. On a non-king, *cancel* the local effect and send a
   `MirrorMessage` intent (kind + tile + power/asset id). On the king, allow it
   and emit the resulting authoritative event.
2. **Authority (king):** the king applies intents in order, lets its local sim
   run, and emits `EntityState`/spawn/terrain events with **server-assigned ids**.
3. **Apply (followers):** followers have local AI/movement suppressed; they
   create/move/destroy entities purely from broadcasts, maintaining a
   `serverId → localActor` map.
4. **Join:** new player receives a `WorldSnapshot` (king's `toZip()`), loads it,
   then starts receiving live deltas. Pause/speed (`SimControl`) is applied last.
5. **Cursors:** every client streams `CursorUpdate` (sequenced); others render a
   floating hand + name.

### Open problems being designed around
- **ID remapping** (local id allocators collide) - server/king authoritative ids.
- **Determinism** - avoided by single-authority simulation; followers never simulate.
- **Snapshot cost** - multi-MB; only on join, not per tick. Live sync is deltas.
- **King handoff** - if the king leaves, promote another client to authority and
  reseed from its latest snapshot (future work; currently king-leave ends the
  shared sim for that room).

## 7. Threading
- **Server:** single thread; all handlers run on the `PollEvents` loop. No locks.
- **Client:** LiteNetLib polled from a MonoBehaviour `Update`, so all packet
  handlers run on the Unity main thread and may touch game/UI objects safely.

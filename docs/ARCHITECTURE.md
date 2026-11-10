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

## 6. How mirroring works

**Intent → authority → broadcast.** A player who isn't the Owner never mutates the shared
world directly; they send an **intent**. The Owner executes it and the resulting state is
broadcast to everyone.

1. **Capture:** Harmony hooks observe the game (`clickedFinal`, unit creation, terraform,
   structural create/remove, memberships, ...). On a follower, god-power clicks become
   intents sent to the Owner; on the Owner, the resulting changes become authoritative events.
2. **Authority (Owner):** the Owner applies intents in order, runs the only real simulation,
   and emits events carrying the Owner's own object ids.
3. **Apply (followers):** followers run AI-free and create/move/update/destroy entities
   purely from the stream, so ids match the Owner's everywhere.
4. **Join:** a new player receives the Owner's world as a chunked snapshot on its own
   channel, loads it, then replays every live change made while it was loading.
5. **Cursors:** every client streams its cursor (sequenced); others render a god hand + name.

### Design decisions
- **One authority:** only the Owner simulates, so there is no determinism problem to solve
  and every client agrees on ids.
- **Events first:** changes are sent when they happen; values the game updates in place are
  sent only when they change; a light periodic check keeps everyone aligned.
- **Snapshots only when needed:** on join, on a world change, or on request, never per tick.
- **Owner handoff:** if the Owner leaves, an admin (or the longest-present player) becomes
  the Owner and the world carries on.

## 7. Threading
- **Server:** single thread; all handlers run on the `PollEvents` loop. No locks.
- **Client:** LiteNetLib polled from a MonoBehaviour `Update`, so all packet
  handlers run on the Unity main thread and may touch game/UI objects safely.

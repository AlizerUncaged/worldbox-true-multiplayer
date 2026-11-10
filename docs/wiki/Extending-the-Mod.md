# Extending the Mod

For contributors working on WorldBox Multiplayer itself. Plugin authors want the
[SDK](SDK-Overview) instead.

## Layout

```
src/Shared/   protocol: packets (LiteNetLib INetSerializable), MirrorKind, codecs
src/Server/   relay + accounts + rooms + REST api + admin panel (.NET 10)
src/Client/   the NML mod (net48)
  Mirror/       GameHooks (Harmony), MirrorManager (send/apply), WorldSync (snapshots)
  Net/          NetClient (LiteNetLib), server config
  UI/           multiplayer window, toolbar tab, cursors, HUD
  Api/          WbmpApi (the public SDK)
  Dev/          DEBUG-only test tooling (dev bridge, auto-login)
installer/    WPF one-click installer (net48)
admin-ui/     admin panel front end (Tailwind), built into the server
deploy/       systemd unit, server settings template, deploy script
docs/         ARCHITECTURE.md (design), EVENTS.md (game hook catalog), images
```

## How a change travels

1. A **Harmony hook** on the Owner sees a game change (`GameHooks`), e.g. `Actor.setCulture`.
2. `MirrorManager.OnKing...` encodes it as a `MirrorMessage` with a `MirrorKind` and a
   compact payload (`MirrorCodec`) and sends it.
3. The **server relays** it: Owner messages to every follower; follower messages to the
   Owner only (intents). The server never simulates.
4. On followers, `MirrorManager.OnMirror` gates it (world epoch, snapshot cut-over) and an
   `Apply...` method replays it, with an `_applying...` flag set so the follower's own hooks
   don't echo it back.

## The sync model (protocol WBMP/4)

Nothing is sent unless something changed. Three layers:

1. **Events.** Every discrete change has a hook: spawns, deaths, moves, combat, terrain,
   fire, structural create/remove (cities, kingdoms, wars, cultures, religions, languages,
   clans, families, armies, buildings), memberships, captures. Objects that change in place
   (city territory, culture/language/religion of cities and kingdoms, renames) are marked
   dirty and sent once per frame.
2. **Change detection** for values the game updates in place with no method to hook. The
   Owner sends only what differs from what it last sent: actor vitals as compact scalars;
   `age_overgrowth` (age itself follows the synced clock); actor records as field patches;
   cities, kingdoms and meta objects when their data changes; animation/task state.
3. **Periodic alignment.** Every 30 s the change baselines reset and the Owner sends the
   roster of living actors (followers drop strays and request missing ones). Full actor
   records refresh on a rolling basis, `max(30 s, population / 10)` per actor.

Ordering:
- **Snapshot cut-over:** when the Owner serializes a snapshot it sends a `SnapshotMark` on
  the reliable stream; the receiver buffers later live changes and replays them once the
  snapshot has loaded (loading runs over several frames). A snapshot also resets every
  change baseline. A follower whose game is still starting up holds the snapshot until its
  own world is ready, and asks again if a world never arrives.
- **Spawns go out one frame late** with settled data; a spawn for an actor the follower
  already has is ignored.
- **Memberships that arrive before their target** are retried, and followers re-link each
  actor's culture/religion/language/clan/family/army from its data every second.

Implementation notes:
- LiteNetLib's packet processor reuses one `MirrorMessage` instance: copy before buffering.
- The game's JSON settings use `DefaultValueHandling.IgnoreAndPopulate`: merging a patch with
  them resets every field not in the patch. Patches use their own settings
  (`Include` + `ObjectCreationHandling.Replace`).
- Structural kinds share the `ObjectRemove` namespace with `ActorRemoveKind = 10`; the
  registry refuses colliding kinds.
- Before saving a snapshot, links to removed meta objects are cleared (the game's save code
  can't serialize them).

## Adding something new to sync

**A discrete event (preferred).**
1. Add a `MirrorKind` value (append; never renumber) and a payload struct plus codec in
   `src/Shared/Protocol/MirrorCodec.cs`.
2. Hook the game method in `GameHooks` (postfix), call a new `MirrorManager.OnKingX`.
3. Send only when `Active && IsKing && !_applyingX`.
4. Add the `case` in `OnMirror` and an `ApplyX` that sets `_applyingX` while applying.
5. Bump `NetProtocol.ConnectKey` if old clients would misread the new data.

**A meta system (culture-like objects).** Register it in `BuildObjectRegistry`
(`Reg(kind, ObjType, DataType, manager)`), hook its manager's create/remove methods
(`PatchCreate`/`PatchRemove`) and actor membership (`PatchMember`). Kinds 10 and below
are taken; `ActorRemoveKind = 10` shares the namespace and `Reg` refuses collisions.

**Continuous state with no method to hook.** Use change detection on the Owner: keep what
was last sent and send only differences (see `SendVitals`, the actor record patches, the
structural sweeps). Never send unchanged data on a timer.

**Things that need the follower's world to agree on ids** must arrive after the snapshot:
the cut-over marker handles live messages; don't send world state outside `Send()`.

## Testing with two game instances

1. Run a local server (`./run_server.sh`, Debug clients connect to `127.0.0.1:40441`).
2. Create a `.dev` file in the WorldBox folder. Debug builds then log in as `testp1`,
   `testp2`, ... (one per running instance). Add a line `console` to also open a debug
   console window per instance.
3. Build Debug with `WORLDBOX_DIR` set (it deploys into the game's `Mods` folder), start
   WorldBox twice (Steam, then `worldbox.exe` directly).
4. Each instance serves a **dev bridge** on `http://localhost:8750`, `8751`, ...:

| Endpoint | |
|---|---|
| `/state` | role, room, epoch, actor count, transfer state, cities |
| `/joinroom?id=0001`, `/createroom`, `/leave`, `/logout` | room control |
| `/spawn?asset=human&count=20&x=&y=`, `/speed?n=5`, `/power?id=&x=&y=` | drive the world |
| `/actors?deep=1` | every actor with vitals, age, links and traits (diff Owner vs follower) |
| `/meta` | cities/kingdoms with their culture/language/religion links, all meta objects, per-kind sent/applied/failed counters |
| `/traffic` | bytes and messages sent per `MirrorKind` |
| `/plugin?msg=` | SDK channel round trip (`wbmp.devtest`) |
| `/shot?path=`, `/mpwindow`, `/closewin`, `/console` | screenshots and UI |

A sync check is: pause the Owner (`/speed?n=0`), wait a moment, then compare `/actors?deep=1`
and `/meta` from both instances. They should be identical.

## Builds

```bash
export WORLDBOX_DIR=/path/to/worldbox
dotnet build src/Client/WorldboxMP.Client.csproj -c Debug     # build + deploy to the game
tools/make-release.sh                                          # all release artifacts in out/release
```

.NET telemetry is switched off in every script.

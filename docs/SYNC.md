# Sync Coverage & 1:1 Status

Honest audit of what's mirrored king→followers and how close to 1:1 it is.

## Model
King (room creator) is the **authority + only simulator**. Followers' AI *decisions*
(`Actor.b6_updateAI`) are suppressed, but their movement interpolation + animation +
render keep running so creatures look right and move smoothly. The king broadcasts
events; followers apply them. A one-time **world snapshot on join** transfers the
existing world; everything after is **granular events** (no periodic reload).

## Transport (protocol WBMP/4): events first, change detection where polling fits

Nothing is sent unless something changed. Three layers:

1. **Events (hooks).** Every discrete change has a Harmony hook: spawns, deaths, moves,
   combat, terrain, fire, structural create/remove (cities, kingdoms, wars, cultures,
   religions, languages, clans, families, armies, buildings...), memberships, captures.
   Structural objects that change in place (city territory via `addZone`/`removeZone`,
   culture/language/religion setters on cities and kingdoms, renames) are marked dirty and
   sent once per frame.
2. **Change detection** for state the game mutates in place with no method to hook. The
   owner scans and sends only what differs from what it last sent:
   - actor vitals (health, nutrition, happiness, stamina, mana) as compact scalars, 1 s;
   - `age_overgrowth` only when it changes (age itself is derived from the synced clock);
   - actor records as **field patches** (only changed fields; full record when a field
     resets to default), rotating through the population;
   - cities/kingdoms (2 s) and other meta objects (5 s), sent only if their data changed;
   - animation/task state (100 ms diff scan, set from too many code paths to hook).
3. **Safety net.** Every 30 s the change baselines are reset (objects, vitals, overgrowth)
   and the owner sends an **actor roster**; followers drop stray actors and request missing
   ones. Actor full records refresh on a rolling basis (`max(30 s, population / 10)` per
   actor), so a big world never sends everything in one burst.

Ordering guarantees:
- **Snapshot cut-over.** When the owner serializes a snapshot it sends a `SnapshotMark` on
  the reliable stream. The receiver buffers every later live change and replays it once the
  snapshot has finished loading (the load runs over several frames on `SmoothLoader`), so
  nothing created during a join or resync is lost or applied to the old world. Taking a
  snapshot also resets every change baseline, so the next sends are complete.
- **Spawns are sent one frame late**, with settled data (births get subspecies, city and
  traits right after creation). A spawn for an actor the follower already has is ignored.
- **Memberships that arrive before their target** (culture, clan...) are retried for 30 s,
  and followers re-link every actor's culture/religion/language/clan/family/army from its
  own data once a second.

Pitfalls found the hard way (keep these in mind when touching the apply side):
- LiteNetLib's packet processor reuses one `MirrorMessage` instance: copy before buffering.
- The game's JSON settings use `DefaultValueHandling.IgnoreAndPopulate`; merging a patch with
  them resets every field not in the patch to its `[DefaultValue]`. Patches use their own
  settings (`Include` + `ObjectCreationHandling.Replace`, or lists get appended to).
- Structural kind numbers share the `ObjectRemove` namespace with `ActorRemoveKind = 10`;
  the registry refuses colliding kinds.

Measured on the two-instance harness (x5 speed, owner traffic over 60 s): a ~150-actor,
4-city world went from 93 KB/s to 31-33 KB/s; a ~400-actor, 7-city world runs at 76 KB/s.
Deep comparisons (every actor's vitals, age, city, culture, clan, kingdom, traits and every
meta system) came back identical.

## Granular events mirrored (king → followers)
| Category | Hook | Apply |
|---|---|---|
| Creature spawn (full data) | `ActorManager.createNewUnit` | serialize `ActorData`(+`SubspeciesData`) → `units.loadObject` (same id, color, stats, genes, traits, equipment) |
| Births/reproduction | (routes through `createNewUnit`) | same |
| Movement (per tile) | `Actor.moveTo` (batched/frame) | follower `moveTo` → game animates the walk |
| Death | `Actor.die` | follower `die` |
| Combat health | `Actor.getHit` | set `ActorData.health` |
| Status effects | `addStatusEffect`/`finishStatusEffect` | apply/clear by id |
| Traits | `addTrait`/`removeTrait` | apply/remove |
| Equipment | `ActorEquipment.setItem` | rebuild `ItemData` → `items.loadObject` → equip |
| Rename | `NanoObject.setName` (actors) | `setName` |
| God powers | `PlayerControl.clickedFinal` | intent→king→re-apply→broadcast |
| Terrain/blocks/biome | `MapAction.terraformTile` | `terraformTile` |
| Fire | `WorldBehaviourActionFire.addFire/removeFire` | add/remove |
| Disasters/drops | `DropManager.spawn` (non-god-power) | `drop_manager.spawn` |
| Structural create | `KingdomManager.makeNewCivKingdom`, `CityManager.newCity`, `WarManager.newWar`, `AllianceManager.newAlliance`, `PlotManager.newPlot`, `ReligionManager.newReligion`, `CultureManager.newCulture`, `LanguageManager.newLanguage`, `BuildingManager.addBuilding` | serialize `XData` → `manager.loadObject` |
| Structural destroy | each `manager.removeObject` | `get(id)` → `removeObject` |
| Membership/conversion | `Actor.joinKingdom/setReligion/setCulture/joinLanguage/joinCity` | resolve target by id → call setter |
| City capture | `City.joinAnotherKingdom` | apply |
| Kingdom king/capital | `Kingdom.setKing/setCapital` | apply |
| War end | `WarManager.endWar` | apply |
| World laws | `WorldLaws.setAgeEnabled` | apply (via reflection, field internal) |
| Pause / speed | `Config.paused` change | apply |
| Year / world time | `MapBox.updateObjectAge` (year boundary) + low-rate clock correction (~4s, single scalar) | set `MapStats.world_time` + refresh |
| Cursors | local mouse (20 Hz, sequenced) | God-finger + name, screen-space |

## How close to 1:1?
**Visible, player-driven, and structural state: effectively 1:1** - actions, spawns,
deaths, terrain, fire, kingdoms/cities/wars/etc., year all mirror as events.

**Not perfectly 1:1 (by design / known):**
- **Followers still run non-AI simulation phases** (aging, needs/hunger decay, status
  timers, autonomous fire/plant growth). These tick locally and can drift slightly
  between the authoritative events. Deaths/health/fire are corrected by their events;
  aging visuals may differ briefly. True 1:1 would require making followers fully
  passive (skip the whole sim tick) - but that also kills movement interpolation +
  render, so it needs careful in-game tuning (not done blind).
- **Kingdom color** isn't a granular setter on `Kingdom` (it's on the data) - it
  arrives via `KingdomData` on create/resync, not per recolor.
- **Per-frame creature state** beyond position (e.g. exact animation frame, facing on
  idle) isn't streamed - followers animate locally.

## Safety net
The **Resync** button (room panel) makes the king broadcast a full world snapshot to
everyone, forcing exact 1:1 on demand. Periodic auto-snapshot is **off** (full reload
is too disruptive to run on a timer).

## Next step for true 1:1 (needs live testing)
Make followers fully passive: prefix-skip the world simulation tick on followers while
keeping `u10_checkSmoothMovement` (movement interp) + render running, so the king is the
sole simulator and followers purely replay events. This removes all drift but risks
freezing visuals if the wrong methods are suppressed - must be validated in-game.

# Sync Coverage & 1:1 Status

Honest audit of what's mirrored king→followers and how close to 1:1 it is.

## Model
King (room creator) is the **authority + only simulator**. Followers' AI *decisions*
(`Actor.b6_updateAI`) are suppressed, but their movement interpolation + animation +
render keep running so creatures look right and move smoothly. The king broadcasts
events; followers apply them. A one-time **world snapshot on join** transfers the
existing world; everything after is **granular events** (no periodic reload).

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
**Visible, player-driven, and structural state: effectively 1:1** — actions, spawns,
deaths, terrain, fire, kingdoms/cities/wars/etc., year all mirror as events.

**Not perfectly 1:1 (by design / known):**
- **Followers still run non-AI simulation phases** (aging, needs/hunger decay, status
  timers, autonomous fire/plant growth). These tick locally and can drift slightly
  between the authoritative events. Deaths/health/fire are corrected by their events;
  aging visuals may differ briefly. True 1:1 would require making followers fully
  passive (skip the whole sim tick) — but that also kills movement interpolation +
  render, so it needs careful in-game tuning (not done blind).
- **Kingdom color** isn't a granular setter on `Kingdom` (it's on the data) — it
  arrives via `KingdomData` on create/resync, not per recolor.
- **Per-frame creature state** beyond position (e.g. exact animation frame, facing on
  idle) isn't streamed — followers animate locally.

## Safety net
The **Resync** button (room panel) makes the king broadcast a full world snapshot to
everyone, forcing exact 1:1 on demand. Periodic auto-snapshot is **off** (full reload
is too disruptive to run on a timer).

## Next step for true 1:1 (needs live testing)
Make followers fully passive: prefix-skip the world simulation tick on followers while
keeping `u10_checkSmoothMovement` (movement interp) + render running, so the king is the
sole simulator and followers purely replay events. This removes all drift but risks
freezing visuals if the wrong methods are suppressed — must be validated in-game.

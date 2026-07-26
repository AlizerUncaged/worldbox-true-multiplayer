# WorldBox Event Hook Catalog (for mirroring)

Verified against the real `Assembly-CSharp.dll` (build 0.51.2). Accessibility is the
*true* runtime accessibility (the publicized DLL lies - `internal`/`private` members
need `AccessTools`/reflection, not direct calls). This is the master list we work
through to mirror the whole simulation granularly.

Managers live on `World.world`: `.units .kingdoms .cities .wars .alliances .clans
.families .plot_manager .diplomacy .religions .cultures .languages .subspecies
.buildings .item_manager .drop_manager`, plus `World.world.world_age_manager`,
`World.world.world_laws`.

Status legend: **[done]** mirrored live · **[planned]** to implement · **[snap]** covered
by snapshot resync only (structural object, hard to apply granularly).

## NML pre-made event listeners (subclass the `*Handler`, call `XxxListener.RegisterHandler`)
`NeoModLoader.General.Event.Listeners.*`:
- `ActorTryToAttackListener` → `Actor.tryToAttack`
- `AllianceCreateListener` → `AllianceManager.newAlliance`
- `CityCreateListener` → `City.newCityEvent`
- `ClanCreateListener` → `ClanManager.newClan`
- `CultureCreateListener` → `Culture.createCulture`
- `KingdomSetupListener` → `KingdomManager.makeNewCivKingdom`
- `PlotStartListener` → `PlotManager.newPlot`
- `WarStartListener` → `WarManager.newWar` · `WarEndListener` → `WarManager.endWar`
- `WorldLogMessageListener` → log formatting

## A. Creatures / actors (highest frequency)
- Spawn: `ActorManager.createNewUnit(string,WorldTile,bool,float,Subspecies,Subspecies,bool,bool,bool,bool)` - public - Post. **[done via spawnNewUnit]**
- Spawn (player/sfx): `ActorManager.spawnNewUnit(string,WorldTile,bool,bool,float,Subspecies,bool,bool)` - public - Post. **[done]**
- Death: `Actor.die(bool,AttackType,bool,bool)` - **private** - Post. **[planned]**
- Damage: `Actor.getHit(float,bool,AttackType,BaseSimObject,bool,bool,bool)` - **internal** - Post. [planned]
- Attack: `Actor.tryToAttack(...)` - **internal** (or NML `ActorTryToAttackListener`). [planned]
- Birth: `BabyMaker.makeBaby(Actor,Actor,ActorSex,bool,int,WorldTile,bool,bool)` - public static - Post. [planned]
- Birth (pregnancy/sexual): `BabyMaker.makeBabyFromPregnancy(Actor)`, `makeBabiesViaSexual(...)` - public static. [planned]
- Status effect: `BaseSimObject.addStatusEffect(StatusAsset,float,bool)` / `finishStatusEffect(string)` - internal/public. [planned]
- Trait: `Actor.addTrait(string,bool)` / `removeTrait(string)` - public - Post. [planned]
- Equip: `ActorEquipmentSlot.setItem(Item,Actor)` - internal / `takeAwayItem()` - public. [planned]
- Join kingdom/city: `Actor.joinKingdom(Kingdom)` / `joinCity(City)` - public; `setKingdom` internal. [snap]
- Rename (any): `NanoObject.setName(string,bool)` - public - Post. [planned]

## B. Genes / subspecies / mutation
- `SubspeciesManager.newSpecies(ActorAsset,WorldTile,bool)` - public. `Subspecies.mutateFrom(Subspecies)` - public. `mutateTraits(int)` - internal. [snap]

## C. Kingdoms / cities / clans / families / alliances
- Kingdom: `KingdomManager.makeNewCivKingdom(Actor,string,bool)` public (or `KingdomSetupListener`); `removeObject(Kingdom)` public Pre; `setKing`/`setCapital`/`updateColor` public. [snap]
- City: `City.newCityEvent(Actor)` public (or `CityCreateListener`); `destroyCity()` public Pre; `joinAnotherKingdom(Kingdom,bool,bool)` public Post (capture/rebellion); `setKingdom`/`finishCapture` internal. [snap]
- Clan/Family/Alliance: `ClanManager.newClan`, `FamilyManager.newFamily`, `AllianceManager.newAlliance`/`dissolveAlliance`, `Alliance.join/leave` - public. [snap]

## D. War & diplomacy
- `WarManager.newWar(Kingdom,Kingdom,WarTypeAsset)` public (or `WarStartListener`); `endWar(War,WarWinner)` public (or `WarEndListener`); `War.joinAttackers/joinDefenders/leaveWar/lostWar/setWinner` public. [snap]
- `DiplomacyManager.startWar` internal; `eventSpite/eventFriendship` public. Opinion is computed - don't mirror.

## E. Plots
- `PlotManager.newPlot(Actor,PlotAsset,bool)` public (or `PlotStartListener`); `Plot.finishPlot(PlotState,Actor)` public; `cancelPlot(Plot)` public. [snap]

## F. Religion / culture / language
- `ReligionManager.newReligion`, `CultureManager.newCulture`/`Culture.createCulture` (or `CultureCreateListener`), `LanguageManager.newLanguage` - public; `Actor.setReligion/setCulture/joinLanguage` public. [snap]

## G. Tiles / terrain
- `MapAction.terraformTile(WorldTile,TileType,TopTileType,TerraformOptions,bool)` - public static - Post. **[done]**
- `MapAction.terraformMain/terraformTop`, `WorldTile.setTileType/setTopTileType/setRoad/setBurnedStage` - public. [planned where not covered by terraformTile]

## H. Fire / water / lava
- Fire: `WorldBehaviourActionFire.addFire(WorldTile)` / `removeFire(WorldTile)` - public static - Post. **[planned]**
- Tile fire: `WorldTile.startFire(bool)`/`stopFire()` internal; `setFireData(bool)` public.
- Lava/water: `MapAction.addLava/removeLava/removeLiquid(WorldTile)` - public static. [planned]

## I. Buildings
- `BuildingManager.addBuilding(BuildingAsset,WorldTile,bool,bool,BuildPlacingType)` - **internal** - Post (returns Building); `Building.completeConstruction()` public; `removeBuildingFinal()` internal Pre. [snap]

## J. Items / equipment
- `ItemManager.newItem(EquipmentAsset)` / `generateItem(...)` - public; `removeObject(Item)` public Pre. [snap]

## K. World laws / weather / disasters / time
- `WorldLaws.setAgeEnabled(string,bool)` / `enable(string)` - public. [planned]
- `WorldAgeManager.startNextAge(float)` / `setCurrentSlotIndex(int,float)` - public. [planned]
- Disasters/weather funnel: `DropManager.spawn(WorldTile,DropAsset,float,float,bool,long)` - public - Post. `DisasterLibrary.spawn*`, `DropsLibrary.action_*` public. [planned]

## Top priority implementation order
1. createNewUnit/spawnNewUnit **[done]** · 2. Actor.die **[planned]** · 3. terraformTile **[done]** ·
4. WorldBehaviourActionFire.addFire/removeFire **[planned]** · 5. BabyMaker.makeBaby ·
6. Actor.getHit / tryToAttack · 7. KingdomSetupListener · 8. City.newCityEvent /
joinAnotherKingdom · 9. WarStart/WarEnd · 10. addBuilding · 11. addTrait/removeTrait ·
12. DropManager.spawn (disasters) · 13. setName · 14. WorldLaws · 15. WorldAgeManager.

Structural objects (kingdoms/wars/plots/cities/genes/meta) are kept consistent via the
**snapshot resync** path; per-tile/per-creature events are mirrored live so a full reload
isn't needed for the common visible changes.

# SDK Overview

WorldBox Multiplayer (WBMP) runs **one** real simulation per room, on the room's **Owner**.
Everyone else is a **follower**: their autonomous AI is suppressed and the world streams in
from the Owner. If your mod changes the world on every client independently, the worlds
drift apart. The SDK, one static class called `WbmpApi`, lets your mod play by the same rules.

## The one rule

> World-changing logic runs only on the authority. Followers display the result.

```csharp
using WorldboxMP.Client.Api;

void SpawnMyThing(WorldTile tile)
{
    if (!WbmpApi.IsAuthority) return;   // a follower: the Owner will tell us about it
    // ... actually spawn the thing ...
}
```

`IsAuthority` is true in single player **and** for the room's Owner, so the same check
works everywhere and costs nothing outside multiplayer.

## What syncs on its own

Anything that goes through the game's normal paths is already mirrored, for example:

- units created through `ActorManager.createNewUnit` (and births),
- buildings through `BuildingManager.addBuilding`,
- god powers clicked through `PlayerControl.clickedFinal` (followers send them to the Owner),
- terrain, fire, traits, equipment, kingdoms, cities, cultures, clans, wars and so on.

So most mods only need the authority check. Use a **channel** for state WBMP doesn't know
about: your own data, random outcomes, custom resources, cooldowns.

## Channels

A channel is a named pipe through WBMP's relay. Payloads are your own bytes.

- **Owner sends** -> every follower receives it (`senderIsKing == true`).
- **Follower sends** -> only the Owner receives it (`senderIsKing == false`). Treat it as a
  request: validate it, apply it on the Owner, then `Send` the result so every follower
  (including the one who asked) applies it.

Delivery is reliable and ordered. Keep payloads small and event-shaped ("unit 42 learned
spell X"), not per-frame streams. Messages sent while a follower is still loading the world
are held and delivered once it has loaded.

## Setup

1. Reference `WorldboxMP.Client.dll` from `Mods/WorldBoxMultiplayer/` (compile-only, do
   not ship it with your mod):

   ```xml
   <Reference Include="WorldboxMP.Client">
     <HintPath>$(WorldBoxDir)\Mods\WorldBoxMultiplayer\WorldboxMP.Client.dll</HintPath>
     <Private>false</Private>
   </Reference>
   ```

2. Declare the dependency in your `mod.json` so NeoModLoader loads WBMP first:

   ```json
   "OptionalDependencies": [ "ALIZER_WORLDBOX_MULTIPLAYER" ]
   ```

   Use `Dependencies` instead if your mod requires multiplayer.

3. `using WorldboxMP.Client.Api;`

If WBMP might not be installed, don't touch `WbmpApi` directly: use the
[soft-dependency wrapper](SDK-Examples#soft-dependency-works-with-or-without-wbmp).

Next: [SDK API Reference](SDK-API-Reference), then [SDK Examples](SDK-Examples).

## Versioning

`WbmpApi` is the stable surface. Everything else in the DLL is internal and can change
between versions (release builds are also obfuscated). If you need something the API doesn't
expose, open an issue; extending the API is cheap.

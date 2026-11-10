# SDK Examples

All examples are NeoModLoader mods (`BasicMod<T>`). They assume the [setup](SDK-Overview#setup)
from the overview.

## Minimal: an authority-only spawner

The vanilla spawn path is already mirrored, so the only job is to make sure it runs once,
on the Owner.

```csharp
using NeoModLoader.api;
using WorldboxMP.Client.Api;

public class SheepRain : BasicMod<SheepRain>
{
    protected override void OnModLoad() { }

    // Call this from your god power, button, timer...
    public static void RainSheep(WorldTile center)
    {
        if (!WbmpApi.IsAuthority) return;          // followers get the sheep via WBMP's spawn sync
        for (int i = 0; i < 10; i++)
            World.world.units.createNewUnit("sheep", center);
    }
}
```

## Random results: decide on the Owner, broadcast the outcome

Anything random must be rolled once (on the Owner) and the result sent, or every client
rolls something different.

```csharp
using System.IO;
using NeoModLoader.api;
using UnityEngine;
using WorldboxMP.Client.Api;

public class LuckyBlessing : BasicMod<LuckyBlessing>
{
    const string Channel = "luckyblessing.result";   // "<modid>.<what>"

    protected override void OnModLoad()
    {
        WbmpApi.RegisterChannel(Channel, (payload, fromOwner) =>
        {
            if (!fromOwner) return;                   // only the Owner decides outcomes
            using (var r = new BinaryReader(new MemoryStream(payload)))
                ApplyBlessing(r.ReadInt64(), r.ReadString());
        });
    }

    public static void Bless(Actor target)
    {
        if (!WbmpApi.IsAuthority) return;
        string trait = Random.value < 0.5f ? "lucky" : "blessed";   // rolled once, here
        ApplyBlessing(target.id, trait);

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) { w.Write(target.id); w.Write(trait); }
        WbmpApi.Send(Channel, ms.ToArray());          // no-op outside a room
    }

    static void ApplyBlessing(long actorId, string trait)
    {
        var a = World.world.units.get(actorId);
        if (a != null && a.isAlive()) a.addTrait(trait);
    }
}
```

(Trait changes are also mirrored by WBMP itself; this shows the pattern for state that
isn't.)

## Player requests: followers ask, the Owner decides

A button in your mod's UI that any player can press. Followers send a request; the Owner
validates, applies and broadcasts.

```csharp
const string Req = "mymod.summon";

void OnLoad()
{
    WbmpApi.RegisterChannel(Req, (payload, fromOwner) =>
    {
        var (x, y) = Decode(payload);
        if (fromOwner) { Summon(x, y); return; }                      // follower: result
        if (WbmpApi.IsKing && Allowed(x, y)) { Summon(x, y); WbmpApi.Send(Req, payload); } // Owner: request
    });
}

void OnButtonPressed(int x, int y)
{
    if (WbmpApi.IsAuthority) { Summon(x, y); WbmpApi.Send(Req, Encode(x, y)); }
    else WbmpApi.Send(Req, Encode(x, y));   // ask the Owner
}
```

## Drifting state: a small periodic resync

For values that change all the time (custom resources, cooldowns), have the Owner send a
compact snapshot every few seconds instead of hooking every change.

```csharp
float _timer;
void Update()   // in a MonoBehaviour
{
    if (!WbmpApi.IsKing) return;
    _timer += Time.unscaledDeltaTime;
    if (_timer < 3f) return;
    _timer = 0f;
    WbmpApi.Send("mymod.mana_pool", EncodeManaPool());
}
```

## Your own Harmony hooks

If you patch a game method that WBMP also replays on followers, check `ApplyingRemote` to
tell a replayed call from a real local one:

```csharp
static void MyPostfix(Actor __instance)
{
    if (WbmpApi.ApplyingRemote) return;   // WBMP is replaying the Owner's action
    // ... your local-only reaction ...
}
```

## Soft dependency: works with or without WBMP

If WBMP may not be installed, don't reference `WbmpApi` directly (your mod would fail to
load without the DLL). Find it by reflection once and fall back to single-player behavior:

```csharp
using System;
using System.Linq;
using System.Reflection;

static class Mp
{
    static readonly Type Api = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("WorldboxMP.Client.Api.WbmpApi", false))
        .FirstOrDefault(t => t != null);

    static bool Get(string name) =>
        Api != null && (bool)Api.GetProperty(name, BindingFlags.Public | BindingFlags.Static).GetValue(null);

    public static bool Installed => Api != null;
    public static bool IsAuthority => Api == null || Get("IsAuthority");
    public static bool InRoom => Get("InRoom");
}
```

Call `Mp.IsAuthority` wherever you would call `WbmpApi.IsAuthority`. Resolve the type after
all mods have loaded (for example on first use), and declare WBMP in
`OptionalDependencies` so it loads first when present.

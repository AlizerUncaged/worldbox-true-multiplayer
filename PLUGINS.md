# Making your mod compatible with WorldBox Multiplayer

> The full, up-to-date guide lives in the wiki:
> [SDK Overview](https://github.com/AlizerUncaged/worldbox-true-multiplayer/wiki/SDK-Overview),
> [API Reference](https://github.com/AlizerUncaged/worldbox-true-multiplayer/wiki/SDK-API-Reference),
> [Examples](https://github.com/AlizerUncaged/worldbox-true-multiplayer/wiki/SDK-Examples).

WorldBox Multiplayer (WBMP) runs one authoritative simulation on the room's **Owner**
(called "King" in the API); every other player is a **follower** that mirrors it (follower
unit AI is suppressed and world changes stream in from the Owner). If your mod changes the world on every client
independently, the worlds drift apart. This guide shows how to make it sync instead.

Everything you need is one public class: `WorldboxMP.Client.Api.WbmpApi`.

## Setup

1. Add a reference to `WorldboxMP.Client.dll` (ships in `Mods/WorldBoxMultiplayer/`).
2. `using WorldboxMP.Client.Api;`

That's it. If WBMP isn't installed, don't reference the DLL; if you want an optional
(soft) dependency, wrap your WBMP calls behind a try/catch or reflection.

## The one rule

> World-changing logic runs only on the authority. Followers just display the result.

```csharp
void SpawnMyCustomThing(WorldTile tile)
{
    if (!WbmpApi.IsAuthority) return;   // follower: the Owner will tell us about it
    // ... actually spawn the thing ...
}
```

`IsAuthority` is `true` in single player AND when you are the room's Owner, so the same
check works everywhere and costs nothing outside multiplayer.

## Role and session state

| Member | Meaning |
|---|---|
| `WbmpApi.InRoom` | connected and inside a multiplayer room |
| `WbmpApi.IsAuthority` | single player, or the room's Owner - run your sim logic when true |
| `WbmpApi.IsKing` | in a room and this client is the Owner |
| `WbmpApi.IsFollower` | in a room, mirroring someone else's world |
| `WbmpApi.ApplyingRemote` | true while WBMP is applying a synced action; use in your Harmony hooks to skip re-broadcasting replayed actions |
| `WbmpApi.Username`, `WbmpApi.RoomId`, `WbmpApi.Players` | session info |

Lifecycle events: `WbmpApi.RoomJoined`, `WbmpApi.RoomLeft`, `WbmpApi.RosterChanged`.

## Syncing your own state: channels

A channel is a named pipe through WBMP's relay. Payloads are your own bytes; WBMP never
looks inside them.

```csharp
const string CH = "mymod.thing_spawned";   // unique id: "<modid>.<what>"

void Awake()
{
    WbmpApi.RegisterChannel(CH, (payload, senderIsKing) =>
    {
        // Followers receive what the king broadcast; the king receives follower intents.
        if (senderIsKing) ApplyThingFromBytes(payload);
        else if (WbmpApi.IsKing) HandlePlayerRequest(payload);
    });
}

// King: after actually spawning, tell everyone.
void OnThingSpawned(int x, int y)
{
    if (WbmpApi.IsKing) WbmpApi.Send(CH, EncodeThing(x, y));
}
```

Routing follows WBMP's king-authoritative model:

- **King sends** -> every follower receives it (`senderIsKing == true` on their side).
- **Follower sends** -> only the king receives it (`senderIsKing == false`). Treat it as
  a request: validate, apply on the king, then `Send` the authoritative result so all
  followers (including the requester) apply it.

Delivery is reliable and ordered. Keep payloads small and event-shaped (like "unit X
gained ability Y"), not per-frame streams.

## Practical patterns

- **Custom units/buildings**: spawn only when `IsAuthority`. WBMP already mirrors
  `ActorManager.createNewUnit` and `BuildingManager.addBuilding`, so vanilla-pathway
  spawns sync automatically; use a channel only for state WBMP doesn't know about.
- **Custom god powers**: powers that route through `PlayerControl.clickedFinal` are
  already mirrored (followers replay the click). Guard any EXTRA side effects with
  `IsAuthority` and sync results through a channel if they are random.
- **Periodic state**: if your mod has drifting values (custom resources, cooldowns),
  have the king `Send` a small resync every few seconds instead of hooking everything.
- **Your own Harmony hooks**: check `WbmpApi.ApplyingRemote` to tell a replayed action
  from a genuine local one, otherwise you may double-apply or echo actions.

## Versioning

`WbmpApi` is the stable surface; internal classes can change without notice. If you need
something the API doesn't expose, open an issue - extending the API is cheap.

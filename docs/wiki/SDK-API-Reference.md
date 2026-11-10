# SDK API Reference

Namespace `WorldboxMP.Client.Api`, class `public static class WbmpApi`.

The names use "King" for the room's **Owner** (the API predates the rename in the UI).

## Role and session

| Member | Type | Meaning |
|---|---|---|
| `InRoom` | `bool` | Connected and inside a room. |
| `IsAuthority` | `bool` | Single player, or the room's Owner. Run world-changing logic only when true. |
| `IsKing` | `bool` | In a room and this client is the Owner. |
| `IsFollower` | `bool` | In a room, mirroring the Owner's world. |
| `ApplyingRemote` | `bool` | True while WBMP is applying a synced action. Check it in your own Harmony hooks so a replayed action isn't treated (and re-sent) as a local one. |
| `Username` | `string` | Logged-in username, `""` when not logged in. |
| `RoomId` | `string` | Current room code, `""` when not in a room. |
| `Players` | `IReadOnlyList<string>` | Usernames in the room. |

## Events

| Event | When |
|---|---|
| `RoomJoined` | After this client joins a room (as Owner or follower). |
| `RoomLeft` | After this client leaves a room for any reason: leaving, kicked, logout, or connection lost. Raised once. |
| `RosterChanged` | The room's player list or roles changed (only while in a room). |

Handlers run on Unity's main thread; exceptions are caught and logged so one bad handler
can't break the mod.

## Channels

```csharp
public delegate void ChannelHandler(byte[] payload, bool senderIsKing);

public static void RegisterChannel(string channelId, ChannelHandler handler);
public static void Send(string channelId, byte[] payload);
```

**`RegisterChannel(channelId, handler)`** registers a handler for a channel. Use a unique
id such as `"mymod.units"`. Registering the same id again replaces the handler; passing
`null` unregisters it. Register once at load.

**`Send(channelId, payload)`** sends bytes on a channel. It does nothing when not in a room.

| Sender | Who receives | `senderIsKing` on the receiver |
|---|---|---|
| Owner | every follower | `true` |
| Follower | the Owner only | `false` |

A sender never receives its own message. To act on your own change, apply it locally
before (or after) sending.

### Handler pattern

```csharp
WbmpApi.RegisterChannel("mymod.thing", (payload, senderIsKing) =>
{
    if (senderIsKing)
    {
        ApplyThing(payload);                 // follower: the Owner's authoritative result
    }
    else if (WbmpApi.IsKing && IsValidRequest(payload))
    {
        ApplyThing(payload);                 // Owner: run it for real
        WbmpApi.Send("mymod.thing", payload); // then tell every follower
    }
});
```

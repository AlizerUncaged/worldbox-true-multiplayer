using System;
using System.Collections.Generic;
using WorldboxMP.Client.Mirror;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.Api
{
    /// <summary>
    /// Public integration surface for OTHER mods ("plugins") that want to be
    /// multiplayer-compatible. Reference WorldboxMP.Client.dll and use this class only;
    /// everything else in the mod is an implementation detail that may change.
    ///
    /// Model recap: one player is the KING (authoritative simulation), everyone else is
    /// a FOLLOWER (visual mirror whose autonomous AI is suppressed). A well-behaved
    /// plugin runs its world-changing logic only on the king and ships the results to
    /// followers over a named channel.
    ///
    /// See PLUGINS.md in the repo root for a full guide with examples.
    /// </summary>
    public static class WbmpApi
    {
        // ── Session / role state ────────────────────────────────────────────
        /// <summary>True while connected to a server and inside a room.</summary>
        public static bool InRoom => NetClient.Instance != null && NetClient.Instance.InRoom
                                     && NetClient.Instance.State == ConnState.Connected;

        /// <summary>True when this client is the room's God King (authoritative sim).
        /// Also true when NOT in multiplayer at all - single player is authoritative -
        /// so "if (WbmpApi.IsAuthority)" is the one check plugins usually need.</summary>
        public static bool IsAuthority => !InRoom || NetClient.Instance.IsKing;

        /// <summary>True when this client is the king of a room.</summary>
        public static bool IsKing => InRoom && NetClient.Instance.IsKing;

        /// <summary>True when this client mirrors someone else's world.</summary>
        public static bool IsFollower => InRoom && !NetClient.Instance.IsKing;

        /// <summary>True while this client is applying a remote (synced) action. Use it in
        /// your own Harmony hooks to tell replayed actions apart from local ones.</summary>
        public static bool ApplyingRemote => MirrorManager.Instance != null && MirrorManager.Instance.ApplyingRemote;

        /// <summary>Logged-in username ("" when not logged in).</summary>
        public static string Username => NetClient.Instance?.Username ?? "";

        /// <summary>Current room code ("" when not in a room).</summary>
        public static string RoomId => NetClient.Instance?.RoomId ?? "";

        /// <summary>Usernames of everyone in the room (empty when not in a room).</summary>
        public static IReadOnlyList<string> Players
        {
            get
            {
                var list = new List<string>();
                var roster = NetClient.Instance?.Roster;
                if (roster != null) foreach (var p in roster) list.Add(p.Username);
                return list;
            }
        }

        // ── Lifecycle events ────────────────────────────────────────────────
        /// <summary>Fired after this client joins a room (king or follower).</summary>
        public static event Action RoomJoined;
        /// <summary>Fired after this client leaves a room / disconnects.</summary>
        public static event Action RoomLeft;
        /// <summary>Fired whenever the player roster changes.</summary>
        public static event Action RosterChanged;

        // ── Plugin channels ─────────────────────────────────────────────────
        /// <summary>Handler for channel messages. senderIsKing tells you whether the
        /// payload came from the authoritative side.</summary>
        public delegate void ChannelHandler(byte[] payload, bool senderIsKing);

        private static readonly Dictionary<string, ChannelHandler> _channels =
            new Dictionary<string, ChannelHandler>(StringComparer.Ordinal);

        /// <summary>
        /// Register a named channel (use a unique id, e.g. "mymod.units"). Messages sent
        /// on the channel by other clients arrive at your handler. Registering again
        /// replaces the handler; pass null to unregister.
        /// </summary>
        public static void RegisterChannel(string channelId, ChannelHandler handler)
        {
            if (string.IsNullOrEmpty(channelId)) throw new ArgumentException("channelId is required");
            if (handler == null) _channels.Remove(channelId);
            else _channels[channelId] = handler;
        }

        /// <summary>
        /// Send a payload on a channel. Routing follows the king-authoritative relay:
        /// from the king it broadcasts to every follower; from a follower it goes to the
        /// king only (an "intent" - the king decides, then broadcasts the result).
        /// No-op when not in a room.
        /// </summary>
        public static void Send(string channelId, byte[] payload)
        {
            if (!InRoom || string.IsNullOrEmpty(channelId)) return;
            NetClient.Instance.SendMirror(MirrorKind.Plugin,
                MirrorCodec.Write(new PluginMsg { Channel = channelId, Data = payload }),
                Mirror.MirrorManager.Instance?.WorldEpoch ?? (byte)0);
        }

        // ── Internal wiring (called by the mod, not by plugins) ─────────────
        internal static void WireInternal()
        {
            var net = NetClient.Instance;
            if (net == null) return;
            net.OnRoomJoined += _ => SafeInvoke(RoomJoined);
            net.OnRosterChanged += () => { if (net.InRoom) SafeInvoke(RosterChanged); };
            // One exit path: voluntary leave, kick, logout and connection loss all raise OnRoomLeft once.
            net.OnRoomLeft += _ => SafeInvoke(RoomLeft);
        }

        internal static void DispatchInternal(PluginMsg m, bool senderIsKing)
        {
            if (m.Channel == null) return;
            if (_channels.TryGetValue(m.Channel, out var h))
            {
                try { h(m.Data ?? Array.Empty<byte>(), senderIsKing); }
                catch (Exception e) { ModLog.Warn($"[plugin] channel '{m.Channel}' handler threw: " + e.Message); }
            }
        }

        private static void SafeInvoke(Action a)
        {
            try { a?.Invoke(); } catch (Exception e) { ModLog.Warn("[plugin] event handler threw: " + e.Message); }
        }
    }
}

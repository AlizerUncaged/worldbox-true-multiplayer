using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using WorldboxMP.Server.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server.Rooms
{
    /// <summary>Room limits and the server's permanent lobbies (from appsettings / env).</summary>
    public class RoomSettings
    {
        /// <summary>Names of the always-open server lobbies, in order: 0000, 0001, ...</summary>
        public List<string> Lobbies { get; set; } = new() { "Lobby" };

        /// <summary>Member cap for every room (lobbies and player rooms). 0 = unlimited.</summary>
        public int MaxPlayersPerRoom { get; set; } = 16;

        /// <summary>Cap on player-created rooms (lobbies don't count). 0 = unlimited.</summary>
        public int MaxRooms { get; set; } = 200;
    }

    /// <summary>
    /// Owns every live room. Single-threaded (driven from the network poll loop).
    /// A server hosts any number of permanent lobbies (configured, never removed) plus
    /// player-created rooms (collision-free 4-char codes, removed when empty).
    /// Handles joins, leaves with king succession, the public listing, and moderation.
    /// </summary>
    public class RoomManager
    {
        private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
        private readonly NetPacketProcessor _proc;
        private readonly Action<string> _log;
        private readonly RoomSettings _settings;

        public RoomManager(NetPacketProcessor proc, Action<string> log, RoomSettings settings)
        {
            _proc = proc;
            _log = log;
            _settings = settings ?? new RoomSettings();

            var names = _settings.Lobbies?.Select(n => SafeText.Clean(n, 40)).Where(n => n.Length > 0).ToList();
            if (names == null || names.Count == 0) names = new List<string> { "Lobby" };
            for (int i = 0; i < names.Count && i < 100; i++)
            {
                var lobby = new Room(i.ToString("D4"), names[i], _settings.MaxPlayersPerRoom, permanent: true)
                    { Visibility = RoomVisibility.Public };
                _rooms[lobby.ShareableId] = lobby;
                _log($"Lobby {lobby.ShareableId} ('{lobby.Name}') open.");
            }
        }

        public Room Find(string shareableId) =>
            shareableId != null && _rooms.TryGetValue(shareableId.Trim(), out var r) ? r : null;

        public int PlayerRoomCount => _rooms.Values.Count(r => !r.IsPermanent);

        public IEnumerable<Room> All => _rooms.Values;

        /// <summary>Admin: empty a room (everyone gets <paramref name="reason"/>). Player
        /// rooms disappear once empty; lobbies stay open.</summary>
        public void CloseRoom(Room room, string reason)
        {
            foreach (var m in room.Members.ToList())
                LeaveRoom(m.Session, notify: true, reason: reason);
        }

        /// <summary>Random 4-char code that is never all digits (digits-only ids are
        /// reserved for lobbies, so a lobby added later can't collide with a live room).</summary>
        private string GenerateUniqueId()
        {
            string id;
            do { id = ShareableId.Generate(NetProtocol.ShareableIdLength); }
            while (_rooms.ContainsKey(id) || id.All(char.IsDigit));
            return id;
        }

        // ── Create ────────────────────────────────────────────────────────
        public Room CreateRoom(Session session, string name, string password, RoomVisibility visibility, out string error)
        {
            error = null;
            if (password != null && password.Length > NetProtocol.MaxPasswordLength)
            {
                error = $"Password too long (max {NetProtocol.MaxPasswordLength}).";
                return null;
            }
            if (_settings.MaxRooms > 0 && PlayerRoomCount >= _settings.MaxRooms)
            {
                error = "The server is at its room limit - join a lobby or try later.";
                return null;
            }
            if (visibility != RoomVisibility.Public && visibility != RoomVisibility.Unlisted)
                visibility = RoomVisibility.Public;

            // Creator leaves any current room first.
            LeaveRoom(session, notify: false);

            name = SafeText.Clean(name, 40);
            if (name.Length == 0) name = $"{session.Username}'s world";

            var room = new Room(GenerateUniqueId(), name, _settings.MaxPlayersPerRoom)
                { Visibility = visibility, KingId4 = session.Id4 };
            room.SetPassword(password);
            _rooms[room.ShareableId] = room;

            room.Add(session, RoomRole.King);
            session.CurrentRoom = room;

            Note(room, $"{session.Username} created room {room.ShareableId} ('{room.Name}', {visibility}, pw={room.HasPassword}).");
            PushRoster(room);
            return room;
        }

        // ── Join ──────────────────────────────────────────────────────────
        public Room JoinRoom(Session session, string shareableId, string password, out string error)
        {
            error = null;
            var room = Find(shareableId);
            if (room == null) { error = "Room not found - check the code."; return null; }
            if (room.Contains(session.Id4)) return room; // already in it
            if (!room.CheckPassword(password))
            {
                error = string.IsNullOrEmpty(password) ? "This room needs a password." : "Wrong password.";
                return null;
            }
            if (room.IsFull) { error = $"Room is full ({room.MaxPlayers} players)."; return null; }

            LeaveRoom(session, notify: false);

            // First player into an empty room with no king (a lobby) holds its world.
            RoomRole role = (room.KingId4 == null && room.IsEmpty) ? RoomRole.King : RoomRole.Player;
            if (role == RoomRole.King) room.KingId4 = session.Id4;

            room.Add(session, role);
            session.CurrentRoom = room;

            Note(room, $"{session.Username} joined room {room.ShareableId} as {role}.");
            PushRoster(room);
            return room;
        }

        // ── Leave (with king succession) ───────────────────────────────────
        /// <param name="notify">Tell the leaving client with a <see cref="RoomLeft"/>
        /// (false when it is switching rooms or already gone).</param>
        /// <param name="reason">Shown to the leaving player (empty = voluntary).</param>
        public void LeaveRoom(Session session, bool notify = true, string reason = "")
        {
            var room = session.CurrentRoom;
            if (room == null) return;

            bool wasKing = room.KingId4 == session.Id4;
            room.Remove(session.Id4);
            if (!room.IsEmpty) Note(room, $"{session.Username} left room {room.ShareableId}.");
            session.CurrentRoom = null;
            if (notify && session.Peer.ConnectionState == LiteNetLib.ConnectionState.Connected)
                session.Peer.Send(_proc, new RoomLeft { ShareableId = room.ShareableId, Reason = reason ?? "" });

            if (room.IsEmpty)
            {
                if (room.KingId4 == session.Id4) room.KingId4 = null;
                if (!room.IsPermanent)
                {
                    _rooms.Remove(room.ShareableId);
                    _log($"Room {room.ShareableId} removed (empty).");
                }
                return;
            }

            // Succession: promote an existing admin, otherwise the longest-standing player.
            if (wasKing)
            {
                var heir = room.Members.FirstOrDefault(m => m.Role == RoomRole.Admin)
                           ?? room.Members.First();
                heir.Role = RoomRole.King;
                room.KingId4 = heir.Id4;
                Note(room, $"Room {room.ShareableId}: owner left; {heir.Username} is the new owner.");
            }

            PushRoster(room);
        }

        // ── Moderation ─────────────────────────────────────────────────────
        public bool Moderate(Session actor, byte action, string targetId4, out string error)
        {
            error = null;
            var room = actor.CurrentRoom;
            if (room == null) { error = "You are not in a room."; return false; }

            var me = room.Get(actor.Id4);
            var target = room.Get(targetId4);
            if (me == null || target == null) { error = "Target not found."; return false; }
            if (me.Id4 == target.Id4) { error = "You cannot target yourself."; return false; }
            if (me.Role == RoomRole.Player) { error = "You are not an admin."; return false; }

            // Admins cannot act on other admins or the king; only the king can.
            if (me.Role == RoomRole.Admin && target.Role != RoomRole.Player)
            {
                error = "Only the room owner can moderate admins.";
                return false;
            }

            switch (action)
            {
                case ModerationRequest.ACTION_KICK:
                    LeaveRoom(target.Session, notify: true, reason: "Kicked by " + me.Username);
                    Note(room, $"{me.Username} kicked {target.Username} from {room.ShareableId}.");
                    break;

                case ModerationRequest.ACTION_PROMOTE:
                    if (me.Role != RoomRole.King) { error = "Only the room owner can promote."; return false; }
                    if (target.Role == RoomRole.Player) target.Role = RoomRole.Admin;
                    Note(room, $"{me.Username} promoted {target.Username} to admin.");
                    PushRoster(room);
                    break;

                case ModerationRequest.ACTION_DEMOTE:
                    if (me.Role != RoomRole.King) { error = "Only the room owner can demote."; return false; }
                    if (target.Role == RoomRole.Admin) target.Role = RoomRole.Player;
                    Note(room, $"{me.Username} demoted {target.Username} to player.");
                    PushRoster(room);
                    break;

                default:
                    error = "Unknown moderation action.";
                    return false;
            }
            return true;
        }

        /// <summary>Console + the room's own log (readable in-game by its players).</summary>
        private void Note(Room room, string text)
        {
            _log(text);
            room.AddLog(new LogEntry { UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Text = text });
        }

        // ── Listing & roster push ──────────────────────────────────────────
        /// <summary>Lobbies first (in configured order), then public rooms by size.</summary>
        public List<RoomSummary> ListPublic()
        {
            return _rooms.Values
                .Where(r => r.Visibility == RoomVisibility.Public)
                .OrderByDescending(r => r.IsPermanent)
                .ThenBy(r => r.IsPermanent ? r.ShareableId : "")
                .ThenByDescending(r => r.Count)
                .Take(200)
                .Select(r => r.BuildSummary())
                .ToList();
        }

        public void PushRoster(Room room)
        {
            var roster = room.BuildRoster();
            room.Broadcast(_proc, roster);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using WorldboxMP.Server.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server.Rooms
{
    /// <summary>
    /// Owns every live room. Single-threaded (driven from the network poll loop).
    /// Handles creation with collision-free shareable ids, joins, leaves with king
    /// succession, the public listing, and moderation.
    /// </summary>
    public class RoomManager
    {
        private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
        private readonly NetPacketProcessor _proc;
        private readonly Action<string> _log;

        public RoomManager(NetPacketProcessor proc, Action<string> log)
        {
            _proc = proc;
            _log = log;
            // Room 0 is always present and permanent.
            var room0 = new Room(NetProtocol.Room0Id, "Lobby", permanent: true) { Visibility = RoomVisibility.Public };
            _rooms[room0.ShareableId] = room0;
            _log($"Created permanent room {room0.ShareableId} ('{room0.Name}').");
        }

        public Room Find(string shareableId) =>
            shareableId != null && _rooms.TryGetValue(shareableId, out var r) ? r : null;

        private string GenerateUniqueId()
        {
            string id;
            do { id = ShareableId.Generate(NetProtocol.ShareableIdLength); }
            while (_rooms.ContainsKey(id));
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

            // Creator leaves any current room first.
            LeaveRoom(session);

            name = string.IsNullOrWhiteSpace(name) ? $"{session.Username}'s room" : name.Trim();
            if (name.Length > 40) name = name.Substring(0, 40);

            var room = new Room(GenerateUniqueId(), name) { Visibility = visibility, KingId4 = session.Id4 };
            room.SetPassword(password);
            _rooms[room.ShareableId] = room;

            room.Add(session, RoomRole.King);
            session.CurrentRoom = room;

            _log($"{session.Username} created room {room.ShareableId} ('{room.Name}', {visibility}, pw={room.HasPassword}).");
            PushRoster(room);
            return room;
        }

        // ── Join ──────────────────────────────────────────────────────────
        public Room JoinRoom(Session session, string shareableId, string password, out string error)
        {
            error = null;
            var room = Find(shareableId);
            if (room == null) { error = "Room not found."; return null; }
            if (room.Contains(session.Id4)) return room; // already in it
            if (!room.CheckPassword(password)) { error = "Wrong password."; return null; }

            LeaveRoom(session);

            // First player to enter an unclaimed permanent room (e.g. room 0) becomes its king.
            RoomRole role = (room.KingId4 == null && room.IsEmpty) ? RoomRole.King : RoomRole.Player;
            if (role == RoomRole.King) room.KingId4 = session.Id4;

            room.Add(session, role);
            session.CurrentRoom = room;

            _log($"{session.Username} joined room {room.ShareableId} as {role}.");
            PushRoster(room);
            return room;
        }

        // ── Leave (with king succession) ───────────────────────────────────
        public void LeaveRoom(Session session)
        {
            var room = session.CurrentRoom;
            if (room == null) return;

            bool wasKing = room.KingId4 == session.Id4;
            room.Remove(session.Id4);
            session.CurrentRoom = null;

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
                _log($"Room {room.ShareableId}: king left; {heir.Username} is the new king.");
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
                error = "Only the king can moderate admins.";
                return false;
            }

            switch (action)
            {
                case ModerationRequest.ACTION_KICK:
                    KickFromRoom(room, target, "Kicked by " + me.Username);
                    _log($"{me.Username} kicked {target.Username} from {room.ShareableId}.");
                    break;

                case ModerationRequest.ACTION_PROMOTE:
                    if (me.Role != RoomRole.King) { error = "Only the king can promote."; return false; }
                    if (target.Role == RoomRole.Player) target.Role = RoomRole.Admin;
                    _log($"{me.Username} promoted {target.Username} to admin.");
                    PushRoster(room);
                    break;

                case ModerationRequest.ACTION_DEMOTE:
                    if (me.Role != RoomRole.King) { error = "Only the king can demote."; return false; }
                    if (target.Role == RoomRole.Admin) target.Role = RoomRole.Player;
                    _log($"{me.Username} demoted {target.Username} to player.");
                    PushRoster(room);
                    break;

                default:
                    error = "Unknown moderation action.";
                    return false;
            }
            return true;
        }

        private void KickFromRoom(Room room, Member target, string reason)
        {
            var session = target.Session;
            target.Session.Peer.Send(_proc, new RoomError { Message = reason });
            // Reuse LeaveRoom for clean succession/empty handling.
            LeaveRoom(session);
        }

        // ── Listing & roster push ──────────────────────────────────────────
        public List<RoomSummary> ListPublic()
        {
            return _rooms.Values
                .Where(r => r.Visibility == RoomVisibility.Public)
                .OrderByDescending(r => r.Count)
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

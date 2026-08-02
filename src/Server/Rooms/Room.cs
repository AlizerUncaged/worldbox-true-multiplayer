using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using WorldboxMP.Server.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server.Rooms
{
    /// <summary>A single player's membership inside a room.</summary>
    public class Member
    {
        public Session Session { get; }
        public RoomRole Role { get; set; }

        public Member(Session session, RoomRole role) { Session = session; Role = role; }

        public string Id4 => Session.Id4;
        public string Username => Session.Username;
    }

    /// <summary>
    /// A live game room. Holds its members, hierarchy, and password/visibility.
    /// Rooms are in-memory (not persisted); only player accounts live in Mongo.
    /// All access happens on the single network poll thread, so no locking.
    /// </summary>
    public class Room
    {
        public string ShareableId { get; }
        public string Name { get; set; }
        public RoomVisibility Visibility { get; set; }

        /// <summary>SHA-256 hex of the password, or null if the room has none.</summary>
        public string PasswordHash { get; private set; }
        public bool HasPassword => PasswordHash != null;

        /// <summary>id4 of the king (creator). May be null for the persistent room 0 until claimed.</summary>
        public string KingId4 { get; set; }

        /// <summary>Room 0 is permanent and is never auto-removed when empty.</summary>
        public bool IsPermanent { get; }

        private readonly Dictionary<string, Member> _members = new();

        public Room(string shareableId, string name, bool permanent = false)
        {
            ShareableId = shareableId;
            Name = name;
            IsPermanent = permanent;
        }

        public IReadOnlyCollection<Member> Members => _members.Values;
        public int Count => _members.Count;
        public bool IsEmpty => _members.Count == 0;

        public void SetPassword(string plaintext)
        {
            PasswordHash = string.IsNullOrEmpty(plaintext) ? null : Hash(plaintext);
        }

        public bool CheckPassword(string plaintext)
        {
            if (!HasPassword) return true;
            return PasswordHash == Hash(plaintext ?? "");
        }

        public Member Add(Session session, RoomRole role)
        {
            var member = new Member(session, role);
            _members[session.Id4] = member;
            return member;
        }

        public void Remove(string id4) => _members.Remove(id4);

        public Member Get(string id4) => _members.TryGetValue(id4, out var m) ? m : null;

        /// <summary>The current king member, or null.</summary>
        public Member GetKing() => KingId4 != null ? Get(KingId4) : null;

        public bool Contains(string id4) => _members.ContainsKey(id4);

        public RoomRoster BuildRoster()
        {
            var roster = new RoomRoster { ShareableId = ShareableId };
            foreach (var m in _members.Values)
                roster.Players.Add(new PlayerInfo { Id4 = m.Id4, Username = m.Username, Role = (byte)m.Role });
            return roster;
        }

        public RoomSummary BuildSummary() => new RoomSummary
        {
            ShareableId = ShareableId,
            Name = Name,
            PlayerCount = Count,
            HasPassword = HasPassword,
        };

        /// <summary>Send a packet to every member of the room.</summary>
        public void Broadcast<T>(NetPacketProcessor proc, T packet,
            DeliveryMethod method = DeliveryMethod.ReliableOrdered) where T : INetSerializable
        {
            var writer = new NetDataWriter();
            proc.WriteNetSerializable(writer, ref packet);
            foreach (var m in _members.Values)
                m.Session.Peer.Send(writer, method);
        }

        /// <summary>Send a packet to every member except one (by id4).</summary>
        public void BroadcastExcept<T>(NetPacketProcessor proc, string exceptId4, T packet,
            DeliveryMethod method = DeliveryMethod.ReliableOrdered) where T : INetSerializable
        {
            var writer = new NetDataWriter();
            proc.WriteNetSerializable(writer, ref packet);
            foreach (var m in _members.Values)
                if (m.Id4 != exceptId4)
                    m.Session.Peer.Send(writer, method);
        }

        /// <summary>Send a packet to every member except one, on a specific channel.</summary>
        public void BroadcastExcept<T>(NetPacketProcessor proc, string exceptId4, T packet,
            byte channel, DeliveryMethod method) where T : INetSerializable
        {
            var writer = new NetDataWriter();
            proc.WriteNetSerializable(writer, ref packet);
            foreach (var m in _members.Values)
                if (m.Id4 != exceptId4)
                    m.Session.Peer.Send(writer, channel, method);
        }

        private static string Hash(string s)
        {
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}

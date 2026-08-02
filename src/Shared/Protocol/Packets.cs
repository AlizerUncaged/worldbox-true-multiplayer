using System.Collections.Generic;
using LiteNetLib.Utils;

namespace WorldboxMP.Shared.Protocol
{
    // ──────────────────────────────────────────────────────────────────────
    //  Nested value types (used inside list-bearing packets)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>One player as seen in a room roster.</summary>
    public class PlayerInfo : INetSerializable
    {
        public string Id4 = "";
        public string Username = "";
        public byte Role; // RoomRole

        public void Serialize(NetDataWriter w) { w.Put(Id4); w.Put(Username); w.Put(Role); }
        public void Deserialize(NetDataReader r) { Id4 = r.GetString(); Username = r.GetString(); Role = r.GetByte(); }
    }

    /// <summary>One public room as seen in the room list.</summary>
    public class RoomSummary : INetSerializable
    {
        public string ShareableId = "";
        public string Name = "";
        public int PlayerCount;
        public bool HasPassword;

        public void Serialize(NetDataWriter w) { w.Put(ShareableId); w.Put(Name); w.Put(PlayerCount); w.Put(HasPassword); }
        public void Deserialize(NetDataReader r) { ShareableId = r.GetString(); Name = r.GetString(); PlayerCount = r.GetInt(); HasPassword = r.GetBool(); }
    }

    /// <summary>One server log line.</summary>
    public class LogEntry : INetSerializable
    {
        public long UnixMs;
        public string Text = "";

        public void Serialize(NetDataWriter w) { w.Put(UnixMs); w.Put(Text); }
        public void Deserialize(NetDataReader r) { UnixMs = r.GetLong(); Text = r.GetString(); }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Auth (C2S requests, S2C response)
    // ──────────────────────────────────────────────────────────────────────

    public class RegisterRequest : INetSerializable
    {
        public string Username = "";
        public string Password = "";
        public void Serialize(NetDataWriter w) { w.Put(Username); w.Put(Password); }
        public void Deserialize(NetDataReader r) { Username = r.GetString(); Password = r.GetString(); }
    }

    public class LoginRequest : INetSerializable
    {
        public string Username = "";
        public string Password = "";
        public void Serialize(NetDataWriter w) { w.Put(Username); w.Put(Password); }
        public void Deserialize(NetDataReader r) { Username = r.GetString(); Password = r.GetString(); }
    }

    public class AuthResponse : INetSerializable
    {
        public bool Ok;
        public string Message = "";
        public string Id4 = "";       // external player id (Guid v4), empty on failure
        public string Username = "";
        public void Serialize(NetDataWriter w) { w.Put(Ok); w.Put(Message); w.Put(Id4); w.Put(Username); }
        public void Deserialize(NetDataReader r) { Ok = r.GetBool(); Message = r.GetString(); Id4 = r.GetString(); Username = r.GetString(); }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Rooms
    // ──────────────────────────────────────────────────────────────────────

    public class CreateRoomRequest : INetSerializable
    {
        public string Name = "";
        public string Password = "";       // empty = no password (max 16 chars enforced server-side)
        public byte Visibility;            // RoomVisibility
        public void Serialize(NetDataWriter w) { w.Put(Name); w.Put(Password); w.Put(Visibility); }
        public void Deserialize(NetDataReader r) { Name = r.GetString(); Password = r.GetString(); Visibility = r.GetByte(); }
    }

    public class JoinRoomRequest : INetSerializable
    {
        public string ShareableId = "";
        public string Password = "";
        public void Serialize(NetDataWriter w) { w.Put(ShareableId); w.Put(Password); }
        public void Deserialize(NetDataReader r) { ShareableId = r.GetString(); Password = r.GetString(); }
    }

    public class LeaveRoomRequest : INetSerializable
    {
        public void Serialize(NetDataWriter w) { }
        public void Deserialize(NetDataReader r) { }
    }

    /// <summary>Sent to a client when it successfully creates/joins a room.</summary>
    public class RoomJoined : INetSerializable
    {
        public string ShareableId = "";
        public string Name = "";
        public byte MyRole;                // RoomRole of the receiving player
        public void Serialize(NetDataWriter w) { w.Put(ShareableId); w.Put(Name); w.Put(MyRole); }
        public void Deserialize(NetDataReader r) { ShareableId = r.GetString(); Name = r.GetString(); MyRole = r.GetByte(); }
    }

    /// <summary>Generic room-operation failure (wrong password, full, not found, no permission...).</summary>
    public class RoomError : INetSerializable
    {
        public string Message = "";
        public void Serialize(NetDataWriter w) { w.Put(Message); }
        public void Deserialize(NetDataReader r) { Message = r.GetString(); }
    }

    public class RoomListRequest : INetSerializable
    {
        public void Serialize(NetDataWriter w) { }
        public void Deserialize(NetDataReader r) { }
    }

    public class RoomListResponse : INetSerializable
    {
        public List<RoomSummary> Rooms = new List<RoomSummary>();
        public void Serialize(NetDataWriter w)
        {
            w.Put((ushort)Rooms.Count);
            foreach (var room in Rooms) room.Serialize(w);
        }
        public void Deserialize(NetDataReader r)
        {
            int n = r.GetUShort();
            Rooms = new List<RoomSummary>(n);
            for (int i = 0; i < n; i++) { var s = new RoomSummary(); s.Deserialize(r); Rooms.Add(s); }
        }
    }

    /// <summary>Full room roster, pushed whenever membership/roles change.</summary>
    public class RoomRoster : INetSerializable
    {
        public string ShareableId = "";
        public List<PlayerInfo> Players = new List<PlayerInfo>();
        public void Serialize(NetDataWriter w)
        {
            w.Put(ShareableId);
            w.Put((ushort)Players.Count);
            foreach (var p in Players) p.Serialize(w);
        }
        public void Deserialize(NetDataReader r)
        {
            ShareableId = r.GetString();
            int n = r.GetUShort();
            Players = new List<PlayerInfo>(n);
            for (int i = 0; i < n; i++) { var p = new PlayerInfo(); p.Deserialize(r); Players.Add(p); }
        }
    }

    /// <summary>Moderation action a player wants to perform on another (permission checked server-side).</summary>
    public class ModerationRequest : INetSerializable
    {
        public const byte ACTION_KICK = 0;
        public const byte ACTION_PROMOTE = 1;  // -> Admin
        public const byte ACTION_DEMOTE = 2;   // -> Player

        public byte Action;
        public string TargetId4 = "";
        public void Serialize(NetDataWriter w) { w.Put(Action); w.Put(TargetId4); }
        public void Deserialize(NetDataReader r) { Action = r.GetByte(); TargetId4 = r.GetString(); }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Live play: cursors, logs, generic mirror envelope
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A player's floating hand cursor in world space. C2S the server fills Id4;
    /// S2C it is broadcast to the rest of the room.
    /// </summary>
    public class CursorUpdate : INetSerializable
    {
        public string Id4 = "";   // ignored on C2S (server stamps the sender)
        public float X;
        public float Y;
        public bool Pressed;
        public void Serialize(NetDataWriter w) { w.Put(Id4); w.Put(X); w.Put(Y); w.Put(Pressed); }
        public void Deserialize(NetDataReader r) { Id4 = r.GetString(); X = r.GetFloat(); Y = r.GetFloat(); Pressed = r.GetBool(); }
    }

    public class LogListRequest : INetSerializable
    {
        public void Serialize(NetDataWriter w) { }
        public void Deserialize(NetDataReader r) { }
    }

    public class LogListResponse : INetSerializable
    {
        public List<LogEntry> Entries = new List<LogEntry>();
        public void Serialize(NetDataWriter w)
        {
            w.Put((ushort)Entries.Count);
            foreach (var e in Entries) e.Serialize(w);
        }
        public void Deserialize(NetDataReader r)
        {
            int n = r.GetUShort();
            Entries = new List<LogEntry>(n);
            for (int i = 0; i < n; i++) { var e = new LogEntry(); e.Deserialize(r); Entries.Add(e); }
        }
    }

    /// <summary>
    /// Generic mirroring envelope. <see cref="Kind"/> is a <see cref="MirrorKind"/>;
    /// <see cref="Payload"/> is a kind-specific binary blob. This lets the world-sync
    /// layer evolve (new entity kinds, fields) without minting a new packet type each
    /// time. SenderId4 is stamped by the server on rebroadcast.
    /// </summary>
    public class MirrorMessage : INetSerializable
    {
        public byte Kind;            // MirrorKind
        public string SenderId4 = "";
        /// <summary>World generation this message belongs to. The king bumps it when it
        /// loads/generates a different world; receivers drop messages whose epoch doesn't
        /// match the world they're on, so stale mirrors can't corrupt a mid-transfer world.
        /// 0 = sender has no synced world yet.</summary>
        public byte Epoch;
        public byte[] Payload = System.Array.Empty<byte>();
        public void Serialize(NetDataWriter w) { w.Put(Kind); w.Put(SenderId4); w.Put(Epoch); w.PutBytesWithLength(Payload); }
        public void Deserialize(NetDataReader r) { Kind = r.GetByte(); SenderId4 = r.GetString(); Epoch = r.GetByte(); Payload = r.GetBytesWithLength(); }
    }

    /// <summary>
    /// Round-trip latency probe. Originator → server → room king. The server stamps
    /// <see cref="OriginId4"/> with the sender. <see cref="ClientTicks"/> is the
    /// originator's Stopwatch timestamp, echoed back unchanged so it can compute the
    /// full round-trip without keeping state.
    /// </summary>
    public class PingProbe : INetSerializable
    {
        public string OriginId4 = "";
        public int Seq;
        public long ClientTicks;
        public void Serialize(NetDataWriter w) { w.Put(OriginId4); w.Put(Seq); w.Put(ClientTicks); }
        public void Deserialize(NetDataReader r) { OriginId4 = r.GetString(); Seq = r.GetInt(); ClientTicks = r.GetLong(); }
    }

    /// <summary>King → server → originator: echo of a <see cref="PingProbe"/>. The server
    /// routes it back to the session named by <see cref="OriginId4"/>.</summary>
    public class PongReply : INetSerializable
    {
        public string OriginId4 = "";
        public int Seq;
        public long ClientTicks;
        public void Serialize(NetDataWriter w) { w.Put(OriginId4); w.Put(Seq); w.Put(ClientTicks); }
        public void Deserialize(NetDataReader r) { OriginId4 = r.GetString(); Seq = r.GetInt(); ClientTicks = r.GetLong(); }
    }

    /// <summary>
    /// Server → king: "produce a world snapshot for this player" (sent on join, and on a
    /// follower's explicit resync request).
    /// </summary>
    public class SnapshotRequest : INetSerializable
    {
        public string ForId4 = "";
        public void Serialize(NetDataWriter w) { w.Put(ForId4); }
        public void Deserialize(NetDataReader r) { ForId4 = r.GetString(); }
    }

    /// <summary>
    /// Follower → server: "my world drifted / never arrived - ask the king to resync me."
    /// The server rate-limits and converts it into a <see cref="SnapshotRequest"/> to the king.
    /// </summary>
    public class ResyncRequest : INetSerializable
    {
        public void Serialize(NetDataWriter w) { }
        public void Deserialize(NetDataReader r) { }
    }

    /// <summary>
    /// One piece of a full-world snapshot (zlib JSON from SaveManager), king → server →
    /// target(s). Travels on <see cref="NetProtocol.SnapshotChannel"/> so a huge world
    /// can't head-of-line-block live mirrors. TargetId4 empty = broadcast resync.
    /// Count/TotalBytes ride on every chunk so receivers can show progress and allocate
    /// without a separate header packet; TransferId lets a newer transfer supersede an
    /// unfinished one.
    /// </summary>
    public class SnapshotChunk : INetSerializable
    {
        public string TargetId4 = "";
        public int TransferId;
        public byte Epoch;          // world generation this snapshot represents
        public int Index;
        public int Count;
        public int TotalBytes;
        public byte[] Data = System.Array.Empty<byte>();
        public void Serialize(NetDataWriter w)
        {
            w.Put(TargetId4); w.Put(TransferId); w.Put(Epoch);
            w.Put(Index); w.Put(Count); w.Put(TotalBytes); w.PutBytesWithLength(Data);
        }
        public void Deserialize(NetDataReader r)
        {
            TargetId4 = r.GetString(); TransferId = r.GetInt(); Epoch = r.GetByte();
            Index = r.GetInt(); Count = r.GetInt(); TotalBytes = r.GetInt(); Data = r.GetBytesWithLength();
        }
    }
}

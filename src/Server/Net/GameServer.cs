using System;
using System.Collections.Generic;
using System.Drawing;
using LiteNetLib;
using LiteNetLib.Utils;
using Pastel;
using WorldboxMP.Server.Auth;
using WorldboxMP.Server.Data;
using WorldboxMP.Server.Rooms;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server.Net
{
    /// <summary>
    /// The LiteNetLib game server: accepts connections, routes packets, and owns
    /// the auth + room subsystems. Everything runs on a single thread driven by
    /// <see cref="PollEvents"/> from <c>Program</c>, so no locking is needed.
    /// </summary>
    public class GameServer
    {
        private readonly int _port;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _net;
        private readonly NetPacketProcessor _proc = new();
        private readonly Dictionary<NetPeer, Session> _sessions = new();

        private readonly AuthService _auth;
        private readonly RoomManager _rooms;

        private readonly LinkedList<LogEntry> _log = new();
        private const int MaxLog = 500;

        public GameServer(IPlayerStore db, int port)
        {
            _port = port;
            _auth = new AuthService(db);
            _net = new NetManager(_listener)
            {
                AutoRecycle = true,        // recycle readers after each receive event
                UpdateTime = 15,
                ChannelsCount = NetProtocol.ChannelCount,   // ch1 = snapshot chunks
            };
            _rooms = new RoomManager(_proc, Log);
            WireEvents();
            WirePackets();
        }

        public void Start()
        {
            if (!_net.Start(_port))
                throw new InvalidOperationException($"Failed to bind UDP port {_port}.");
            Log($"Server listening on 0.0.0.0:{_port} (key '{NetProtocol.ConnectKey}').");
        }

        public void PollEvents() => _net.PollEvents();

        public void Stop()
        {
            _net.Stop();
            Log("Server stopped.");
        }

        // ── Wiring ──────────────────────────────────────────────────────────
        private void WireEvents()
        {
            _listener.ConnectionRequestEvent += request =>
                request.AcceptIfKey(NetProtocol.ConnectKey);

            _listener.PeerConnectedEvent += peer =>
            {
                _sessions[peer] = new Session(peer);
                Log($"Peer connected: {peer.Address} (id {peer.Id}).");
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                if (_sessions.TryGetValue(peer, out var session))
                {
                    _rooms.LeaveRoom(session);
                    _sessions.Remove(peer);
                    LogC($"Peer disconnected: {session.Username} ({info.Reason}).",
                         $"{Dim("disconnect")} {Usr(string.IsNullOrEmpty(session.Username) ? "(anon)" : session.Username)} {Dim("(" + info.Reason + ")")}");
                }
            };

            _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                try { _proc.ReadAllPackets(reader, peer); }
                catch (Exception ex) { Log($"Bad packet from {peer.Address}: {ex.Message}"); }
            };
        }

        private void WirePackets()
        {
            _proc.SubscribeNetSerializable<RegisterRequest, NetPeer>(OnRegister);
            _proc.SubscribeNetSerializable<LoginRequest, NetPeer>(OnLogin);
            _proc.SubscribeNetSerializable<CreateRoomRequest, NetPeer>(OnCreateRoom);
            _proc.SubscribeNetSerializable<JoinRoomRequest, NetPeer>(OnJoinRoom);
            _proc.SubscribeNetSerializable<LeaveRoomRequest, NetPeer>(OnLeaveRoom);
            _proc.SubscribeNetSerializable<RoomListRequest, NetPeer>(OnRoomList);
            _proc.SubscribeNetSerializable<ModerationRequest, NetPeer>(OnModeration);
            _proc.SubscribeNetSerializable<CursorUpdate, NetPeer>(OnCursor);
            _proc.SubscribeNetSerializable<LogListRequest, NetPeer>(OnLogList);
            _proc.SubscribeNetSerializable<MirrorMessage, NetPeer>(OnMirror);
            _proc.SubscribeNetSerializable<SnapshotChunk, NetPeer>(OnSnapshotChunk);
            _proc.SubscribeNetSerializable<ResyncRequest, NetPeer>(OnResyncRequest);
            _proc.SubscribeNetSerializable<PingProbe, NetPeer>(OnPingProbe);
            _proc.SubscribeNetSerializable<PongReply, NetPeer>(OnPongReply);
        }

        private Session SessionOf(NetPeer peer) => _sessions.TryGetValue(peer, out var s) ? s : null;

        /// <summary>A reconnect of the same account replaces its old session - keep it simple, no dup sessions.</summary>
        private void DropOldSessions(Session keep)
        {
            var stale = new List<NetPeer>();
            foreach (var kv in _sessions)
                if (kv.Value != keep && kv.Value.Id4 == keep.Id4) stale.Add(kv.Key);
            foreach (var peer in stale)
            {
                var old = _sessions[peer];
                _rooms.LeaveRoom(old);
                _sessions.Remove(peer);
                try { peer.Disconnect(); } catch { }
                Log($"Replaced old session of {keep.Username}.");
            }
        }

        private bool RequireAuth(Session s)
        {
            if (s != null && s.IsAuthenticated) return true;
            s?.Peer.Send(_proc, new RoomError { Message = "You must log in first." });
            return false;
        }

        // ── Handlers ────────────────────────────────────────────────────────
        private void OnRegister(RegisterRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null) return;
            var res = _auth.Register(req.Username, req.Password);
            if (res.Ok) { s.Id4 = res.Player.Id4; s.Username = res.Player.Username; DropOldSessions(s); LogC($"Registered: {s.Username} (id4 {s.Id4}).", $"{Ok("registered")} {Usr(s.Username)} {Dim("id4 " + s.Id4)}"); }
            peer.Send(_proc, MakeAuth(res));
        }

        private void OnLogin(LoginRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null) return;
            var res = _auth.Login(req.Username, req.Password);
            if (res.Ok) { s.Id4 = res.Player.Id4; s.Username = res.Player.Username; DropOldSessions(s); LogC($"Login: {s.Username}.", $"{Ok("login")} {Usr(s.Username)}"); }
            peer.Send(_proc, MakeAuth(res));
        }

        private AuthResponse MakeAuth(AuthResult res) => new AuthResponse
        {
            Ok = res.Ok,
            Message = res.Message,
            Id4 = res.Ok ? res.Player.Id4 : "",
            Username = res.Ok ? res.Player.Username : "",
        };

        private void OnCreateRoom(CreateRoomRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (!RequireAuth(s)) return;
            var room = _rooms.CreateRoom(s, req.Name, req.Password, (RoomVisibility)req.Visibility, out var err);
            if (room == null) { peer.Send(_proc, new RoomError { Message = err }); return; }
            peer.Send(_proc, new RoomJoined { ShareableId = room.ShareableId, Name = room.Name, MyRole = (byte)RoomRole.King });
            // The roster broadcast inside CreateRoom/JoinRoom reaches this client BEFORE its
            // RoomJoined (it still has no RoomId, so it drops the packet). Re-push now that
            // the client knows its room - otherwise a joiner sees an empty player list.
            _rooms.PushRoster(room);
        }

        private void OnJoinRoom(JoinRoomRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (!RequireAuth(s)) return;
            var room = _rooms.JoinRoom(s, req.ShareableId, req.Password, out var err);
            if (room == null) { peer.Send(_proc, new RoomError { Message = err }); return; }
            var me = room.Get(s.Id4);
            peer.Send(_proc, new RoomJoined { ShareableId = room.ShareableId, Name = room.Name, MyRole = (byte)(me?.Role ?? RoomRole.Player) });
            _rooms.PushRoster(room);   // see OnCreateRoom: the joiner missed the in-join roster push

            // Ask the king (if any, and not us) to send this joiner the current world.
            var king = room.GetKing();
            if (king != null && king.Id4 != s.Id4)
                king.Session.Peer.Send(_proc, new SnapshotRequest { ForId4 = s.Id4 });
        }

        private void OnLeaveRoom(LeaveRoomRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null) return;
            _rooms.LeaveRoom(s);
        }

        private void OnRoomList(RoomListRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (!RequireAuth(s)) return;
            peer.Send(_proc, new RoomListResponse { Rooms = _rooms.ListPublic() });
        }

        private void OnModeration(ModerationRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (!RequireAuth(s)) return;
            if (!_rooms.Moderate(s, req.Action, req.TargetId4, out var err))
                peer.Send(_proc, new RoomError { Message = err });
        }

        private void OnCursor(CursorUpdate req, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null || s.CurrentRoom == null) return;
            req.Id4 = s.Id4;
            // Cursors are high-frequency and tolerate loss → sequenced (unreliable, ordered).
            s.CurrentRoom.BroadcastExcept(_proc, s.Id4, req, DeliveryMethod.Sequenced);
        }

        private void OnLogList(LogListRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (!RequireAuth(s)) return;
            var resp = new LogListResponse();
            foreach (var e in _log) resp.Entries.Add(e);
            peer.Send(_proc, resp);
        }

        private void OnMirror(MirrorMessage msg, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null || s.CurrentRoom == null) return;
            var room = s.CurrentRoom;
            msg.SenderId4 = s.Id4;

            LogMirror(s, msg);

            // King-authoritative routing:
            //  - From the king  → authoritative event, fan out to everyone else.
            //  - From a player   → an intent, forward only to the king to execute.
            var method = (MirrorKind)msg.Kind == MirrorKind.EntityState
                ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;
            if (room.KingId4 == s.Id4)
            {
                room.BroadcastExcept(_proc, s.Id4, msg, method);
            }
            else
            {
                var king = room.GetKing();
                king?.Session.Peer.Send(_proc, msg, method);
            }
        }

        // Verbose, human-readable logging of mirrored gameplay (EntityState is skipped - 10 Hz spam).
        private void LogMirror(Session s, MirrorMessage msg)
        {
            string room = s.CurrentRoom.ShareableId;
            switch ((MirrorKind)msg.Kind)
            {
                case MirrorKind.GodPower:
                    var gp = MirrorCodec.ReadGodPower(msg.Payload);
                    LogC($"[{room}] {s.Username} used power '{gp.PowerId}' at ({gp.X},{gp.Y})",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("used power")} {Val(gp.PowerId)} {Dim($"at ({gp.X},{gp.Y})")}");
                    break;
                case MirrorKind.SpawnUnit:
                    var sp = MirrorCodec.ReadSpawn(msg.Payload);
                    LogC($"[{room}] {s.Username} spawned creature (sub {sp.SubId}, {sp.ActorJson.Length}B)",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("spawned")} {Val("creature")} {Dim($"sub {sp.SubId} · {sp.ActorJson.Length}B")}");
                    break;
                case MirrorKind.Terrain:
                    var tr = MirrorCodec.ReadTerrain(msg.Payload);
                    LogC($"[{room}] {s.Username} terrain '{tr.TileId}' at ({tr.X},{tr.Y})",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("terrain")} {Val(tr.TileId)} {Dim($"at ({tr.X},{tr.Y})")}");
                    break;
                case MirrorKind.Death:
                    var dm = MirrorCodec.ReadDeath(msg.Payload);
                    LogC($"[{room}] {s.Username} unit #{dm.KingId} died",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("death")} {Dim("#" + dm.KingId)}");
                    break;
                case MirrorKind.Fire:
                    var fm = MirrorCodec.ReadFire(msg.Payload);
                    LogC($"[{room}] {s.Username} fire {(fm.On ? "+" : "-")} ({fm.X},{fm.Y})",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("fire")} {Val(fm.On ? "+" : "-")} {Dim($"({fm.X},{fm.Y})")}");
                    break;
                case MirrorKind.Trait:
                    var tt = MirrorCodec.ReadTrait(msg.Payload);
                    LogC($"[{room}] {s.Username} trait {(tt.Add ? "+" : "-")}{tt.TraitId} #{tt.KingId}",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("trait")} {Val((tt.Add ? "+" : "-") + tt.TraitId)} {Dim("#" + tt.KingId)}");
                    break;
                case MirrorKind.Object:
                    var om = MirrorCodec.ReadObject(msg.Payload);
                    LogC($"[{room}] {s.Username} structural kind={om.Kind} ({om.Json.Length}B)",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("object")} {Val("kind " + om.Kind)} {Dim(om.Json.Length + "B")}");
                    break;
                case MirrorKind.Equip:
                    var em = MirrorCodec.ReadEquip(msg.Payload);
                    LogC($"[{room}] {s.Username} equip #{em.ActorId}",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("equip")} {Dim("#" + em.ActorId)}");
                    break;
                case MirrorKind.Rename:
                    var rn = MirrorCodec.ReadRename(msg.Payload);
                    LogC($"[{room}] {s.Username} rename #{rn.Id} -> {rn.Name}",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("rename")} {Dim("#" + rn.Id)} {Val(rn.Name)}");
                    break;
                case MirrorKind.Time:
                    var tm = MirrorCodec.ReadTime(msg.Payload);
                    LogC($"[{room}] {s.Username} year-sync t={tm.WorldTime:F0}",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("time")} {Dim("t=" + tm.WorldTime.ToString("F0"))}");
                    break;
                // Health/Status are high-frequency → not logged.
                case MirrorKind.SimControl:
                    var sc = MirrorCodec.ReadSimControl(msg.Payload);
                    LogC($"[{room}] {s.Username} sim paused={sc.Paused} speed={sc.SpeedId}",
                         $"{Dim("[" + room + "]")} {Usr(s.Username)} {Act("sim")} paused={Val(sc.Paused.ToString())} speed={Val(sc.SpeedId)}");
                    break;
                // EntityState: intentionally not logged (high frequency).
            }
        }

        // Round-trip ping: stamp the originator and forward to the room's king.
        private void OnPingProbe(PingProbe p, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null || s.CurrentRoom == null) return;
            p.OriginId4 = s.Id4;
            var king = s.CurrentRoom.GetKing();
            king?.Session.Peer.Send(_proc, p, DeliveryMethod.ReliableOrdered);
        }

        // King's echo → route back to the originating player.
        private void OnPongReply(PongReply p, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null || s.CurrentRoom == null) return;
            if (s.CurrentRoom.KingId4 != s.Id4) return; // only the king answers probes
            var target = s.CurrentRoom.Get(p.OriginId4);
            target?.Session.Peer.Send(_proc, p, DeliveryMethod.ReliableOrdered);
        }

        private void OnSnapshotChunk(SnapshotChunk chunk, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null || s.CurrentRoom == null) return;
            // Only the king ships snapshots; relay on the snapshot channel so the
            // transfer can't head-of-line-block live mirror traffic.
            if (s.CurrentRoom.KingId4 != s.Id4) return;
            if (string.IsNullOrEmpty(chunk.TargetId4))
            {
                s.CurrentRoom.BroadcastExcept(_proc, s.Id4, chunk, NetProtocol.SnapshotChannel, DeliveryMethod.ReliableOrdered);
                if (chunk.Index == 0 || chunk.Index == chunk.Count - 1)
                    LogC($"[{s.CurrentRoom.ShareableId}] {s.Username} resync chunk {chunk.Index + 1}/{chunk.Count} ({chunk.TotalBytes} bytes total) → all",
                         $"{Dim("[" + s.CurrentRoom.ShareableId + "]")} {Usr(s.Username)} {Act("resync")} {Dim($"chunk {chunk.Index + 1}/{chunk.Count} → all")}");
                return;
            }
            var target = s.CurrentRoom.Get(chunk.TargetId4);
            if (target != null)
            {
                target.Session.Peer.Send(_proc, chunk, NetProtocol.SnapshotChannel, DeliveryMethod.ReliableOrdered);
                if (chunk.Index == 0 || chunk.Index == chunk.Count - 1)
                    Log($"Relayed snapshot chunk {chunk.Index + 1}/{chunk.Count} ({chunk.TotalBytes} bytes total) {s.Username} → {target.Username}.");
            }
        }

        /// <summary>A follower asked to be resynced: convert to a SnapshotRequest to the
        /// room king, rate-limited per session (snapshots are expensive for the king).</summary>
        private const int ResyncCooldownSeconds = 8;
        private void OnResyncRequest(ResyncRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null || s.CurrentRoom == null) return;
            var room = s.CurrentRoom;
            if (room.KingId4 == s.Id4) return;   // the king resyncs locally, not via the server
            var now = System.DateTime.UtcNow;
            if ((now - s.LastResyncRequestUtc).TotalSeconds < ResyncCooldownSeconds) return;
            s.LastResyncRequestUtc = now;
            var king = room.GetKing();
            if (king == null) return;
            king.Session.Peer.Send(_proc, new SnapshotRequest { ForId4 = s.Id4 });
            LogC($"[{room.ShareableId}] {s.Username} requested world resync",
                 $"{Dim("[" + room.ShareableId + "]")} {Usr(s.Username)} {Act("requested resync")}");
        }

        // ── Logging ─────────────────────────────────────────────────────────
        // Color helpers (console only). The ring buffer / clients always get plain text.
        internal static string Usr(string s) => s.Pastel(Color.Cyan);
        internal static string Act(string s) => s.Pastel(Color.Gold);
        internal static string Val(string s) => s.Pastel(Color.MediumPurple);
        internal static string Dim(string s) => s.Pastel(Color.Gray);
        internal static string Ok(string s) => s.Pastel(Color.LightGreen);
        internal static string Bad(string s) => s.Pastel(Color.IndianRed);

        /// <summary>Log plain text (console shows it uncolored).</summary>
        private void Log(string text) => Emit(text, text);

        /// <summary>Log with a separate colored console rendering (clients still get the plain text).</summary>
        private void LogC(string plain, string colored) => Emit(plain, colored);

        private void Emit(string plain, string colored)
        {
            var entry = new LogEntry { UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Text = plain };
            _log.AddLast(entry);
            while (_log.Count > MaxLog) _log.RemoveFirst();
            Console.WriteLine($"{$"[{DateTime.Now:HH:mm:ss}]".Pastel(Color.DimGray)} {colored}");
        }
    }
}

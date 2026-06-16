using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
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

        public GameServer(Database db, int port)
        {
            _port = port;
            _auth = new AuthService(db);
            _net = new NetManager(_listener)
            {
                AutoRecycle = true,        // recycle readers after each receive event
                UpdateTime = 15,
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
                    Log($"Peer disconnected: {session.Username} ({info.Reason}).");
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
        }

        private Session SessionOf(NetPeer peer) => _sessions.TryGetValue(peer, out var s) ? s : null;

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
            if (res.Ok) { s.Id4 = res.Player.Id4; s.Username = res.Player.Username; Log($"Registered: {s.Username} (id4 {s.Id4})."); }
            peer.Send(_proc, MakeAuth(res));
        }

        private void OnLogin(LoginRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (s == null) return;
            var res = _auth.Login(req.Username, req.Password);
            if (res.Ok) { s.Id4 = res.Player.Id4; s.Username = res.Player.Username; Log($"Login: {s.Username}."); }
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
        }

        private void OnJoinRoom(JoinRoomRequest req, NetPeer peer)
        {
            var s = SessionOf(peer); if (!RequireAuth(s)) return;
            var room = _rooms.JoinRoom(s, req.ShareableId, req.Password, out var err);
            if (room == null) { peer.Send(_proc, new RoomError { Message = err }); return; }
            var me = room.Get(s.Id4);
            peer.Send(_proc, new RoomJoined { ShareableId = room.ShareableId, Name = room.Name, MyRole = (byte)(me?.Role ?? RoomRole.Player) });
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
            msg.SenderId4 = s.Id4;
            // Foundation behavior: relay to the rest of the room. The king-authoritative
            // routing (intents -> king, state -> everyone) lands in the mirroring milestone.
            s.CurrentRoom.BroadcastExcept(_proc, s.Id4, msg);
        }

        // ── Logging ─────────────────────────────────────────────────────────
        private void Log(string text)
        {
            var entry = new LogEntry { UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Text = text };
            _log.AddLast(entry);
            while (_log.Count > MaxLog) _log.RemoveFirst();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
        }
    }
}

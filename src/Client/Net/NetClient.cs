using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.Net
{
    public enum ConnState { Disconnected, Connecting, Connected }

    /// <summary>
    /// Client-side LiteNetLib connection to the multiplayer server. All callbacks
    /// fire on the Unity main thread because <see cref="Poll"/> is driven from a
    /// MonoBehaviour Update, so handlers may safely touch game/UI objects.
    /// </summary>
    public class NetClient
    {
        public static NetClient Instance { get; private set; }

        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetManager _net;
        private readonly NetPacketProcessor _proc = new NetPacketProcessor();
        private NetPeer _server;

        private string _host = "127.0.0.1";
        private int _port = NetProtocol.DefaultPort;

        // ── Session state ───────────────────────────────────────────────────
        public ConnState State { get; private set; } = ConnState.Disconnected;
        public bool LoggedIn { get; private set; }
        public string Id4 { get; private set; } = "";
        public string Username { get; private set; } = "";

        public string RoomId { get; private set; } = "";
        public string RoomName { get; private set; } = "";
        public RoomRole MyRole { get; private set; } = RoomRole.Player;
        public bool InRoom => RoomId.Length > 0;
        public bool IsAdmin => MyRole == RoomRole.Admin || MyRole == RoomRole.King;
        public bool IsKing => MyRole == RoomRole.King;

        public List<PlayerInfo> Roster { get; private set; } = new List<PlayerInfo>();
        public List<RoomSummary> RoomList { get; private set; } = new List<RoomSummary>();
        public List<LogEntry> Logs { get; private set; } = new List<LogEntry>();

        // ── Events for the UI ───────────────────────────────────────────────
        public event Action<ConnState> OnStateChanged;
        public event Action<AuthResponse> OnAuth;
        public event Action<RoomJoined> OnRoomJoined;
        public event Action<string> OnRoomError;
        public event Action OnRosterChanged;
        public event Action OnRoomListChanged;
        public event Action OnLogsChanged;
        public event Action<CursorUpdate> OnRemoteCursor;
        public event Action<MirrorMessage> OnMirror;
        public event Action<SnapshotRequest> OnSnapshotRequested;
        public event Action<WorldSnapshot> OnSnapshotReceived;

        public NetClient()
        {
            Instance = this;
            _net = new NetManager(_listener) { AutoRecycle = true, UpdateTime = 15, UnconnectedMessagesEnabled = false };
            WireEvents();
            WirePackets();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────
        public void Connect(string host = null, int port = 0)
        {
            if (host != null) _host = host;
            if (port > 0) _port = port;
            if (State != ConnState.Disconnected) return;

            if (!_net.IsRunning) _net.Start();
            SetState(ConnState.Connecting);
            _server = _net.Connect(_host, _port, NetProtocol.ConnectKey);
        }

        public void Disconnect()
        {
            _net.DisconnectAll();
            _server = null;
            ResetSession();
            SetState(ConnState.Disconnected);
        }

        public void Poll() => _net.PollEvents();

        public void Stop()
        {
            _net.Stop();
            ResetSession();
            SetState(ConnState.Disconnected);
        }

        private void ResetSession()
        {
            LoggedIn = false; Id4 = ""; Username = "";
            RoomId = ""; RoomName = ""; MyRole = RoomRole.Player;
            Roster.Clear(); RoomList.Clear(); Logs.Clear();
        }

        // ── Outgoing API ────────────────────────────────────────────────────
        public void Register(string user, string pass) => Send(new RegisterRequest { Username = user, Password = pass });
        public void Login(string user, string pass) => Send(new LoginRequest { Username = user, Password = pass });
        public void CreateRoom(string name, string password, RoomVisibility vis) =>
            Send(new CreateRoomRequest { Name = name, Password = password ?? "", Visibility = (byte)vis });
        public void JoinRoom(string id, string password) =>
            Send(new JoinRoomRequest { ShareableId = id, Password = password ?? "" });
        public void LeaveRoom() { Send(new LeaveRoomRequest()); RoomId = ""; RoomName = ""; Roster.Clear(); OnRosterChanged?.Invoke(); }
        public void RequestRoomList() => Send(new RoomListRequest());
        public void RequestLogs() => Send(new LogListRequest());
        public void Kick(string id4) => Send(new ModerationRequest { Action = ModerationRequest.ACTION_KICK, TargetId4 = id4 });
        public void Promote(string id4) => Send(new ModerationRequest { Action = ModerationRequest.ACTION_PROMOTE, TargetId4 = id4 });
        public void Demote(string id4) => Send(new ModerationRequest { Action = ModerationRequest.ACTION_DEMOTE, TargetId4 = id4 });
        public void SendCursor(float x, float y, bool pressed) =>
            Send(new CursorUpdate { X = x, Y = y, Pressed = pressed }, DeliveryMethod.Sequenced);
        public void SendMirror(MirrorKind kind, byte[] payload) =>
            Send(new MirrorMessage { Kind = (byte)kind, Payload = payload ?? Array.Empty<byte>() });

        private void Send<T>(T packet, DeliveryMethod method = DeliveryMethod.ReliableOrdered) where T : INetSerializable
        {
            if (_server == null || State != ConnState.Connected) return;
            _server.Send(_proc, packet, method);
        }

        // ── Wiring ──────────────────────────────────────────────────────────
        private void WireEvents()
        {
            _listener.PeerConnectedEvent += peer => { _server = peer; SetState(ConnState.Connected); };
            _listener.PeerDisconnectedEvent += (peer, info) => { ResetSession(); SetState(ConnState.Disconnected); };
            _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                try { _proc.ReadAllPackets(reader); }
                catch (Exception e) { ModLog.Warn("Bad packet: " + e.Message); }
            };
        }

        private void WirePackets()
        {
            _proc.SubscribeNetSerializable<AuthResponse>(OnAuthResponse);
            _proc.SubscribeNetSerializable<RoomJoined>(OnRoomJoinedPacket);
            _proc.SubscribeNetSerializable<RoomError>(p => OnRoomError?.Invoke(p.Message));
            _proc.SubscribeNetSerializable<RoomRoster>(OnRosterPacket);
            _proc.SubscribeNetSerializable<RoomListResponse>(p => { RoomList = p.Rooms; OnRoomListChanged?.Invoke(); });
            _proc.SubscribeNetSerializable<LogListResponse>(p => { Logs = p.Entries; OnLogsChanged?.Invoke(); });
            _proc.SubscribeNetSerializable<CursorUpdate>(p => OnRemoteCursor?.Invoke(p));
            _proc.SubscribeNetSerializable<MirrorMessage>(p => OnMirror?.Invoke(p));
            _proc.SubscribeNetSerializable<SnapshotRequest>(p => OnSnapshotRequested?.Invoke(p));
            _proc.SubscribeNetSerializable<WorldSnapshot>(p => OnSnapshotReceived?.Invoke(p));
        }

        /// <summary>King → server → joiner: ship the current world snapshot.</summary>
        public void SendSnapshot(string targetId4, byte[] data) =>
            Send(new WorldSnapshot { TargetId4 = targetId4, Data = data ?? Array.Empty<byte>() });

        /// <summary>Change target endpoint and reconnect (used by the settings dialog).</summary>
        public void Reconnect(string host, int port)
        {
            Disconnect();
            _host = host; _port = port;
            Connect(host, port);
        }

        public string Host => _host;
        public int Port => _port;

        private void OnAuthResponse(AuthResponse p)
        {
            if (p.Ok) { LoggedIn = true; Id4 = p.Id4; Username = p.Username; }
            OnAuth?.Invoke(p);
        }

        private void OnRoomJoinedPacket(RoomJoined p)
        {
            RoomId = p.ShareableId; RoomName = p.Name; MyRole = (RoomRole)p.MyRole;
            OnRoomJoined?.Invoke(p);
        }

        private void OnRosterPacket(RoomRoster p)
        {
            if (p.ShareableId == RoomId)
            {
                Roster = p.Players;
                // Keep my own role in sync (promotions/demotions).
                var me = Roster.Find(x => x.Id4 == Id4);
                if (me != null) MyRole = (RoomRole)me.Role;
                OnRosterChanged?.Invoke();
            }
        }

        private void SetState(ConnState s)
        {
            if (State == s) return;
            State = s;
            OnStateChanged?.Invoke(s);
        }
    }
}

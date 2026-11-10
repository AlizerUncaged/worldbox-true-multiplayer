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
    ///
    /// Resilience: a "remember me" session token (per server, in PlayerPrefs) signs the
    /// player back in on every (re)connect, and an involuntary drop while in a room
    /// rejoins that room automatically once signed in again.
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

        /// <summary>Why the last connection ended / was refused (empty while fine).</summary>
        public string LastDisconnectReason { get; private set; } = "";

        /// <summary>A login/register/token request is in flight.</summary>
        public bool AuthPending { get; private set; }

        /// <summary>Signing in with a remembered session right now (UI shows a spinner, not the form).</summary>
        public bool ResumingSession => AuthPending && _authViaToken;

        /// <summary>A create/join request is in flight.</summary>
        public bool RoomPending { get; private set; }

        /// <summary>Dev tooling (per-instance test accounts) turns remembered sessions off.</summary>
        public bool DisableTokenLogin { get; set; }

        private bool _authViaToken;
        private bool _suppressAutoLogin;       // set after "signed in elsewhere" until a manual login
        private string _pendingJoinPassword = "";
        private string _roomPassword = "";
        private string _rejoinRoomId = "";      // room to re-enter after an involuntary drop
        private string _rejoinPassword = "";

        // ── Events for the UI ───────────────────────────────────────────────
        public event Action<ConnState> OnStateChanged;
        public event Action<AuthResponse> OnAuth;
        public event Action<RoomJoined> OnRoomJoined;
        /// <summary>We left the current room (reason empty = voluntary; else kicked/closed).</summary>
        public event Action<string> OnRoomLeft;
        public event Action<string> OnRoomError;
        /// <summary>Announcement from the server operator (admin panel).</summary>
        public event Action<string> OnServerNotice;
        public event Action OnRosterChanged;
        public event Action OnRoomListChanged;
        public event Action OnLogsChanged;
        public event Action<CursorUpdate> OnRemoteCursor;
        public event Action<MirrorMessage> OnMirror;
        public event Action<SnapshotRequest> OnSnapshotRequested;
        public event Action<SnapshotChunk> OnSnapshotChunk;

        public NetClient()
        {
            Instance = this;
            _net = new NetManager(_listener)
            {
                AutoRecycle = true, UpdateTime = 15, UnconnectedMessagesEnabled = false,
                EnableStatistics = true,  // thread-safe byte/packet counters for the HUD
                ChannelsCount = NetProtocol.ChannelCount   // ch1 = snapshot chunks
            };
            WireEvents();
            WirePackets();
        }

        /// <summary>Transport-level round-trip time in ms, measured by LiteNetLib's own
        /// logic thread (independent of the Unity frame loop, so a busy main thread can't
        /// inflate it). -1 while disconnected.</summary>
        public int TransportRttMs => _server != null && State == ConnState.Connected ? _server.RoundTripTime : -1;

        /// <summary>Total bytes received/sent over the socket since startup (thread-safe longs).</summary>
        public long BytesReceived => _net.Statistics.BytesReceived;
        public long BytesSent => _net.Statistics.BytesSent;

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
            ClearRoom();
            RoomList.Clear(); Logs.Clear();
            AuthPending = false; _authViaToken = false; RoomPending = false;
            LastPingMs = -1;
        }

        private void ClearRoom()
        {
            RoomId = ""; RoomName = ""; MyRole = RoomRole.Player; _roomPassword = "";
            Roster.Clear();
        }

        // ── Remembered session (per server endpoint) ───────────────────────
        private string TokenKey => "wbmp_session_" + _host + ":" + _port;
        private string SavedToken
        {
            get => UnityEngine.PlayerPrefs.GetString(TokenKey, "");
            set
            {
                if (string.IsNullOrEmpty(value)) UnityEngine.PlayerPrefs.DeleteKey(TokenKey);
                else UnityEngine.PlayerPrefs.SetString(TokenKey, value);
                UnityEngine.PlayerPrefs.Save();
            }
        }

        /// <summary>True when this server has a remembered session for us.</summary>
        public bool HasSavedSession => !DisableTokenLogin && SavedToken.Length > 0;

        private void TryResumeSession()
        {
            if (LoggedIn || AuthPending || _suppressAutoLogin || DisableTokenLogin) return;
            string token = SavedToken;
            if (token.Length == 0) return;
            AuthPending = true; _authViaToken = true;
            Send(new TokenLoginRequest { Token = token });
        }

        // ── Outgoing API ────────────────────────────────────────────────────
        /// <summary>Register = create the account and sign in, remembered.</summary>
        public void Register(string user, string pass)
        {
            if (!CanSend || AuthPending) return;
            _suppressAutoLogin = false;
            AuthPending = true; _authViaToken = false;
            Send(new RegisterRequest { Username = user ?? "", Password = pass ?? "" });
            OnStateChanged?.Invoke(State);
        }

        public void Login(string user, string pass, bool remember = false)
        {
            if (!CanSend || AuthPending) return;
            _suppressAutoLogin = false;
            AuthPending = true; _authViaToken = false;
            Send(new LoginRequest { Username = user ?? "", Password = pass ?? "", Remember = remember && !DisableTokenLogin });
            OnStateChanged?.Invoke(State);
        }

        /// <summary>Sign out: forget the remembered session here and on the server.</summary>
        public void Logout()
        {
            Send(new LogoutRequest { Token = SavedToken });
            SavedToken = "";
            _rejoinRoomId = "";
            bool wasInRoom = InRoom;
            LoggedIn = false; Id4 = ""; Username = "";
            ClearRoom(); RoomList.Clear();
            if (wasInRoom) OnRoomLeft?.Invoke("");
            OnRosterChanged?.Invoke();
            OnStateChanged?.Invoke(State);
        }

        public void CreateRoom(string name, string password, RoomVisibility vis)
        {
            if (!CanSend || RoomPending) return;
            RoomPending = true; _pendingJoinPassword = password ?? "";
            Send(new CreateRoomRequest { Name = name ?? "", Password = password ?? "", Visibility = (byte)vis });
        }

        public void JoinRoom(string id, string password)
        {
            if (!CanSend || RoomPending) return;
            RoomPending = true; _pendingJoinPassword = password ?? "";
            Send(new JoinRoomRequest { ShareableId = (id ?? "").Trim().ToUpperInvariant(), Password = password ?? "" });
        }

        public void LeaveRoom()
        {
            _rejoinRoomId = "";
            if (!InRoom) return;
            Send(new LeaveRoomRequest());
            // Leave locally right away (the server's RoomLeft confirmation is then a no-op).
            ClearRoom();
            OnRoomLeft?.Invoke("");
            OnRosterChanged?.Invoke();
        }

        private bool CanSend => _server != null && State == ConnState.Connected;
        public void RequestRoomList() => Send(new RoomListRequest());
        public void RequestLogs() => Send(new LogListRequest());
        public void Kick(string id4) => Send(new ModerationRequest { Action = ModerationRequest.ACTION_KICK, TargetId4 = id4 });
        public void Promote(string id4) => Send(new ModerationRequest { Action = ModerationRequest.ACTION_PROMOTE, TargetId4 = id4 });
        public void Demote(string id4) => Send(new ModerationRequest { Action = ModerationRequest.ACTION_DEMOTE, TargetId4 = id4 });
        public void SendCursor(float x, float y, bool pressed) =>
            Send(new CursorUpdate { X = x, Y = y, Pressed = pressed }, DeliveryMethod.Sequenced);
        public void SendMirror(MirrorKind kind, byte[] payload, byte epoch, DeliveryMethod method = DeliveryMethod.ReliableOrdered) =>
            Send(new MirrorMessage { Kind = (byte)kind, Payload = payload ?? Array.Empty<byte>(), Epoch = epoch }, method);

        /// <summary>Follower: ask the server to have the king resend us the world.</summary>
        public void RequestResync() => Send(new ResyncRequest());

        private void Send<T>(T packet, DeliveryMethod method = DeliveryMethod.ReliableOrdered) where T : INetSerializable
        {
            if (_server == null || State != ConnState.Connected) return;
            _server.Send(_proc, packet, method);
        }

        private void Send<T>(T packet, byte channel, DeliveryMethod method) where T : INetSerializable
        {
            if (_server == null || State != ConnState.Connected) return;
            _server.Send(_proc, packet, channel, method);
        }

        // ── Wiring ──────────────────────────────────────────────────────────
        private void WireEvents()
        {
            _listener.PeerConnectedEvent += peer =>
            {
                _server = peer;
                LastDisconnectReason = "";
                SetState(ConnState.Connected);
                TryResumeSession();
            };
            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                string detail = "";
                try { if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0) detail = info.AdditionalData.GetString(200); }
                catch { }
                LastDisconnectReason = DescribeDisconnect(info.Reason, detail);
                if (detail == NetProtocol.SignedInElsewhere) { _suppressAutoLogin = true; _rejoinRoomId = ""; }
                else if (InRoom) { _rejoinRoomId = RoomId; _rejoinPassword = _roomPassword; }
                bool wasInRoom = InRoom;
                ResetSession();
                if (wasInRoom) OnRoomLeft?.Invoke(LastDisconnectReason);
                SetState(ConnState.Disconnected);
                OnRosterChanged?.Invoke();
            };
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
            _proc.SubscribeNetSerializable<RoomError>(OnRoomErrorPacket);
            _proc.SubscribeNetSerializable<RoomLeft>(OnRoomLeftPacket);
            _proc.SubscribeNetSerializable<ServerNotice>(p => OnServerNotice?.Invoke(p.Text ?? ""));
            _proc.SubscribeNetSerializable<RoomRoster>(OnRosterPacket);
            _proc.SubscribeNetSerializable<RoomListResponse>(p => { RoomList = p.Rooms; OnRoomListChanged?.Invoke(); });
            _proc.SubscribeNetSerializable<LogListResponse>(p => { Logs = p.Entries; OnLogsChanged?.Invoke(); });
            _proc.SubscribeNetSerializable<CursorUpdate>(p => OnRemoteCursor?.Invoke(p));
            _proc.SubscribeNetSerializable<MirrorMessage>(p => OnMirror?.Invoke(p));
            _proc.SubscribeNetSerializable<SnapshotRequest>(p => OnSnapshotRequested?.Invoke(p));
            _proc.SubscribeNetSerializable<SnapshotChunk>(p => OnSnapshotChunk?.Invoke(p));
            _proc.SubscribeNetSerializable<PingProbe>(OnPingProbe);
            _proc.SubscribeNetSerializable<PongReply>(OnPongReply);
        }

        // ── Round-trip ping (originator → server → king → server → originator) ──────
        /// <summary>Last measured round-trip latency in ms (-1 until a reply arrives).</summary>
        public int LastPingMs { get; private set; } = -1;
        private int _pingSeq;

        /// <summary>Send a ping probe (call ~1/s). The server routes it to the room king.</summary>
        public void SendPing()
        {
            if (State != ConnState.Connected || !LoggedIn) return;
            Send(new PingProbe { Seq = ++_pingSeq, ClientTicks = System.Diagnostics.Stopwatch.GetTimestamp() },
                 DeliveryMethod.ReliableOrdered);
        }

        // We only receive a probe if the server picked us as the room king → echo it back.
        private void OnPingProbe(PingProbe p) =>
            Send(new PongReply { OriginId4 = p.OriginId4, Seq = p.Seq, ClientTicks = p.ClientTicks });

        // Our probe came back full circle → compute elapsed from the echoed timestamp.
        private void OnPongReply(PongReply p)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = (now - p.ClientTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastPingMs = ms < 0 ? 0 : (int)System.Math.Round(ms);
        }

        /// <summary>King → server → target(s): one snapshot chunk, on the snapshot channel.</summary>
        public void SendSnapshotChunk(SnapshotChunk chunk) =>
            Send(chunk, NetProtocol.SnapshotChannel, DeliveryMethod.ReliableOrdered);

        /// <summary>Change target endpoint and reconnect (used by the settings dialog).</summary>
        public void Reconnect(string host, int port)
        {
            _host = host; _port = port;
            try { _net.Stop(); } catch { }          // fully tear down (DisconnectAll+reconnect on one manager is unreliable)
            _server = null;
            ResetSession();
            _rejoinRoomId = ""; _suppressAutoLogin = false; LastDisconnectReason = "";
            SetState(ConnState.Disconnected);        // lets dev auto-login re-arm
            _net.Start();
            SetState(ConnState.Connecting);
            _server = _net.Connect(_host, _port, NetProtocol.ConnectKey);
        }

        public string Host => _host;
        public int Port => _port;

        private void OnAuthResponse(AuthResponse p)
        {
            AuthPending = false; _authViaToken = false;
            if (p.Ok)
            {
                LoggedIn = true; Id4 = p.Id4; Username = p.Username;
                if (!string.IsNullOrEmpty(p.Token) && !DisableTokenLogin) SavedToken = p.Token;
            }
            else if (p.ViaToken) SavedToken = "";   // expired/revoked: fall back to the form
            OnAuth?.Invoke(p);

            // Back after an involuntary drop: re-enter the room we were in.
            if (p.Ok && _rejoinRoomId.Length > 0)
            {
                string id = _rejoinRoomId, pw = _rejoinPassword;
                _rejoinRoomId = ""; _rejoinPassword = "";
                JoinRoom(id, pw);
            }
        }

        private void OnRoomJoinedPacket(RoomJoined p)
        {
            RoomPending = false;
            RoomId = p.ShareableId; RoomName = p.Name; MyRole = (RoomRole)p.MyRole;
            _roomPassword = _pendingJoinPassword; _pendingJoinPassword = "";
            OnRoomJoined?.Invoke(p);
        }

        private void OnRoomErrorPacket(RoomError p)
        {
            RoomPending = false;
            OnRoomError?.Invoke(p.Message);
        }

        private void OnRoomLeftPacket(RoomLeft p)
        {
            RoomPending = false;
            if (!InRoom || (p.ShareableId.Length > 0 && p.ShareableId != RoomId)) return;   // already left locally
            ClearRoom();
            OnRoomLeft?.Invoke(p.Reason ?? "");
            OnRosterChanged?.Invoke();
        }

        private static string DescribeDisconnect(DisconnectReason reason, string detail)
        {
            if (!string.IsNullOrEmpty(detail)) return detail;
            switch (reason)
            {
                case DisconnectReason.ConnectionFailed:
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable:
                    return "Can't reach the server.";
                case DisconnectReason.UnknownHost: return "Unknown server address.";
                case DisconnectReason.Timeout: return "Connection lost (timed out).";
                case DisconnectReason.ConnectionRejected: return "The server refused the connection (outdated mod?).";
                case DisconnectReason.RemoteConnectionClose: return "The server closed the connection.";
                case DisconnectReason.DisconnectPeerCalled: return "";
                default: return "Disconnected (" + reason + ").";
            }
        }

        private void OnRosterPacket(RoomRoster p)
        {
            // Accept when it's for our room, OR when we don't know our room yet: during a
            // join, the in-join roster broadcast can arrive just before our RoomJoined (the
            // server only ever sends us rosters for our own room, so buffering it is safe).
            if (p.ShareableId == RoomId || RoomId.Length == 0)
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

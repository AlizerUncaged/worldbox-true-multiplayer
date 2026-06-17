using System;
using NeoModLoader.General.UI.Window;   // SingleAutoLayoutWindow
using UnityEngine;
using UnityEngine.UI;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// The multiplayer manager window. Subclasses NML's SingleAutoLayoutWindow so it
    /// uses WorldBox's native window frame, title bar, close button and scroll/content
    /// layout. Our sections (login / lobby / room / settings) are built into the
    /// content area; only labels and lists refresh on events.
    /// </summary>
    public class MultiplayerWindow : SingleAutoLayoutWindow<MultiplayerWindow>
    {
        public const string WindowId = "wbmp_main";
        private static bool _created;

        private NetClient Net => NetClient.Instance;
        private Transform _host;

        private Text _statusText, _msgText;
        private GameObject _loginPanel, _lobbyPanel, _roomPanel, _settingsPanel;
        private InputField _userField, _passField;
        private InputField _roomNameField, _roomPassField, _joinIdField, _joinPassField;
        private InputField _hostField, _portField;
        private Toggle _unlistedToggle;
        private GameObject _roomListContainer, _rosterContainer, _logContainer;
        private bool _showSettings;

        // ── Static entry points (create lazily, then show) ──────────────────
        public static void Ensure() { /* creation is lazy on first Open */ }

        private static void Create()
        {
            if (_created) return;
            CreateWindow(WindowId, "WorldBox Multiplayer");
            _created = true;
        }

        public static void Open()
        {
            Create();
            ScrollWindow.showWindow(WindowId);
            if (Instance != null) { Instance.Refresh(); if (NetClient.Instance.LoggedIn) NetClient.Instance.RequestRoomList(); }
        }

        public static void OpenSettings()
        {
            Create();
            if (Instance != null) Instance._showSettings = true;
            ScrollWindow.showWindow(WindowId);
            Instance?.Refresh();
        }

        public static void Toggle()
        {
            Create();
            if (ScrollWindow.isCurrentWindow(WindowId)) { try { Instance?.ScrollWindowComponent.clickHide("right"); } catch { } }
            else { ScrollWindow.showWindow(WindowId); Instance?.Refresh(); }
        }

        // ── Build (NML calls Init once during CreateWindow) ─────────────────
        protected override void Init()
        {
            _host = ContentTransform;

            // Let sections fill the content width inside the frame.
            var lg = GetLayoutGroup();
            lg.childControlWidth = true; lg.childForceExpandWidth = true;
            lg.childControlHeight = true; lg.childForceExpandHeight = false;
            lg.spacing = 6; lg.padding = new RectOffset(6, 6, 8, 8);

            _statusText = UiKit.MakeText("Status", _host, "", 12, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.SetHeight(_statusText.gameObject, 18f);
            _msgText = UiKit.MakeText("Msg", _host, "", 11, TextAnchor.MiddleCenter, FontStyle.Italic);
            UiKit.SetHeight(_msgText.gameObject, 16f);

            BuildLoginPanel();
            BuildLobbyPanel();
            BuildRoomPanel();
            BuildSettingsPanel();
            WireNet();
            Refresh();
        }

        private void BuildLoginPanel()
        {
            _loginPanel = UiKit.MakeColumn("Login", _host, null);
            UiKit.MakeText("h", _loginPanel.transform, "Account", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            _userField = UiKit.MakeInput("User", _loginPanel.transform, "Username", false, 24);
            _passField = UiKit.MakeInput("Pass", _loginPanel.transform, "Password", true, 64);
            var row = UiKit.MakeRow("Buttons", _loginPanel.transform);
            UiKit.MakeButton("LoginBtn", row.transform, "Login",
                () => Net.Login(_userField.text, _passField.text), UiKit.ButtonRed);
            UiKit.MakeButton("RegBtn", row.transform, "Register",
                () => Net.Register(_userField.text, _passField.text), UiKit.ButtonGrey);
            UiKit.MakeText("hint", _loginPanel.transform,
                "Register: pick a username + password.", 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        }

        private void BuildLobbyPanel()
        {
            _lobbyPanel = UiKit.MakeColumn("Lobby", _host, null);

            UiKit.MakeText("ch", _lobbyPanel.transform, "Create a room", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            _roomNameField = UiKit.MakeInput("RName", _lobbyPanel.transform, "Room name", false, 40);
            _roomPassField = UiKit.MakeInput("RPass", _lobbyPanel.transform, "Password (optional, max 16)", false, 16);
            var trow = UiKit.MakeRow("TRow", _lobbyPanel.transform);
            var togGo = UiKit.MakeRect("UnlistedTog", trow.transform);
            _unlistedToggle = togGo.AddComponent<Toggle>();
            var togBg = togGo.AddComponent<Image>(); togBg.color = UiKit.InputBg;
            _unlistedToggle.targetGraphic = togBg;
            UiKit.SetHeight(togGo, 24f);
            UiKit.MakeText("TogLbl", trow.transform, "Unlisted", 12);
            UiKit.MakeButton("CreateBtn", _lobbyPanel.transform, "Create Room", () =>
                Net.CreateRoom(_roomNameField.text, _roomPassField.text,
                    _unlistedToggle.isOn ? RoomVisibility.Unlisted : RoomVisibility.Public), UiKit.ButtonRed);

            UiKit.MakeText("jh", _lobbyPanel.transform, "Join by code", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            var jrow = UiKit.MakeRow("JRow", _lobbyPanel.transform);
            _joinIdField = UiKit.MakeInput("JId", jrow.transform, "CODE", false, 4);
            _joinPassField = UiKit.MakeInput("JPass", jrow.transform, "Password", false, 16);
            UiKit.MakeButton("JoinBtn", _lobbyPanel.transform, "Join Room",
                () => Net.JoinRoom(_joinIdField.text.ToUpperInvariant(), _joinPassField.text), UiKit.ButtonGrey);

            var lrow = UiKit.MakeRow("LRow", _lobbyPanel.transform);
            UiKit.MakeText("ph", lrow.transform, "Public rooms", 14, TextAnchor.MiddleLeft, FontStyle.Bold);
            UiKit.MakeButton("Refresh", lrow.transform, "Refresh", () => Net.RequestRoomList(), UiKit.ButtonGrey);
            _roomListContainer = UiKit.MakeColumn("RoomList", _lobbyPanel.transform, null, 3f, new RectOffset(0, 0, 0, 0));
        }

        private void BuildRoomPanel()
        {
            _roomPanel = UiKit.MakeColumn("RoomP", _host, null);
            var brow = UiKit.MakeRow("RTopRow", _roomPanel.transform);
            UiKit.MakeButton("Logs", brow.transform, "Logs", () => Net.RequestLogs(), UiKit.ButtonGrey);
            UiKit.MakeButton("Resync", brow.transform, "Resync", () => Mirror.MirrorManager.Instance?.ForceSnapshotToAll(), UiKit.ButtonGrey);
            UiKit.MakeButton("Leave", brow.transform, "Leave", () => Net.LeaveRoom(), UiKit.ButtonRed);
            UiKit.MakeText("plh", _roomPanel.transform, "Players", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            _rosterContainer = UiKit.MakeColumn("Roster", _roomPanel.transform, null, 3f, new RectOffset(0, 0, 0, 0));
            UiKit.MakeText("lgh", _roomPanel.transform, "Logs", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            _logContainer = UiKit.MakeColumn("LogList", _roomPanel.transform, null, 1f, new RectOffset(0, 0, 0, 0));
        }

        private void BuildSettingsPanel()
        {
            _settingsPanel = UiKit.MakeColumn("Settings", _host, null);
            UiKit.MakeText("sh", _settingsPanel.transform, "Server", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            _hostField = UiKit.MakeInput("Host", _settingsPanel.transform, "Host (e.g. 127.0.0.1)", false, 64);
            _portField = UiKit.MakeInput("Port", _settingsPanel.transform, "Port", false, 6);
            _portField.contentType = InputField.ContentType.IntegerNumber;
            _hostField.text = ServerConfig.Host;
            _portField.text = ServerConfig.Port.ToString();
            UiKit.MakeButton("SaveServer", _settingsPanel.transform, "Save & Reconnect", () =>
            {
                string host = string.IsNullOrWhiteSpace(_hostField.text) ? "127.0.0.1" : _hostField.text.Trim();
                int port = int.TryParse(_portField.text, out var p) ? p : NetProtocol.DefaultPort;
                ServerConfig.Host = host; ServerConfig.Port = port;
                Net.Reconnect(host, port);
                _showSettings = false;
                Msg($"Reconnecting to {host}:{port}...");
                Refresh();
            }, UiKit.ButtonRed);
            var back = UiKit.MakeButton("BackBtn", _settingsPanel.transform, "Back", () => { _showSettings = false; Refresh(); }, UiKit.ButtonGrey);
        }

        // ── Net events ──────────────────────────────────────────────────────
        private void WireNet()
        {
            Net.OnStateChanged += _ => Refresh();
            Net.OnAuth += r => { Msg(r.Message); Refresh(); if (r.Ok) Net.RequestRoomList(); };
            Net.OnRoomJoined += _ => { Msg("Joined room " + Net.RoomId); Refresh(); };
            Net.OnRoomError += m => Msg(m);
            Net.OnRosterChanged += Refresh;
            Net.OnRoomListChanged += RefreshRoomList;
            Net.OnLogsChanged += RefreshLogs;
        }

        private void Msg(string m) { if (_msgText != null) _msgText.text = m ?? ""; }

        // ── Refresh ─────────────────────────────────────────────────────────
        private void Refresh()
        {
            if (_statusText == null) return;
            string conn = Net.State == ConnState.Connected ? "Connected" :
                          Net.State == ConnState.Connecting ? "Connecting..." : "Disconnected";
            string who = Net.LoggedIn ? $" | {Net.Username}" : "";
            _statusText.text = $"{conn}{who}";

            if (_settingsPanel != null && _showSettings)
            {
                _loginPanel.SetActive(false); _lobbyPanel.SetActive(false); _roomPanel.SetActive(false);
                _settingsPanel.SetActive(true);
                return;
            }
            if (_settingsPanel != null) _settingsPanel.SetActive(false);

            bool loggedIn = Net.LoggedIn;
            bool inRoom = Net.InRoom;
            _loginPanel.SetActive(!loggedIn);
            _lobbyPanel.SetActive(loggedIn && !inRoom);
            _roomPanel.SetActive(loggedIn && inRoom);

            if (inRoom) RefreshRoster();
        }

        private void RefreshRoomList()
        {
            if (_roomListContainer == null) return;
            ClearChildren(_roomListContainer);
            if (Net.RoomList.Count == 0)
                UiKit.MakeText("empty", _roomListContainer.transform, "(no public rooms)", 11, TextAnchor.MiddleLeft, FontStyle.Italic);
            foreach (var r in Net.RoomList)
            {
                string code = r.ShareableId, label = $"{r.Name}  [{code}]  {r.PlayerCount}p{(r.HasPassword ? " \U0001F512" : "")}";
                UiKit.MakeButton("room_" + code, _roomListContainer.transform, label,
                    () => Net.JoinRoom(code, _joinPassField != null ? _joinPassField.text : ""), UiKit.ButtonGrey, 12);
            }
        }

        private void RefreshRoster()
        {
            if (_rosterContainer == null) return;
            ClearChildren(_rosterContainer);
            foreach (var p in Net.Roster)
            {
                var row = UiKit.MakeRow("p_" + p.Id4, _rosterContainer.transform, 4f);
                string tag = ((RoomRole)p.Role) == RoomRole.King ? " \U0001F451" : ((RoomRole)p.Role) == RoomRole.Admin ? " ★" : "";
                bool isMe = p.Id4 == Net.Id4;
                UiKit.MakeText("n", row.transform, p.Username + tag + (isMe ? " (you)" : ""), 12);
                if (Net.IsAdmin && !isMe)
                {
                    UiKit.MakeButton("kick", row.transform, "Kick", () => Net.Kick(p.Id4), UiKit.ButtonRed, 11)
                        .GetComponent<LayoutElement>().preferredWidth = 46;
                    if (Net.IsKing && (RoomRole)p.Role == RoomRole.Player)
                        UiKit.MakeButton("prom", row.transform, "+", () => Net.Promote(p.Id4), UiKit.ButtonGrey, 11)
                            .GetComponent<LayoutElement>().preferredWidth = 28;
                    if (Net.IsKing && (RoomRole)p.Role == RoomRole.Admin)
                        UiKit.MakeButton("dem", row.transform, "-", () => Net.Demote(p.Id4), UiKit.ButtonGrey, 11)
                            .GetComponent<LayoutElement>().preferredWidth = 28;
                }
            }
        }

        private void RefreshLogs()
        {
            if (_logContainer == null) return;
            ClearChildren(_logContainer);
            int start = Mathf.Max(0, Net.Logs.Count - 12);
            for (int i = start; i < Net.Logs.Count; i++)
            {
                var e = Net.Logs[i];
                var time = DateTimeOffset.FromUnixTimeMilliseconds(e.UnixMs).LocalDateTime.ToString("HH:mm:ss");
                UiKit.MakeText("log" + i, _logContainer.transform, $"{time}  {e.Text}", 10, TextAnchor.MiddleLeft);
            }
        }

        private static void ClearChildren(GameObject go)
        {
            if (go == null) return;
            for (int i = go.transform.childCount - 1; i >= 0; i--)
                Destroy(go.transform.GetChild(i).gameObject);
        }
    }
}

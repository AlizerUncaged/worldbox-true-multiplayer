using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// The multiplayer manager window. Built entirely from code on its own overlay
    /// canvas. Three modes (login / lobby / in-room) are pre-built once so text
    /// inputs keep their content; only dynamic labels and lists refresh on events.
    /// </summary>
    public class MultiplayerWindow : MonoBehaviour
    {
        public static MultiplayerWindow Instance { get; private set; }

        private NetClient Net => NetClient.Instance;

        private GameObject _root;
        private Text _statusText, _msgText;
        private GameObject _loginPanel, _lobbyPanel, _roomPanel, _settingsPanel;
        private InputField _userField, _passField;
        private InputField _roomNameField, _roomPassField, _joinIdField, _joinPassField;
        private InputField _hostField, _portField;
        private Toggle _unlistedToggle;
        private GameObject _roomListContainer, _rosterContainer, _logContainer;
        private bool _showSettings;

        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("WBMP_Window");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<MultiplayerWindow>();
            Instance.Build();
            Instance.Hide();
        }

        public void Toggle() { if (_root.activeSelf) Hide(); else Show(); }
        public void Show() { _root.SetActive(true); Refresh(); if (Net.LoggedIn) Net.RequestRoomList(); }
        public void Hide() { _root.SetActive(false); }

        // ── Build (once) ────────────────────────────────────────────────────
        private void Build()
        {
            var canvasGo = new GameObject("WBMP_Canvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();

            // Root window panel
            var rootImg = UiKit.MakePanel("Root", canvasGo.transform, UiKit.PanelBg);
            _root = rootImg.gameObject;
            var rt = rootImg.rectTransform;
            rt.sizeDelta = new Vector2(380, 560);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            var rootCol = _root.AddComponent<VerticalLayoutGroup>();
            rootCol.spacing = 6; rootCol.padding = new RectOffset(0, 0, 0, 10);
            rootCol.childControlWidth = true; rootCol.childControlHeight = true;
            rootCol.childForceExpandWidth = true; rootCol.childForceExpandHeight = false;

            // Header (draggable) + close
            var header = UiKit.MakePanel("Header", _root.transform, UiKit.SectionBg);
            UiKit.SetHeight(header.gameObject, 34f);
            header.gameObject.AddComponent<PanelDragger>().Target = rt;
            var hrow = UiKit.MakeRow("HeaderRow", header.transform, 4f);
            var hrt = ((RectTransform)hrow.transform);
            hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one;
            UiKit.MakeText("Title", hrow.transform, "WorldBox Multiplayer", 15, TextAnchor.MiddleLeft, FontStyle.Bold);
            var gear = UiKit.MakeButton("Gear", hrow.transform, "⚙", () => { _showSettings = !_showSettings; Refresh(); }, UiKit.ButtonGrey, 14);
            UiKit.SetHeight(gear.gameObject, 26f); gear.GetComponent<LayoutElement>().preferredWidth = 34;
            var close = UiKit.MakeButton("Close", hrow.transform, "X", Hide, UiKit.ButtonRed, 14);
            UiKit.SetHeight(close.gameObject, 26f);
            close.GetComponent<LayoutElement>().preferredWidth = 30;

            _statusText = UiKit.MakeText("Status", _root.transform, "", 12, TextAnchor.MiddleCenter);
            UiKit.SetHeight(_statusText.gameObject, 18f);
            _msgText = UiKit.MakeText("Msg", _root.transform, "", 12, TextAnchor.MiddleCenter, FontStyle.Italic);
            UiKit.SetHeight(_msgText.gameObject, 18f);

            BuildLoginPanel();
            BuildLobbyPanel();
            BuildRoomPanel();
            BuildSettingsPanel();
            WireNet();
        }

        private void BuildLoginPanel()
        {
            _loginPanel = UiKit.MakeColumn("Login", _root.transform, UiKit.SectionBg);
            UiKit.MakeText("h", _loginPanel.transform, "Account", 14, TextAnchor.MiddleLeft, FontStyle.Bold);
            _userField = UiKit.MakeInput("User", _loginPanel.transform, "Username", false, 24);
            _passField = UiKit.MakeInput("Pass", _loginPanel.transform, "Password", true, 64);
            var row = UiKit.MakeRow("Buttons", _loginPanel.transform);
            UiKit.MakeButton("LoginBtn", row.transform, "Login",
                () => Net.Login(_userField.text, _passField.text), UiKit.ButtonRed);
            UiKit.MakeButton("RegBtn", row.transform, "Register",
                () => Net.Register(_userField.text, _passField.text), UiKit.ButtonGrey);
            UiKit.MakeText("hint", _loginPanel.transform,
                "Register: pick a username + password. Username must be unique.", 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        }

        private void BuildLobbyPanel()
        {
            _lobbyPanel = UiKit.MakeColumn("Lobby", _root.transform, UiKit.SectionBg);

            UiKit.MakeText("ch", _lobbyPanel.transform, "Create a room", 14, TextAnchor.MiddleLeft, FontStyle.Bold);
            _roomNameField = UiKit.MakeInput("RName", _lobbyPanel.transform, "Room name", false, 40);
            _roomPassField = UiKit.MakeInput("RPass", _lobbyPanel.transform, "Password (optional, max 16)", false, 16);
            var trow = UiKit.MakeRow("TRow", _lobbyPanel.transform);
            var togGo = UiKit.MakeRect("UnlistedTog", trow.transform);
            _unlistedToggle = togGo.AddComponent<Toggle>();
            var togBg = togGo.AddComponent<Image>(); togBg.color = UiKit.InputBg;
            _unlistedToggle.targetGraphic = togBg;
            UiKit.SetHeight(togGo, 24f);
            UiKit.MakeText("TogLbl", trow.transform, "Unlisted (hidden from list)", 12);
            UiKit.MakeButton("CreateBtn", _lobbyPanel.transform, "Create Room", () =>
                Net.CreateRoom(_roomNameField.text, _roomPassField.text,
                    _unlistedToggle.isOn ? RoomVisibility.Unlisted : RoomVisibility.Public), UiKit.ButtonRed);

            UiKit.MakeText("jh", _lobbyPanel.transform, "Join by code", 14, TextAnchor.MiddleLeft, FontStyle.Bold);
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
            _roomPanel = UiKit.MakeColumn("RoomP", _root.transform, UiKit.SectionBg);
            var brow = UiKit.MakeRow("RTopRow", _roomPanel.transform);
            UiKit.MakeButton("Logs", brow.transform, "Server Logs", () => Net.RequestLogs(), UiKit.ButtonGrey);
            UiKit.MakeButton("Leave", brow.transform, "Leave Room", () => Net.LeaveRoom(), UiKit.ButtonRed);
            UiKit.MakeText("plh", _roomPanel.transform, "Players", 14, TextAnchor.MiddleLeft, FontStyle.Bold);
            _rosterContainer = UiKit.MakeColumn("Roster", _roomPanel.transform, null, 3f, new RectOffset(0, 0, 0, 0));
            UiKit.MakeText("lgh", _roomPanel.transform, "Logs", 14, TextAnchor.MiddleLeft, FontStyle.Bold);
            _logContainer = UiKit.MakeColumn("LogList", _roomPanel.transform, null, 1f, new RectOffset(0, 0, 0, 0));
        }

        private void BuildSettingsPanel()
        {
            _settingsPanel = UiKit.MakeColumn("Settings", _root.transform, UiKit.SectionBg);
            UiKit.MakeText("sh", _settingsPanel.transform, "Server", 14, TextAnchor.MiddleLeft, FontStyle.Bold);
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
            UiKit.MakeText("shint", _settingsPanel.transform, "Default port is 40441. Settings are saved locally.",
                11, TextAnchor.MiddleLeft, FontStyle.Italic);
        }

        /// <summary>Open the window directly to the server settings (used by the tab button).</summary>
        public void ShowSettings() { _showSettings = true; Show(); }

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
            if (_root == null) return;
            string conn = Net.State == ConnState.Connected ? "Connected" :
                          Net.State == ConnState.Connecting ? "Connecting..." : "Disconnected";
            string who = Net.LoggedIn ? $" | {Net.Username}" : "";
            _statusText.text = $"{conn}{who}";

            // Settings overrides everything when open.
            if (_settingsPanel != null && _showSettings)
            {
                _loginPanel.SetActive(false); _lobbyPanel.SetActive(false); _roomPanel.SetActive(false);
                _settingsPanel.SetActive(true);
                return;
            }
            if (_settingsPanel != null) _settingsPanel.SetActive(false);

            bool loggedIn = Net.LoggedIn;
            bool inRoom = Net.InRoom;

            // Panels are visible by session state (not connection) so the UI is
            // always visible; the status line shows whether we're connected, and
            // the action buttons simply no-op until the connection is up.
            _loginPanel.SetActive(!loggedIn);
            _lobbyPanel.SetActive(loggedIn && !inRoom);
            _roomPanel.SetActive(loggedIn && inRoom);

            if (inRoom) RefreshRoster();
        }

        private void RefreshRoomList()
        {
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
                // Moderation buttons for admins/king, on others.
                if (Net.IsAdmin && !isMe)
                {
                    UiKit.MakeButton("kick", row.transform, "Kick", () => Net.Kick(p.Id4), UiKit.ButtonRed, 11)
                        .GetComponent<LayoutElement>().preferredWidth = 50;
                    if (Net.IsKing && (RoomRole)p.Role == RoomRole.Player)
                        UiKit.MakeButton("prom", row.transform, "Promote", () => Net.Promote(p.Id4), UiKit.ButtonGrey, 11)
                            .GetComponent<LayoutElement>().preferredWidth = 64;
                    if (Net.IsKing && (RoomRole)p.Role == RoomRole.Admin)
                        UiKit.MakeButton("dem", row.transform, "Demote", () => Net.Demote(p.Id4), UiKit.ButtonGrey, 11)
                            .GetComponent<LayoutElement>().preferredWidth = 64;
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
                var time = System.DateTimeOffset.FromUnixTimeMilliseconds(e.UnixMs).LocalDateTime.ToString("HH:mm:ss");
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

    /// <summary>Drag a target RectTransform by its header.</summary>
    public class PanelDragger : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public RectTransform Target;
        private Vector2 _offset;
        public void OnBeginDrag(PointerEventData e)
        {
            if (Target == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)Target.parent, e.position, e.pressEventCamera, out var local);
            _offset = Target.anchoredPosition - local;
        }
        public void OnDrag(PointerEventData e)
        {
            if (Target == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)Target.parent, e.position, e.pressEventCamera, out var local))
                Target.anchoredPosition = local + _offset;
        }
    }
}

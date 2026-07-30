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
        public new const string WindowId = "wbmp_main";
        private static bool _created;

        private NetClient Net => NetClient.Instance;
        private Transform _host;

        private Text _statusText, _msgText;
        private GameObject _loginPanel, _lobbyPanel, _createPanel, _joinPanel, _roomPanel, _settingsPanel;
        private InputField _userField, _passField;
        private InputField _roomNameField, _roomPassField, _joinIdField, _joinPassField;
        private InputField _hostField, _portField;
        private Toggle _unlistedToggle;
        private GameObject _roomListContainer, _rosterContainer;
        private bool _showSettings;

        // Nested lobby: the door opens a MENU (two square icon buttons + public rooms);
        // Create / Join each live on their own sub-panel with a Back button.
        private enum LobbyView { Menu, Create, Join }
        private LobbyView _lobbyView = LobbyView.Menu;

        // ── Static entry points (create lazily, then show) ──────────────────
        public static void Ensure() { /* creation is lazy on first Open */ }

        private static void Create()
        {
            if (_created) return;
            // NML uses the title as a locale key; register it so LocalizedTextManager
            // stops logging "missing text: WorldBox Multiplayer" on every open.
            try { NeoModLoader.General.LM.AddToCurrentLocale("WorldBox Multiplayer", "WorldBox Multiplayer"); } catch { }
            CreateWindow(WindowId, "WorldBox Multiplayer");
            // NML leaves the freshly built window visible, so the login form greeted the
            // player on every boot (the tab prewarms the window at creation). Deactivate
            // the clone immediately - showWindow() reactivates it on an explicit open.
            try { Instance?.ScrollWindowComponent.gameObject.SetActive(false); } catch { }
            _created = true;
        }

        /// <summary>Create the window now (hidden) so the first real Open() is a clean show, not a create+show race.</summary>
        public static void Prewarm() => Create();

        public static void Open()
        {
            Create();
            if (Instance != null)
            {
                Instance._showSettings = false;      // main buttons always show the normal panels
                Instance._lobbyView = LobbyView.Menu; // and the lobby starts at its menu
            }
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

            // Let sections fill the content width inside the frame, centered.
            var lg = GetLayoutGroup();
            lg.childControlWidth = true; lg.childForceExpandWidth = true;
            lg.childControlHeight = true; lg.childForceExpandHeight = false;
            lg.childAlignment = TextAnchor.UpperCenter;
            // Inset from the decorative frame border (left a touch more) + leave room for the right scrollbar.
            lg.spacing = 8; lg.padding = new RectOffset(18, 22, 10, 12);

            // Title banner.
            var banner = IconFactory.GetEmbedded("title-banner.png", padToSquare: false);
            if (banner != null)
            {
                var bgo = UiKit.MakeRect("Banner", _host);
                var bimg = bgo.AddComponent<Image>();
                bimg.sprite = banner; bimg.preserveAspect = true; bimg.raycastTarget = false;
                UiKit.SetHeight(bgo, 60f);
            }

            _statusText = UiKit.MakeText("Status", _host, "", 12, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.SetHeight(_statusText.gameObject, 18f);
            _msgText = UiKit.MakeText("Msg", _host, "", 11, TextAnchor.MiddleCenter, FontStyle.Italic);
            UiKit.SetHeight(_msgText.gameObject, 16f);

            BuildLoginPanel();
            BuildLobbyPanel();
            BuildCreatePanel();
            BuildJoinPanel();
            BuildRoomPanel();
            BuildSettingsPanel();
            FixClickThrough();
            WireNet();
            Refresh();
        }

        /// <summary>
        /// The cloned native frame ships decorative graphics (logo, frame plates) whose
        /// rects extend over the content area and sit ABOVE it in raycast order - clicks
        /// on the input fields died on them (buttons only worked because they fall outside
        /// those rects). Interactive controls are the only legitimate click targets, so
        /// every graphic without a Selectable on itself or an ancestor stops raycasting,
        /// and one invisible full-window plate (bottom of the stack) keeps the window
        /// swallowing clicks that would otherwise fall through to the world map.
        /// </summary>
        private void FixClickThrough()
        {
            Transform root;
            try { root = ScrollWindowComponent.transform; } catch { root = transform; }
            int kept = 0, disabled = 0;
            foreach (var g in root.GetComponentsInChildren<Graphic>(true))
            {
                bool interactive = false;
                for (var t = g.transform; t != null && !interactive; t = t.parent)
                    interactive = t.GetComponent<Selectable>() != null;
                g.raycastTarget = interactive;
                if (interactive) kept++; else disabled++;
            }
            ModLog.Info($"[UI] raycast fix: root={root.name} parent={(root.parent != null ? root.parent.name : "null")} kept={kept} disabled={disabled}");
            var plate = UiKit.MakeRect("ClickPlate", root);
            plate.transform.SetAsFirstSibling();
            var prt = plate.GetComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = prt.offsetMax = Vector2.zero;
            plate.AddComponent<Image>().color = Color.clear;   // invisible, still raycastable
        }

        private void BuildLoginPanel()
        {
            _loginPanel = UiKit.MakeColumn("Login", _host, UiKit.CardBg, 6f, new RectOffset(10, 10, 8, 8));
            UiKit.MakeText("h", _loginPanel.transform, "Account", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            _userField = UiKit.MakeInput("User", _loginPanel.transform, "Username", false, 24);
            _passField = UiKit.MakeInput("Pass", _loginPanel.transform, "Password", true, 64);
#if DEBUG
            if (!string.IsNullOrEmpty(Dev.DevAutoLogin.User)) { _userField.text = Dev.DevAutoLogin.User; _passField.text = Dev.DevAutoLogin.User; }
#endif
            var row = UiKit.MakeRow("Buttons", _loginPanel.transform);
            UiKit.MakeButton("LoginBtn", row.transform, "Login",
                () => Net.Login(_userField.text, _passField.text), UiKit.ButtonRed);
            UiKit.MakeButton("RegBtn", row.transform, "Register",
                () => Net.Register(_userField.text, _passField.text), UiKit.ButtonGrey);
            UiKit.MakeText("hint", _loginPanel.transform,
                "Register: pick a username + password.", 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        }

        // Lobby MENU: two square icon buttons (native Other-window style) + public rooms.
        private void BuildLobbyPanel()
        {
            _lobbyPanel = UiKit.MakeColumn("Lobby", _host, UiKit.CardBg, 6f, new RectOffset(10, 10, 8, 8));

            var brow = UiKit.MakeRow("MenuRow", _lobbyPanel.transform, 14f);
            var bh = brow.GetComponent<HorizontalLayoutGroup>();
            bh.childForceExpandWidth = false;
            bh.childAlignment = TextAnchor.MiddleCenter;
            UiKit.SetHeight(brow, 68f);
            UiKit.MakeIconButton("CreateRoomBtn", brow.transform, "Create a room",
                IconFactory.GetEmbedded("create-room-icon.png"),
                () => { _lobbyView = LobbyView.Create; Refresh(); });
            UiKit.MakeIconButton("JoinRoomBtn", brow.transform, "Join by code",
                IconFactory.GetEmbedded("join-room-icon.png"),
                () => { _lobbyView = LobbyView.Join; Refresh(); });

            var lrow = UiKit.MakeRow("LRow", _lobbyPanel.transform);
            UiKit.MakeText("ph", lrow.transform, "Public rooms", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            UiKit.MakeButton("Refresh", lrow.transform, "Refresh", () => Net.RequestRoomList(), UiKit.ButtonGrey, 12, 24f, 80f);
            _roomListContainer = UiKit.MakeColumn("RoomList", _lobbyPanel.transform, null, 3f, new RectOffset(0, 0, 0, 0));
        }

        private void BuildCreatePanel()
        {
            _createPanel = UiKit.MakeColumn("CreateP", _host, UiKit.CardBg, 6f, new RectOffset(10, 10, 8, 8));
            UiKit.MakeText("ch", _createPanel.transform, "Create a room", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            _roomNameField = UiKit.MakeInput("RName", _createPanel.transform, "Room name", false, 40);
            _roomPassField = UiKit.MakeInput("RPass", _createPanel.transform, "Password (optional, max 16)", false, 16);
            var trow = UiKit.MakeRow("TRow", _createPanel.transform);
            var togGo = UiKit.MakeRect("UnlistedTog", trow.transform);
            _unlistedToggle = togGo.AddComponent<Toggle>();
            var togBg = togGo.AddComponent<Image>(); togBg.color = UiKit.InputBg;
            _unlistedToggle.targetGraphic = togBg;
            UiKit.SetHeight(togGo, 24f);
            UiKit.MakeText("TogLbl", trow.transform, "Unlisted", 12);
            UiKit.MakeButton("CreateBtn", _createPanel.transform, "Create Room", () =>
                Net.CreateRoom(_roomNameField.text, _roomPassField.text,
                    _unlistedToggle.isOn ? RoomVisibility.Unlisted : RoomVisibility.Public), UiKit.ButtonRed);
            UiKit.MakeButton("CreateBack", _createPanel.transform, "Back",
                () => { _lobbyView = LobbyView.Menu; Refresh(); }, UiKit.ButtonGrey);
        }

        private void BuildJoinPanel()
        {
            _joinPanel = UiKit.MakeColumn("JoinP", _host, UiKit.CardBg, 6f, new RectOffset(10, 10, 8, 8));
            UiKit.MakeText("jh", _joinPanel.transform, "Join by code", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            var jrow = UiKit.MakeRow("JRow", _joinPanel.transform);
            _joinIdField = UiKit.MakeInput("JId", jrow.transform, "CODE", false, 4);
            _joinPassField = UiKit.MakeInput("JPass", jrow.transform, "Password", false, 16);
            UiKit.MakeButton("JoinBtn", _joinPanel.transform, "Join Room",
                () => Net.JoinRoom(_joinIdField.text.ToUpperInvariant(), _joinPassField.text), UiKit.ButtonRed);
            UiKit.MakeButton("JoinBack", _joinPanel.transform, "Back",
                () => { _lobbyView = LobbyView.Menu; Refresh(); }, UiKit.ButtonGrey);
        }

        private void BuildRoomPanel()
        {
            _roomPanel = UiKit.MakeColumn("RoomP", _host, UiKit.CardBg, 6f, new RectOffset(10, 10, 8, 8));
            var brow = UiKit.MakeRow("RTopRow", _roomPanel.transform);
            UiKit.MakeButton("Resync", brow.transform, "Resync", () => Mirror.MirrorManager.Instance?.ForceSnapshotToAll(), UiKit.ButtonGrey);
            UiKit.MakeButton("Leave", brow.transform, "Leave", () => Net.LeaveRoom(), UiKit.ButtonRed);
            UiKit.MakeText("plh", _roomPanel.transform, "Players", 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);
            _rosterContainer = UiKit.MakeColumn("Roster", _roomPanel.transform, null, 3f, new RectOffset(0, 0, 0, 0));
        }

        private void BuildSettingsPanel()
        {
            _settingsPanel = UiKit.MakeColumn("Settings", _host, UiKit.CardBg, 6f, new RectOffset(10, 10, 8, 8));
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
                _loginPanel.SetActive(false); _lobbyPanel.SetActive(false);
                _createPanel.SetActive(false); _joinPanel.SetActive(false); _roomPanel.SetActive(false);
                _settingsPanel.SetActive(true);
                return;
            }
            if (_settingsPanel != null) _settingsPanel.SetActive(false);

            bool loggedIn = Net.LoggedIn;
            bool inRoom = Net.InRoom;
            bool lobby = loggedIn && !inRoom;
            if (!lobby) _lobbyView = LobbyView.Menu;   // leaving/joining resets the nesting
            _loginPanel.SetActive(!loggedIn);
            _lobbyPanel.SetActive(lobby && _lobbyView == LobbyView.Menu);
            _createPanel.SetActive(lobby && _lobbyView == LobbyView.Create);
            _joinPanel.SetActive(lobby && _lobbyView == LobbyView.Join);
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
                // "[name max 10 chars] - [N] players" - the code is join plumbing, never shown.
                string code = r.ShareableId;
                string name = string.IsNullOrWhiteSpace(r.Name) ? "room" : r.Name.Trim();
                if (name.Length > 10) name = name.Substring(0, 10);
                string label = $"{name} - {r.PlayerCount} player{(r.PlayerCount == 1 ? "" : "s")}";
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
                // Fixed-height row, vertically centered: name stretches, action buttons are
                // compact fixed-width so they never balloon or sit off-line with the text.
                var row = UiKit.MakeRow("p_" + p.Id4, _rosterContainer.transform, 4f);
                var hl = row.GetComponent<HorizontalLayoutGroup>();
                hl.childAlignment = TextAnchor.MiddleLeft;
                hl.childForceExpandWidth = false;
                UiKit.SetHeight(row, 26f);
                string tag = ((RoomRole)p.Role) == RoomRole.King ? " \U0001F451 God King" : ((RoomRole)p.Role) == RoomRole.Admin ? " ★ Admin" : "";
                bool isMe = p.Id4 == Net.Id4;
                var nameText = UiKit.MakeText("n", row.transform, p.Username + tag + (isMe ? " (you)" : ""), 12);
                var nle = nameText.gameObject.AddComponent<LayoutElement>();
                nle.flexibleWidth = 1f;   // name takes the slack; buttons keep their size
                if (Net.IsAdmin && !isMe)
                {
                    UiKit.MakeButton("kick", row.transform, "Kick", () => Net.Kick(p.Id4), UiKit.ButtonRed, 11, 22f, 48f);
                    if (Net.IsKing && (RoomRole)p.Role == RoomRole.Player)
                        UiKit.MakeButton("prom", row.transform, "+", () => Net.Promote(p.Id4), UiKit.ButtonGrey, 11, 22f, 26f);
                    if (Net.IsKing && (RoomRole)p.Role == RoomRole.Admin)
                        UiKit.MakeButton("dem", row.transform, "-", () => Net.Demote(p.Id4), UiKit.ButtonGrey, 11, 22f, 26f);
                }
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

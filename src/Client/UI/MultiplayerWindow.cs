using System;
using System.Collections.Generic;
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
    /// layout. Sections (login / lobby / create / join / room / settings) are built once;
    /// <see cref="Refresh"/> only toggles them and updates labels, and every action
    /// button is gated on the connection + any request in flight, so nothing can be
    /// clicked into the void.
    /// </summary>
    public class MultiplayerWindow : SingleAutoLayoutWindow<MultiplayerWindow>
    {
        public new const string WindowId = "wbmp_main";
        private static bool _created;

        private NetClient Net => NetClient.Instance;
        private Transform _host;

        private Text _statusText, _msgText;
        private float _msgClearAt;
        private GameObject _loginPanel, _lobbyPanel, _createPanel, _joinPanel, _roomPanel, _settingsPanel;
        private readonly List<GameObject> _loginForm = new List<GameObject>();
        private Text _resumeText;
        private InputField _userField, _passField;
        private InputField _roomNameField, _roomPassField, _joinIdField, _joinPassField;
        private InputField _hostField, _portField;
        private Button _loginBtn, _registerBtn, _rememberBtn, _createBtn, _joinBtn, _visibilityBtn, _resyncBtn, _leaveBtn;
        private Text _whoText, _joinHint, _roomTitle, _roomCode, _roomRoleText, _playersHeader;
        private GameObject _roomListContainer, _rosterContainer;
        private bool _showSettings;
        private bool _unlisted;
        private ScrollRect _scroll;
        private GameObject _moreBelow;
        private float _nextListRefresh;

        private const string RememberPref = "wbmp_remember";
        private static bool Remember
        {
            get => PlayerPrefs.GetInt(RememberPref, 1) == 1;
            set { PlayerPrefs.SetInt(RememberPref, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        // Nested lobby: the door opens a MENU (two square icon buttons + room list);
        // Create / Join each live on their own sub-panel with a Back button.
        private enum LobbyView { Menu, Create, Join }
        private LobbyView _lobbyView = LobbyView.Menu;

        private enum MsgKind { Info, Ok, Error }

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
            Instance?.OnShown();
        }

        public static void OpenSettings()
        {
            Create();
            if (Instance != null) Instance._showSettings = true;
            ScrollWindow.showWindow(WindowId);
            Instance?.OnShown();
        }

        public static void Toggle()
        {
            Create();
            if (IsShowing) { try { Instance?.ScrollWindowComponent.clickHide("right"); } catch { } }
            else { ScrollWindow.showWindow(WindowId); Instance?.OnShown(); }
        }

        private static bool IsShowing
        {
            get { try { return ScrollWindow.isCurrentWindow(WindowId); } catch { return false; } }
        }

        private void OnShown()
        {
            if (_hostField != null) { _hostField.text = ServerConfig.Host; _portField.text = ServerConfig.Port.ToString(); }
            Refresh();
            if (Net.LoggedIn && !Net.InRoom) { Net.RequestRoomList(); _nextListRefresh = Time.unscaledTime + 5f; }
        }

        /// <summary>Per-frame upkeep, driven by NetPump: keyboard (Enter submits, Tab
        /// moves between fields), message expiry, and live room-list refresh.</summary>
        public static void Tick() => Instance?.TickInstance();

        private void TickInstance()
        {
            if (_statusText == null) return;
            if (_msgClearAt > 0f && Time.unscaledTime >= _msgClearAt) { _msgClearAt = 0f; _msgText.text = ""; }
            if (!IsShowing) return;
            UpdateMoreBelow();

            if (Net.LoggedIn && !Net.InRoom && _lobbyView == LobbyView.Menu && !_showSettings
                && Time.unscaledTime >= _nextListRefresh)
            {
                _nextListRefresh = Time.unscaledTime + 5f;
                Net.RequestRoomList();
            }

            bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            bool tab = Input.GetKeyDown(KeyCode.Tab);
            if (!enter && !tab) return;

            if (_loginPanel.activeInHierarchy)
            {
                if (tab && _userField.isFocused) Focus(_passField);
                else if (tab && _passField.isFocused) Focus(_userField);
                else if (enter && (_userField.isFocused || _passField.isFocused || NothingFocused())) DoLogin();
            }
            else if (_joinPanel.activeInHierarchy)
            {
                if (tab && _joinIdField.isFocused) Focus(_joinPassField);
                else if (tab && _joinPassField.isFocused) Focus(_joinIdField);
                else if (enter) DoJoin();
            }
            else if (_createPanel.activeInHierarchy)
            {
                if (tab && _roomNameField.isFocused) Focus(_roomPassField);
                else if (tab && _roomPassField.isFocused) Focus(_roomNameField);
                else if (enter) DoCreate();
            }
        }

        private bool NothingFocused() =>
            UnityEngine.EventSystems.EventSystem.current == null
            || UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject == null;

        private static void Focus(InputField f)
        {
            if (f == null) return;
            f.Select();
            f.ActivateInputField();
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
                UiKit.SetHeight(bgo, 46f);
            }

            _statusText = UiKit.MakeText("Status", _host, "", 12, TextAnchor.MiddleCenter, FontStyle.Bold);
            _statusText.supportRichText = false;
            UiKit.SetHeight(_statusText.gameObject, 18f);
            _msgText = UiKit.MakeText("Msg", _host, "", 11, TextAnchor.MiddleCenter, FontStyle.Italic);
            _msgText.supportRichText = false;
            var mle = _msgText.gameObject.AddComponent<LayoutElement>();
            mle.minHeight = 16f;

            BuildLoginPanel();
            BuildLobbyPanel();
            BuildCreatePanel();
            BuildJoinPanel();
            BuildRoomPanel();
            BuildSettingsPanel();
            FixClickThrough();
            SetupScrolling();
            WireNet();
            Refresh();
        }

        /// <summary>
        /// Mouse-wheel scrolling + a "more below" hint. FixClickThrough turns raycasts off on
        /// every decorative graphic, which also starved the ScrollRect of wheel events (they
        /// need a raycast hit somewhere inside it). A transparent plate on the viewport,
        /// behind the content, gives the wheel something to land on without stealing clicks.
        /// </summary>
        private void SetupScrolling()
        {
            // Walk up by hand: GetComponentInParent skips inactive objects, and the window
            // is still inactive while NML runs Init.
            for (var t = _host; t != null && _scroll == null; t = t.parent) _scroll = t.GetComponent<ScrollRect>();
            if (_scroll == null) { ModLog.Warn("[UI] no ScrollRect above the window content"); return; }
            _scroll.scrollSensitivity = 28f;
            var vp = _scroll.viewport != null ? _scroll.viewport : (RectTransform)_scroll.transform;
            var img = vp.GetComponent<Image>();
            if (img == null) { img = vp.gameObject.AddComponent<Image>(); img.color = Color.clear; }
            img.raycastTarget = true;

            // Bottom-center pill, drawn over the viewport, never blocks clicks.
            _moreBelow = UiKit.MakeRect("MoreBelow", _scroll.transform);
            var rt = _moreBelow.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f); rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(112f, 16f);
            rt.anchoredPosition = new Vector2(0f, 2f);
            var bg = _moreBelow.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.07f, 0.85f);
            bg.raycastTarget = false;
            var label = UiKit.MakeText("Label", _moreBelow.transform, "more below, scroll", 9, TextAnchor.MiddleCenter, FontStyle.Bold, UiKit.Orange);
            label.raycastTarget = false;
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            _moreBelow.SetActive(false);
        }

        private void UpdateMoreBelow()
        {
            if (_scroll == null || _moreBelow == null || _scroll.content == null) return;
            var vp = _scroll.viewport != null ? _scroll.viewport : (RectTransform)_scroll.transform;
            float overflow = _scroll.content.rect.height - vp.rect.height;
            bool show = overflow > 8f && _scroll.verticalNormalizedPosition > 0.02f;
            if (_moreBelow.activeSelf != show) _moreBelow.SetActive(show);
            if (show) _moreBelow.transform.SetAsLastSibling();
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

        private static GameObject Card(string name, Transform parent) =>
            UiKit.MakeColumn(name, parent, UiKit.CardBg, 6f, new RectOffset(10, 10, 8, 8));

        private static Text Header(Transform parent, string text) =>
            UiKit.MakeText("h", parent, text, 14, TextAnchor.MiddleLeft, FontStyle.Bold, UiKit.Orange);

        private static Text Hint(Transform parent, string text)
        {
            var t = UiKit.MakeText("hint", parent, text, 11, TextAnchor.MiddleLeft, FontStyle.Italic, new Color(1f, 1f, 1f, 0.7f));
            t.supportRichText = false;
            return t;
        }

        private void BuildLoginPanel()
        {
            _loginPanel = Card("Login", _host);
            var t = _loginPanel.transform;
            Header(t, "Account");
            _resumeText = Hint(t, "Signing you back in...");
            _userField = UiKit.MakeInput("User", t, "Username", false, 24);
            _passField = UiKit.MakeInput("Pass", t, "Password", true, 64);
#if DEBUG
            if (!string.IsNullOrEmpty(Dev.DevAutoLogin.User)) { _userField.text = Dev.DevAutoLogin.User; _passField.text = Dev.DevAutoLogin.User; }
#endif
            _rememberBtn = UiKit.MakeButton("Remember", t, RememberLabel(), () =>
            {
                Remember = !Remember;
                SetLabel(_rememberBtn, RememberLabel());
            }, UiKit.ButtonGrey, 12, 24f);
            var row = UiKit.MakeRow("Buttons", t);
            _loginBtn = UiKit.MakeButton("LoginBtn", row.transform, "Log in", DoLogin, UiKit.ButtonRed);
            _registerBtn = UiKit.MakeButton("RegBtn", row.transform, "Register", DoRegister, UiKit.ButtonGrey);
            var hint = Hint(t, "New here? Pick a username + password and press Register.");
            var srv = UiKit.MakeButton("ServerBtn", t, "Server settings...", () => { _showSettings = true; Refresh(); }, UiKit.ButtonGrey, 11, 22f);
            _loginForm.AddRange(new[] { _userField.gameObject, _passField.gameObject, _rememberBtn.gameObject, row, hint.gameObject, srv.gameObject });
        }

        private static string RememberLabel() => Remember ? "[x] Keep me signed in" : "[  ] Keep me signed in";

        private void DoLogin()
        {
            string u = _userField.text.Trim(), p = _passField.text;
            if (u.Length == 0 || p.Length == 0) { Msg("Enter your username and password.", MsgKind.Error); return; }
            Net.Login(u, p, Remember);
            Msg("Logging in...", MsgKind.Info);
            Refresh();
        }

        private void DoRegister()
        {
            string u = _userField.text.Trim(), p = _passField.text;
            if (u.Length < 3) { Msg("Pick a username (3-24 letters/digits).", MsgKind.Error); return; }
            if (p.Length < 4) { Msg("Pick a password (at least 4 characters).", MsgKind.Error); return; }
            Net.Register(u, p);
            Msg("Creating your account...", MsgKind.Info);
            Refresh();
        }

        // Lobby MENU: who I am + two square icon buttons + lobbies/public rooms.
        private void BuildLobbyPanel()
        {
            _lobbyPanel = Card("Lobby", _host);
            var t = _lobbyPanel.transform;

            var whoRow = UiKit.MakeRow("WhoRow", t, 4f);
            var wh = whoRow.GetComponent<HorizontalLayoutGroup>();
            wh.childForceExpandWidth = false; wh.childAlignment = TextAnchor.MiddleLeft;
            UiKit.SetHeight(whoRow, 24f);
            _whoText = UiKit.MakeText("Who", whoRow.transform, "", 12, TextAnchor.MiddleLeft, FontStyle.Bold);
            _whoText.supportRichText = false;
            _whoText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            UiKit.MakeButton("Logout", whoRow.transform, "Log out", () => { Net.Logout(); Msg("Logged out.", MsgKind.Info); Refresh(); },
                UiKit.ButtonGrey, 11, 22f, 64f);

            var brow = UiKit.MakeRow("MenuRow", t, 14f);
            var bh = brow.GetComponent<HorizontalLayoutGroup>();
            bh.childForceExpandWidth = false;
            bh.childAlignment = TextAnchor.MiddleCenter;
            UiKit.SetHeight(brow, 58f);
            UiKit.MakeIconButton("CreateRoomBtn", brow.transform, "Create a room",
                IconFactory.GetEmbedded("create-room-icon.png"),
                () => { _lobbyView = LobbyView.Create; Refresh(); Focus(_roomNameField); }, 36f);
            UiKit.MakeIconButton("JoinRoomBtn", brow.transform, "Join by code",
                IconFactory.GetEmbedded("join-room-icon.png"),
                () => OpenJoin("", ""), 36f);

            var lrow = UiKit.MakeRow("LRow", t);
            Header(lrow.transform, "Lobbies & rooms");
            UiKit.MakeButton("Refresh", lrow.transform, "Refresh", () => { Net.RequestRoomList(); _nextListRefresh = Time.unscaledTime + 5f; },
                UiKit.ButtonGrey, 12, 24f, 80f);
            _roomListContainer = UiKit.MakeColumn("RoomList", t, null, 3f, new RectOffset(0, 0, 0, 0));
        }

        private void BuildCreatePanel()
        {
            _createPanel = Card("CreateP", _host);
            var t = _createPanel.transform;
            Header(t, "Create a room");
            Hint(t, "You become its Owner: your current world is the one everyone plays in.");
            _roomNameField = UiKit.MakeInput("RName", t, "Room name", false, 40);
            _roomPassField = UiKit.MakeInput("RPass", t, "Password (optional, max 16)", false, NetProtocol.MaxPasswordLength);
            _visibilityBtn = UiKit.MakeButton("Visibility", t, VisibilityLabel(), () =>
            {
                _unlisted = !_unlisted;
                SetLabel(_visibilityBtn, VisibilityLabel());
            }, UiKit.ButtonGrey, 12, 24f);
            _createBtn = UiKit.MakeButton("CreateBtn", t, "Create room", DoCreate, UiKit.ButtonRed);
            UiKit.MakeButton("CreateBack", t, "Back", () => { _lobbyView = LobbyView.Menu; Refresh(); }, UiKit.ButtonGrey);
        }

        private string VisibilityLabel() => _unlisted ? "Unlisted: join by code only" : "Public: shown in the room list";

        private void DoCreate()
        {
            Net.CreateRoom(_roomNameField.text, _roomPassField.text,
                _unlisted ? RoomVisibility.Unlisted : RoomVisibility.Public);
            Msg("Creating room...", MsgKind.Info);
            Refresh();
        }

        private void BuildJoinPanel()
        {
            _joinPanel = Card("JoinP", _host);
            var t = _joinPanel.transform;
            Header(t, "Join a room");
            _joinHint = Hint(t, "Enter the 4-character room code a friend shared.");
            var jrow = UiKit.MakeRow("JRow", t);
            _joinIdField = UiKit.MakeInput("JId", jrow.transform, "CODE", false, NetProtocol.ShareableIdLength);
            _joinIdField.onValidateInput += (text, index, c) => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '\0';
            _joinPassField = UiKit.MakeInput("JPass", jrow.transform, "Password", false, NetProtocol.MaxPasswordLength);
            _joinBtn = UiKit.MakeButton("JoinBtn", t, "Join room", DoJoin, UiKit.ButtonRed);
            UiKit.MakeButton("JoinBack", t, "Back", () => { _lobbyView = LobbyView.Menu; Refresh(); }, UiKit.ButtonGrey);
        }

        private void OpenJoin(string code, string roomName)
        {
            _lobbyView = LobbyView.Join;
            _joinIdField.text = code ?? "";
            _joinPassField.text = "";
            _joinHint.text = string.IsNullOrEmpty(roomName)
                ? "Enter the 4-character room code a friend shared."
                : $"\"{roomName}\" is password protected.";
            Refresh();
            Focus(string.IsNullOrEmpty(code) ? _joinIdField : _joinPassField);
        }

        private void DoJoin()
        {
            string code = _joinIdField.text.Trim().ToUpperInvariant();
            if (code.Length != NetProtocol.ShareableIdLength) { Msg("Room codes are 4 characters.", MsgKind.Error); return; }
            Net.JoinRoom(code, _joinPassField.text);
            Msg("Joining " + code + "...", MsgKind.Info);
            Refresh();
        }

        private void BuildRoomPanel()
        {
            _roomPanel = Card("RoomP", _host);
            var t = _roomPanel.transform;
            _roomTitle = Header(t, "");
            _roomTitle.supportRichText = false;

            var crow = UiKit.MakeRow("CodeRow", t, 4f);
            var ch = crow.GetComponent<HorizontalLayoutGroup>();
            ch.childForceExpandWidth = false; ch.childAlignment = TextAnchor.MiddleLeft;
            UiKit.SetHeight(crow, 24f);
            _roomCode = UiKit.MakeText("Code", crow.transform, "", 12, TextAnchor.MiddleLeft, FontStyle.Bold);
            _roomCode.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            UiKit.MakeButton("Copy", crow.transform, "Copy code", () =>
            {
                GUIUtility.systemCopyBuffer = Net.RoomId;
                Msg("Room code copied - share it with your friends!", MsgKind.Ok);
            }, UiKit.ButtonGrey, 11, 22f, 84f);

            _roomRoleText = Hint(t, "");

            var brow = UiKit.MakeRow("RTopRow", t);
            // King: push the world to everyone. Follower: ask the server to have the king
            // resend it to us (rate-limited server-side). Both give visible feedback.
            _resyncBtn = UiKit.MakeButton("Resync", brow.transform, "Resync", () =>
            {
                if (Net.IsKing)
                {
                    Mirror.MirrorManager.Instance?.ForceSnapshotToAll();
                    Msg("Sending your world to all players...", MsgKind.Info);
                }
                else
                {
                    Net.RequestResync();
                    Msg("Resync requested - the world will arrive shortly.", MsgKind.Info);
                }
            }, UiKit.ButtonGrey);
            _leaveBtn = UiKit.MakeButton("Leave", brow.transform, "Leave", () => Net.LeaveRoom(), UiKit.ButtonRed);
            _playersHeader = Header(t, "Players");
            _rosterContainer = UiKit.MakeColumn("Roster", t, null, 3f, new RectOffset(0, 0, 0, 0));
        }

        private void BuildSettingsPanel()
        {
            _settingsPanel = Card("Settings", _host);
            var t = _settingsPanel.transform;
            Header(t, "Server");
            Hint(t, "Play on the official server, or point the mod at your own.");
            _hostField = UiKit.MakeInput("Host", t, "Host (e.g. 127.0.0.1)", false, 64);
            _portField = UiKit.MakeInput("Port", t, "Port", false, 6);
            _portField.contentType = InputField.ContentType.IntegerNumber;
            _hostField.text = ServerConfig.Host;
            _portField.text = ServerConfig.Port.ToString();
            UiKit.MakeButton("SaveServer", t, "Save & reconnect", () =>
            {
                string host = string.IsNullOrWhiteSpace(_hostField.text) ? "127.0.0.1" : _hostField.text.Trim();
                int port = int.TryParse(_portField.text, out var p) && p > 0 && p < 65536 ? p : NetProtocol.DefaultPort;
                ServerConfig.Host = host; ServerConfig.Port = port;
                Net.Reconnect(host, port);
                _showSettings = false;
                Msg($"Connecting to {host}:{port}...", MsgKind.Info);
                Refresh();
            }, UiKit.ButtonRed);
            UiKit.MakeButton("DefaultServer", t, "Use the default server", () =>
            {
                ServerConfig.ClearSavedOverride();
                _hostField.text = ServerConfig.Host;
                _portField.text = ServerConfig.Port.ToString();
                Net.Reconnect(ServerConfig.Host, ServerConfig.Port);
                _showSettings = false;
                Msg($"Connecting to {ServerConfig.Host}:{ServerConfig.Port}...", MsgKind.Info);
                Refresh();
            }, UiKit.ButtonGrey);
            UiKit.MakeButton("BackBtn", t, "Back", () => { _showSettings = false; Refresh(); }, UiKit.ButtonGrey);
        }

        // ── Net events ──────────────────────────────────────────────────────
        private void WireNet()
        {
            Net.OnStateChanged += _ => Refresh();
            Net.OnAuth += r =>
            {
                if (r.Ok) { Msg(r.Message == "OK" ? "Welcome, " + r.Username + "!" : r.Message, MsgKind.Ok); _passField.text = ""; Net.RequestRoomList(); }
                else Msg(r.ViaToken ? "Please log in again." : r.Message, MsgKind.Error);
                Refresh();
            };
            Net.OnRoomJoined += _ => { Msg(Net.IsKing ? "Room ready - you are the Owner." : "Joined " + Net.RoomName + ".", MsgKind.Ok); Refresh(); };
            Net.OnRoomLeft += reason =>
            {
                _lobbyView = LobbyView.Menu;
                if (!string.IsNullOrEmpty(reason)) Msg(reason, MsgKind.Error);
                Refresh();
                if (Net.LoggedIn) Net.RequestRoomList();
            };
            Net.OnRoomError += m => { Msg(m, MsgKind.Error); Refresh(); };
            Net.OnServerNotice += text =>
            {
                Msg("Server: " + text, MsgKind.Info);
                // Shown even when this window is closed.
                try { WorldTip.showNow("Server: " + text, false, "top", 8f); } catch { }
            };
            Net.OnRosterChanged += Refresh;
            Net.OnRoomListChanged += RefreshRoomList;
        }

        private void Msg(string m, MsgKind kind = MsgKind.Info)
        {
            if (_msgText == null) return;
            _msgText.text = m ?? "";
            _msgText.color = kind == MsgKind.Error ? new Color(1f, 0.55f, 0.5f)
                           : kind == MsgKind.Ok ? new Color(0.6f, 1f, 0.6f)
                           : Color.white;
            _msgClearAt = string.IsNullOrEmpty(m) ? 0f : Time.unscaledTime + (kind == MsgKind.Error ? 10f : 6f);
        }

        private static void SetLabel(Button b, string label)
        {
            var txt = b != null ? b.GetComponentInChildren<Text>(true) : null;
            if (txt != null) txt.text = label;
        }

        private static void SetEnabled(Button b, bool on)
        {
            if (b == null) return;
            b.interactable = on;
            var cg = b.GetComponent<CanvasGroup>() ?? b.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = on ? 1f : 0.5f;
        }

        // ── Refresh ─────────────────────────────────────────────────────────
        private void Refresh()
        {
            if (_statusText == null) return;
            bool connected = Net.State == ConnState.Connected;
            if (connected)
            {
                _statusText.text = Net.LoggedIn ? $"Online - {Net.Host}" : $"Connected to {Net.Host}";
                _statusText.color = new Color(0.6f, 1f, 0.6f);
            }
            else
            {
                string why = string.IsNullOrEmpty(Net.LastDisconnectReason) ? "" : Net.LastDisconnectReason + " ";
                _statusText.text = Net.State == ConnState.Connecting && why.Length == 0
                    ? $"Connecting to {Net.Host}..."
                    : $"{why}Retrying...";
                _statusText.color = new Color(1f, 0.75f, 0.45f);
            }

            if (_showSettings)
            {
                _loginPanel.SetActive(false); _lobbyPanel.SetActive(false);
                _createPanel.SetActive(false); _joinPanel.SetActive(false); _roomPanel.SetActive(false);
                _settingsPanel.SetActive(true);
                return;
            }
            _settingsPanel.SetActive(false);

            bool loggedIn = Net.LoggedIn;
            bool inRoom = Net.InRoom;
            bool lobby = loggedIn && !inRoom;
            if (!lobby) _lobbyView = LobbyView.Menu;   // leaving/joining resets the nesting
            _loginPanel.SetActive(!loggedIn);
            _lobbyPanel.SetActive(lobby && _lobbyView == LobbyView.Menu);
            _createPanel.SetActive(lobby && _lobbyView == LobbyView.Create);
            _joinPanel.SetActive(lobby && _lobbyView == LobbyView.Join);
            _roomPanel.SetActive(loggedIn && inRoom);

            // Login: a remembered session signs in by itself - show that instead of the form.
            bool resuming = Net.ResumingSession;
            _resumeText.gameObject.SetActive(resuming);
            foreach (var go in _loginForm) go.SetActive(!resuming);
            bool canAuth = connected && !Net.AuthPending;
            SetEnabled(_loginBtn, canAuth);
            SetEnabled(_registerBtn, canAuth);

            bool canRoom = connected && !Net.RoomPending;
            SetEnabled(_createBtn, canRoom);
            SetEnabled(_joinBtn, canRoom);

            if (lobby) _whoText.text = "Signed in as " + Net.Username;
            if (inRoom) RefreshRoom();
        }

        private void RefreshRoom()
        {
            _roomTitle.text = string.IsNullOrEmpty(Net.RoomName) ? "Room" : Net.RoomName;
            _roomCode.text = "Code: " + Net.RoomId;
            string king = "";
            foreach (var p in Net.Roster) if ((RoomRole)p.Role == RoomRole.King) king = p.Username;
            _roomRoleText.text = Net.IsKing
                ? "You are the Owner - your world is the shared one."
                : string.IsNullOrEmpty(king) ? "Waiting for the Owner..." : $"You are playing in {king}'s world.";
            SetLabel(_resyncBtn, Net.IsKing ? "Resend world" : "Resync");
            int max = 0;
            foreach (var r in Net.RoomList) if (r.ShareableId == Net.RoomId) max = r.MaxPlayers;
            _playersHeader.text = max > 0 ? $"Players ({Net.Roster.Count}/{max})" : $"Players ({Net.Roster.Count})";
            RefreshRoster();
        }

        private void RefreshRoomList()
        {
            if (_roomListContainer == null) return;
            ClearChildren(_roomListContainer);
            if (Net.RoomList.Count == 0)
                Hint(_roomListContainer.transform, Net.State == ConnState.Connected ? "No rooms yet - create one!" : "Offline.");
            foreach (var r in Net.RoomList)
            {
                string code = r.ShareableId;
                string name = string.IsNullOrWhiteSpace(r.Name) ? "Room" : r.Name.Trim();
                if (name.Length > 22) name = name.Substring(0, 21) + "…";
                bool full = r.MaxPlayers > 0 && r.PlayerCount >= r.MaxPlayers;

                var row = UiKit.MakeRow("room_" + code, _roomListContainer.transform, 4f);
                var hl = row.GetComponent<HorizontalLayoutGroup>();
                hl.childAlignment = TextAnchor.MiddleLeft; hl.childForceExpandWidth = false;
                UiKit.SetHeight(row, 36f);

                var col = UiKit.MakeColumn("info", row.transform, null, 0f, new RectOffset(2, 0, 0, 0));
                col.AddComponent<LayoutElement>().flexibleWidth = 1f;
                var title = UiKit.MakeText("name", col.transform, name, 12, TextAnchor.MiddleLeft, FontStyle.Bold,
                    r.IsLobby ? UiKit.Orange : Color.white);
                title.supportRichText = false;
                title.horizontalOverflow = HorizontalWrapMode.Overflow;
                UiKit.SetHeight(title.gameObject, 17f);
                // One short line; Unity Text can't ellipsize, so keep it brief instead of wrapping.
                string king = r.KingName ?? "";
                if (king.Length > 12) king = king.Substring(0, 11) + "…";
                string sub = king.Length > 0 ? "Owner: " + king : r.IsLobby ? "Empty lobby" : "Empty";
                var subText = Hint(col.transform, sub);
                subText.fontSize = 10;
                subText.horizontalOverflow = HorizontalWrapMode.Overflow;
                UiKit.SetHeight(subText.gameObject, 14f);

                string count = r.MaxPlayers > 0 ? $"{r.PlayerCount}/{r.MaxPlayers}" : r.PlayerCount.ToString();
                var ct = UiKit.MakeText("count", row.transform, count, 11, TextAnchor.MiddleRight);
                var cle = ct.gameObject.AddComponent<LayoutElement>(); cle.minWidth = 36f; cle.preferredWidth = 36f;

                string label = full ? "Full" : r.HasPassword ? "Locked" : "Join";
                string roomName = name;
                var btn = UiKit.MakeButton("join", row.transform, label, () =>
                {
                    if (r.HasPassword) OpenJoin(code, roomName);
                    else { Net.JoinRoom(code, ""); Msg("Joining " + roomName + "...", MsgKind.Info); Refresh(); }
                }, r.HasPassword ? UiKit.ButtonGrey : UiKit.ButtonRed, 11, 24f, 60f);
                SetEnabled(btn, !full && Net.State == ConnState.Connected && !Net.RoomPending);
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
                var role = (RoomRole)p.Role;
                string tag = role == RoomRole.King ? "  Owner" : role == RoomRole.Admin ? "  Admin" : "";
                bool isMe = p.Id4 == Net.Id4;
                var nameText = UiKit.MakeText("n", row.transform, p.Username + (isMe ? " (you)" : "") + tag, 12,
                    TextAnchor.MiddleLeft, role == RoomRole.Player ? FontStyle.Normal : FontStyle.Bold,
                    role == RoomRole.King ? UiKit.Orange : role == RoomRole.Admin ? new Color(0.6f, 0.85f, 1f) : Color.white);
                nameText.supportRichText = false;
                var nle = nameText.gameObject.AddComponent<LayoutElement>();
                nle.flexibleWidth = 1f;   // name takes the slack; buttons keep their size
                bool canModerate = !isMe && (Net.IsKing || (Net.IsAdmin && role == RoomRole.Player));
                if (canModerate)
                {
                    if (Net.IsKing && role == RoomRole.Player)
                        UiKit.MakeButton("prom", row.transform, "Admin+", () => Net.Promote(p.Id4), UiKit.ButtonGrey, 10, 22f, 54f);
                    if (Net.IsKing && role == RoomRole.Admin)
                        UiKit.MakeButton("dem", row.transform, "Admin-", () => Net.Demote(p.Id4), UiKit.ButtonGrey, 10, 22f, 54f);
                    UiKit.MakeButton("kick", row.transform, "Kick", () => Net.Kick(p.Id4), UiKit.ButtonRed, 11, 22f, 48f);
                }
            }
        }

        private static void ClearChildren(GameObject go)
        {
            if (go == null) return;
            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                var child = go.transform.GetChild(i).gameObject;
                child.SetActive(false);   // hide now: Destroy is deferred to end of frame (no one-frame duplicates)
                Destroy(child);
            }
        }
    }
}

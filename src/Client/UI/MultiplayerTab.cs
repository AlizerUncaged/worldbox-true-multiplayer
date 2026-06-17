using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;                    // AccessTools
using NeoModLoader.General;          // PowerButtonCreator
using NeoModLoader.General.UI.Tab;   // TabManager
using UnityEngine;
using UnityEngine.UI;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// The 7th toolbar tab. Buttons are state-gated: when you're not logged in / not
    /// in a room, the actions that need it are darkened and, if clicked, show a hint
    /// ("Log in first!" / "Join a room first!") instead of silently opening login.
    /// </summary>
    public static class MultiplayerTab
    {
        public const string TabId = "wbmp_tab";
        private static bool _created;
        private static bool _subscribed;

        private static readonly FieldInfo _ptcInstance =
            AccessTools.Field(typeof(PowerTabController), "instance");

        private class Btn { public Image Icon; public Func<bool> Enabled; }
        private static readonly List<Btn> _btns = new List<Btn>();

        private static Net.NetClient Net => WorldboxMP.Client.Net.NetClient.Instance;

        public static bool TryCreate()
        {
            if (_created) return true;
            if (_ptcInstance == null || _ptcInstance.GetValue(null) == null) return false;

            try
            {
                var tabIcon = IconFactory.GetEmbedded("multiplayer-header-icon.png")
                              ?? IconFactory.Get("mp_tab", new Color(0.30f, 0.55f, 0.95f));
                var tab = TabManager.CreateTab(TabId, "Multiplayer", "WorldBox Multiplayer", tabIcon);
                EnlargeTabIcon(tabIcon, 2f);

                // Account / login — always available.
                AddButton("wbmp_login", "Account / login",
                    IconFactory.GetEmbedded("login-icon.png") ?? IconFactory.Get("wbmp_login", new Color(0.30f, 0.55f, 0.95f)),
                    tab, AlwaysOn, MultiplayerWindow.Open);

                // Players — needs to be in a room.
                AddButton("wbmp_players", "Players in the current room",
                    IconFactory.GetEmbedded("player-list-icon.png") ?? IconFactory.Get("wbmp_players", new Color(0.35f, 0.75f, 0.45f)),
                    tab, InRoom, MultiplayerWindow.Open);

                // Logs — needs to be in a room.
                AddButton("wbmp_logs", "Server logs",
                    IconFactory.GetEmbedded("logs-icon.png") ?? IconFactory.Get("wbmp_logs", new Color(0.85f, 0.70f, 0.30f)),
                    tab, InRoom, () => { Net?.RequestLogs(); MultiplayerWindow.Open(); });

                // Settings — always available.
                AddButton("wbmp_settings", "Server settings",
                    IconFactory.GetEmbedded("settings-icon.png") ?? IconFactory.Get("wbmp_settings", new Color(0.55f, 0.55f, 0.60f)),
                    tab, AlwaysOn, MultiplayerWindow.OpenSettings);

                Subscribe();
                RefreshButtons();
                _created = true;
                ModLog.Info("Multiplayer tab created.");
                return true;
            }
            catch (Exception e) { ModLog.Warn("Tab creation failed (will retry): " + e.Message); return false; }
        }

        private static bool AlwaysOn() => true;
        private static bool InRoom() => Net != null && Net.InRoom;

        private static string BlockedReason()
        {
            if (Net == null || !Net.LoggedIn) return "Log in first!";
            if (!Net.InRoom) return "Join a room first!";
            return "Not available right now.";
        }

        private static void AddButton(string id, string tip, Sprite icon, PowersTab tab, Func<bool> enabled, Action action)
        {
            var btn = PowerButtonCreator.CreateSimpleButton(id, () =>
            {
                if (enabled()) action();
                else { try { WorldTip.showNow(BlockedReason(), false, "top", 3f); } catch { } }
            }, icon);
            PowerButtonCreator.AddButtonToTab(btn, tab);

            Image iconImg = null;
            foreach (var img in btn.GetComponentsInChildren<Image>(true))
                if (img.sprite == icon) { iconImg = img; break; }
            _btns.Add(new Btn { Icon = iconImg, Enabled = enabled });
        }

        private static void Subscribe()
        {
            if (_subscribed || Net == null) return;
            _subscribed = true;
            Net.OnStateChanged += _ => RefreshButtons();
            Net.OnAuth += _ => RefreshButtons();
            Net.OnRoomJoined += _ => RefreshButtons();
            Net.OnRosterChanged += RefreshButtons;   // also fires on leave
        }

        private static void RefreshButtons()
        {
            foreach (var b in _btns)
            {
                if (b.Icon == null) continue;
                b.Icon.color = b.Enabled() ? Color.white : new Color(0.40f, 0.40f, 0.40f, 0.55f);
            }
        }

        /// <summary>Scale up the tab's header icon (catgirl) and let it overflow the slot.</summary>
        private static void EnlargeTabIcon(Sprite tabIcon, float scale)
        {
            try
            {
                foreach (var img in Resources.FindObjectsOfTypeAll<Image>())
                {
                    if (img == null || img.sprite != tabIcon || img.name != "Icon") continue;
                    img.transform.localScale = Vector3.one * scale;
                    img.transform.SetAsLastSibling();
                    var le = img.GetComponent<LayoutElement>() ?? img.gameObject.AddComponent<LayoutElement>();
                    le.ignoreLayout = true;
                }
            }
            catch (Exception e) { ModLog.Warn("EnlargeTabIcon failed: " + e.Message); }
        }
    }
}

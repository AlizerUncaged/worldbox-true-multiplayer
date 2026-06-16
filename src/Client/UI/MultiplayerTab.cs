using NeoModLoader.General;          // PowerButtonCreator
using NeoModLoader.General.UI.Tab;   // TabManager
using UnityEngine;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Creates the 7th toolbar tab and fills it with buttons that drive the
    /// multiplayer manager window. Created lazily once the powers UI exists.
    /// </summary>
    public static class MultiplayerTab
    {
        public const string TabId = "wbmp_tab";
        private static bool _created;

        public static bool TryCreate()
        {
            if (_created) return true;
            // The tab system needs the base tabs to exist (only true in-game).
            if (PowerTabController.instance == null) return false;

            try
            {
                var tabIcon = IconFactory.Get("mp_tab", new Color(0.30f, 0.55f, 0.95f));
                var tab = TabManager.CreateTab(TabId, "Multiplayer", "WorldBox Multiplayer", tabIcon);

                AddButton("wbmp_open", "Open the multiplayer manager",
                    new Color(0.30f, 0.55f, 0.95f), tab, () => MultiplayerWindow.Instance?.Show());

                AddButton("wbmp_players", "Players in the current room",
                    new Color(0.35f, 0.75f, 0.45f), tab, () => { MultiplayerWindow.Instance?.Show(); });

                AddButton("wbmp_logs", "Server logs",
                    new Color(0.85f, 0.70f, 0.30f), tab, () =>
                    {
                        Net.NetClient.Instance?.RequestLogs();
                        MultiplayerWindow.Instance?.Show();
                    });

                _created = true;
                ModLog.Info("Multiplayer tab created.");
                return true;
            }
            catch (System.Exception e)
            {
                ModLog.Warn("Tab creation failed (will retry): " + e.Message);
                return false;
            }
        }

        private static void AddButton(string id, string tip, Color color, PowersTab tab, System.Action onClick)
        {
            var icon = IconFactory.Get(id, color);
            var btn = PowerButtonCreator.CreateSimpleButton(id, () => onClick(), icon);
            PowerButtonCreator.AddButtonToTab(btn, tab);
        }
    }
}

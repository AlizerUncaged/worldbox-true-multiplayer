using System.Reflection;
using HarmonyLib;                    // AccessTools
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

        // PowerTabController.instance is internal at runtime (the publicized DLL
        // lies), so read it via reflection rather than touching it directly.
        private static readonly FieldInfo _ptcInstance =
            AccessTools.Field(typeof(PowerTabController), "instance");

        public static bool TryCreate()
        {
            if (_created) return true;
            // The tab system needs the base tabs to exist (only true in-game).
            if (_ptcInstance == null || _ptcInstance.GetValue(null) == null) return false;

            try
            {
                // Embedded 16:9 logo, letterboxed to a square so it fits the tab slot.
                var tabIcon = IconFactory.GetEmbedded("multiplayer-header-icon.png")
                              ?? IconFactory.Get("mp_tab", new Color(0.30f, 0.55f, 0.95f));
                var tab = TabManager.CreateTab(TabId, "Multiplayer", "WorldBox Multiplayer", tabIcon);
                EnlargeTabIcon(tabIcon, 2f);

                AddButton("wbmp_open", "Open the multiplayer manager",
                    new Color(0.30f, 0.55f, 0.95f), tab, MultiplayerWindow.Open);

                AddButton("wbmp_players", "Players in the current room",
                    new Color(0.35f, 0.75f, 0.45f), tab, MultiplayerWindow.Open);

                AddButton("wbmp_logs", "Server logs",
                    new Color(0.85f, 0.70f, 0.30f), tab, () =>
                    {
                        Net.NetClient.Instance?.RequestLogs();
                        MultiplayerWindow.Open();
                    });

                AddButton("wbmp_settings", "Server settings",
                    new Color(0.55f, 0.55f, 0.60f), tab, MultiplayerWindow.OpenSettings);

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

        /// <summary>
        /// Scale up the tab's header icon (the catgirl). The tab entry is
        /// "Button_&lt;TabId&gt;/Icon"; we enlarge it and let it overflow the slot.
        /// </summary>
        private static void EnlargeTabIcon(Sprite tabIcon, float scale)
        {
            try
            {
                foreach (var img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
                {
                    if (img == null || img.sprite != tabIcon || img.name != "Icon") continue;
                    img.transform.localScale = Vector3.one * scale;
                    img.transform.SetAsLastSibling();
                    var le = img.GetComponent<UnityEngine.UI.LayoutElement>();
                    if (le == null) le = img.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                    le.ignoreLayout = true;   // allow it to draw out of bounds
                }
            }
            catch (System.Exception e) { ModLog.Warn("EnlargeTabIcon failed: " + e.Message); }
        }

        private static void AddButton(string id, string tip, Color color, PowersTab tab, System.Action onClick)
        {
            var icon = IconFactory.Get(id, color);
            var btn = PowerButtonCreator.CreateSimpleButton(id, () => onClick(), icon);
            PowerButtonCreator.AddButtonToTab(btn, tab);
        }
    }
}

using NeoModLoader.General;   // OT.InitializeCommonText
using UnityEngine;
using UnityEngine.UI;
using WorldboxMP.Client.Net;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Overlay in the top-right corner: total bytes received/sent (left) and the ping
    /// (right), e.g. "D 1.2 MB  U 340.5 KB  |  ping 3 ms". The ping is LiteNetLib's
    /// transport RTT, measured on the lib's own logic thread, so a busy Unity main
    /// thread cannot inflate it. F3 hides/shows the HUD (visible by default).
    /// </summary>
    public class PingHud : MonoBehaviour
    {
        private Text _label;
        private Text _pauseLabel;
        private bool _visible = true;

        // Compact world-transfer progress bar (top-center). Shown only for LARGE
        // transfers (player join / king world change with a big map) - small resyncs
        // stay invisible. Fill uses localScale.x, the same technique the game's own
        // bars use (there is no reusable native bar prefab).
        private GameObject _barRoot;
        private RectTransform _barFill;
        private Text _barText;
        private static float _transferPct = -1f;   // <0 = hidden
        private static string _transferLabel = "";

        public static void Spawn()
        {
            var go = new GameObject("WBMP_PingHud");
            DontDestroyOnLoad(go);
            go.AddComponent<PingHud>();
        }

        private void Awake()
        {
            var canvasGo = new GameObject("WBMP_PingCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31050;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var textGo = new GameObject("PingText", typeof(Text));
            textGo.transform.SetParent(canvasGo.transform, false);
            _label = textGo.GetComponent<Text>();
            OT.InitializeCommonText(_label);              // game font/style
            UiKit.ApplyGameFont(_label);
            _label.alignment = TextAnchor.UpperLeft;      // left-aligned, as requested
            _label.horizontalOverflow = HorizontalWrapMode.Overflow;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.fontSize = 12;
            _label.raycastTarget = false;

            // Anchor to the top-right corner with a small margin; pivot top-right so the
            // box hugs the corner while the text inside flows left-aligned. Wide enough
            // for "beta-0.1.0  |  D 1,023.9 MB  U 1,023.9 MB  |  ping 999 ms".
            var rt = _label.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(440f, 20f);
            rt.anchoredPosition = new Vector2(-10f, -8f);

            // Centered "God King paused" banner (top-center), shown only on followers while the
            // God King has the game genuinely paused (not while he's just in a menu/window).
            var pauseGo = new GameObject("GodKingPauseText", typeof(Text));
            pauseGo.transform.SetParent(canvasGo.transform, false);
            _pauseLabel = pauseGo.GetComponent<Text>();
            OT.InitializeCommonText(_pauseLabel);
            UiKit.ApplyGameFont(_pauseLabel);
            _pauseLabel.alignment = TextAnchor.UpperCenter;
            _pauseLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            _pauseLabel.verticalOverflow = VerticalWrapMode.Overflow;
            _pauseLabel.fontSize = 18;
            _pauseLabel.raycastTarget = false;
            _pauseLabel.color = new Color(1f, 0.85f, 0.3f);
            var prt = _pauseLabel.rectTransform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 1f);
            prt.pivot = new Vector2(0.5f, 1f);
            prt.sizeDelta = new Vector2(420f, 26f);
            prt.anchoredPosition = new Vector2(0f, -36f);
            _pauseLabel.text = "";

            // World-transfer progress bar: dark strip + warm fill + label, top-center.
            _barRoot = new GameObject("WBMP_TransferBar", typeof(Image));
            _barRoot.transform.SetParent(canvasGo.transform, false);
            var bbg = _barRoot.GetComponent<Image>();
            bbg.color = new Color(0.06f, 0.06f, 0.09f, 0.85f);
            bbg.raycastTarget = false;
            var brt = (RectTransform)_barRoot.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 1f);
            brt.pivot = new Vector2(0.5f, 1f);
            brt.sizeDelta = new Vector2(340f, 18f);
            brt.anchoredPosition = new Vector2(0f, -62f);

            var fillGo = new GameObject("Fill", typeof(Image));
            fillGo.transform.SetParent(_barRoot.transform, false);
            var fimg = fillGo.GetComponent<Image>();
            fimg.color = new Color(1f, 0.72f, 0.25f, 0.95f);
            fimg.raycastTarget = false;
            _barFill = (RectTransform)fillGo.transform;
            _barFill.anchorMin = new Vector2(0f, 0f);
            _barFill.anchorMax = new Vector2(1f, 1f);
            _barFill.offsetMin = new Vector2(2f, 2f);
            _barFill.offsetMax = new Vector2(-2f, -2f);
            _barFill.pivot = new Vector2(0f, 0.5f);
            _barFill.localScale = new Vector3(0f, 1f, 1f);

            var btGo = new GameObject("Label", typeof(Text));
            btGo.transform.SetParent(_barRoot.transform, false);
            _barText = btGo.GetComponent<Text>();
            OT.InitializeCommonText(_barText);
            UiKit.ApplyGameFont(_barText);
            _barText.alignment = TextAnchor.MiddleCenter;
            _barText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _barText.fontSize = 11;
            _barText.raycastTarget = false;
            _barText.color = Color.white;
            var btrt = _barText.rectTransform;
            btrt.anchorMin = Vector2.zero; btrt.anchorMax = Vector2.one;
            btrt.offsetMin = btrt.offsetMax = Vector2.zero;

            _barRoot.SetActive(false);
        }

        /// <summary>Show/update the transfer bar (pct01 in 0..1). Called by MirrorManager
        /// only for large transfers; small resyncs never surface UI.</summary>
        public static void SetTransfer(float pct01, string label)
        {
            _transferPct = Mathf.Clamp01(pct01);
            _transferLabel = label ?? "";
            TransferStatus = _transferLabel;   // REST /state mirrors the label
        }

        /// <summary>Hide the transfer bar.</summary>
        public static void ClearTransfer()
        {
            _transferPct = -1f;
            _transferLabel = "";
            TransferStatus = "";
        }

        /// <summary>Center-top banner while a world snapshot transfer is in flight
        /// ("Receiving world from the God King... 42%"). Set by MirrorManager.</summary>
        public static string TransferStatus = "";

        private void Update()
        {
            var net = NetClient.Instance;
            if (_label == null) return;

            if (Input.GetKeyDown(KeyCode.F3)) _visible = !_visible;   // toggle the net HUD

            if (_pauseLabel != null)
                _pauseLabel.text = Mirror.MirrorManager.KingPaused ? "⏸  The God King has paused the game" : "";

            if (_barRoot != null)
            {
                bool show = _transferPct >= 0f;
                if (_barRoot.activeSelf != show) _barRoot.SetActive(show);
                if (show)
                {
                    _barFill.localScale = new Vector3(_transferPct, 1f, 1f);
                    _barText.text = _transferLabel;
                }
            }

            if (!_visible) { _label.text = ""; return; }

            string ver = ModInfo.Display;   // e.g. "beta-0.1.0"

            if (net == null || net.State != ConnState.Connected)
            {
                _label.text = ver + "  |  ping --";
                _label.color = new Color(1f, 1f, 1f, 0.5f);
                return;
            }

            var fmt = Util.ByteSize.Instance;
            string traffic = $"D {fmt.SizeSuffix(net.BytesReceived)}  U {fmt.SizeSuffix(net.BytesSent)}";

            int ms = net.TransportRttMs;
            if (ms < 0) { _label.text = ver + "  |  " + traffic + "  |  ping --"; _label.color = new Color(1f, 1f, 1f, 0.6f); return; }

            // green < 80ms, yellow < 160ms, red otherwise.
            _label.color = ms < 80 ? new Color(0.5f, 1f, 0.5f) : (ms < 160 ? new Color(1f, 0.9f, 0.4f) : new Color(1f, 0.5f, 0.5f));
            string upd = Net.UpdateChecker.UpdateAvailable;
            _label.text = ver + "  |  " + traffic + "  |  ping " + ms + " ms"
                          + (upd != null ? "  |  UPDATE " + upd + " available!" : "");
        }
    }
}

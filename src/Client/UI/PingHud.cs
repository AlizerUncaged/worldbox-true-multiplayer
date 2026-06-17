using NeoModLoader.General;   // OT.InitializeCommonText
using UnityEngine;
using UnityEngine.UI;
using WorldboxMP.Client.Net;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Small always-on overlay in the top-right corner showing the round-trip ping
    /// (originator → server → king → server → originator) in milliseconds. The value
    /// is measured by <see cref="NetClient.SendPing"/> / pong handling (~1 Hz).
    /// </summary>
    public class PingHud : MonoBehaviour
    {
        private Text _label;

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
            _label.alignment = TextAnchor.UpperLeft;      // left-aligned, as requested
            _label.horizontalOverflow = HorizontalWrapMode.Overflow;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.fontSize = 12;
            _label.raycastTarget = false;

            // Anchor to the top-right corner with a small margin; pivot top-right so the
            // box hugs the corner while the text inside flows left-aligned.
            var rt = _label.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(160f, 20f);
            rt.anchoredPosition = new Vector2(-10f, -8f);
        }

        private void Update()
        {
            var net = NetClient.Instance;
            if (_label == null) return;

            if (net == null || net.State != ConnState.Connected)
            {
                _label.text = "ping --";
                _label.color = new Color(1f, 1f, 1f, 0.5f);
                return;
            }

            int ms = net.LastPingMs;
            if (ms < 0) { _label.text = "ping --";  _label.color = new Color(1f, 1f, 1f, 0.6f); return; }

            // green < 80ms, yellow < 160ms, red otherwise.
            _label.color = ms < 80 ? new Color(0.5f, 1f, 0.5f) : (ms < 160 ? new Color(1f, 0.9f, 0.4f) : new Color(1f, 0.5f, 0.5f));
            _label.text = "ping " + ms + " ms";
        }
    }
}

using System.Collections.Generic;
using NeoModLoader.General;   // OT.InitializeCommonText
using UnityEngine;
using UnityEngine.UI;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Renders each remote player's floating cursor (a colored pointer + their name)
    /// in world space, and streams the local player's cursor to the room. Updates in
    /// real time via the server's sequenced cursor channel.
    /// </summary>
    public class PlayerCursors : MonoBehaviour
    {
        private class Visual { public GameObject Root; public SpriteRenderer Dot; public Text Label; }

        private readonly Dictionary<string, Visual> _cursors = new Dictionary<string, Visual>();
        private static Sprite _dotSprite;
        private float _sendTimer;
        private const float SendInterval = 0.05f;   // 20 Hz

        public static void Spawn()
        {
            var go = new GameObject("WBMP_Cursors");
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerCursors>();
        }

        private void Awake()
        {
            if (NetClient.Instance != null)
            {
                NetClient.Instance.OnRemoteCursor += OnRemoteCursor;
                NetClient.Instance.OnRosterChanged += PruneToRoster;
            }
        }

        private void Update()
        {
            var net = NetClient.Instance;
            if (net == null || !net.InRoom || net.State != ConnState.Connected) return;
            var cam = Camera.main;
            if (cam == null) return;

            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer < SendInterval) return;
            _sendTimer = 0f;

            Vector3 world = cam.ScreenToWorldPoint(Input.mousePosition);
            net.SendCursor(world.x, world.y, Input.GetMouseButton(0));
        }

        private void OnRemoteCursor(CursorUpdate c)
        {
            if (c.Id4 == NetClient.Instance.Id4) return;   // never draw our own
            if (!_cursors.TryGetValue(c.Id4, out var v))
            {
                v = CreateVisual(c.Id4);
                _cursors[c.Id4] = v;
            }
            v.Root.transform.position = new Vector3(c.X, c.Y, -10f);
            if (v.Dot != null) v.Dot.color = c.Pressed ? Color.white : ColorFor(c.Id4);
        }

        private Visual CreateVisual(string id4)
        {
            var root = new GameObject("cursor_" + id4);
            root.transform.SetParent(transform, false);

            var dotGo = new GameObject("dot");
            dotGo.transform.SetParent(root.transform, false);
            var sr = dotGo.AddComponent<SpriteRenderer>();
            sr.sprite = DotSprite();
            sr.color = ColorFor(id4);
            sr.sortingOrder = 32000;
            dotGo.transform.localScale = Vector3.one * 0.4f;

            // Name label on a tiny world-space canvas.
            var canvasGo = new GameObject("label");
            canvasGo.transform.SetParent(root.transform, false);
            canvasGo.transform.localPosition = new Vector3(0.6f, 0.6f, 0f);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 32001;
            var crt = (RectTransform)canvasGo.transform;
            crt.sizeDelta = new Vector2(120, 24);
            canvasGo.transform.localScale = Vector3.one * 0.03f;

            var txtGo = new GameObject("text");
            txtGo.transform.SetParent(canvasGo.transform, false);
            var txt = txtGo.AddComponent<Text>();
            OT.InitializeCommonText(txt);
            txt.fontSize = 18;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.color = ColorFor(id4);
            txt.text = NameOf(id4);
            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

            return new Visual { Root = root, Dot = sr, Label = txt };
        }

        private void PruneToRoster()
        {
            var net = NetClient.Instance;
            var present = new HashSet<string>();
            foreach (var p in net.Roster) present.Add(p.Id4);
            var stale = new List<string>();
            foreach (var kv in _cursors)
            {
                if (!present.Contains(kv.Key)) { Destroy(kv.Value.Root); stale.Add(kv.Key); }
                else if (kv.Value.Label != null) kv.Value.Label.text = NameOf(kv.Key);
            }
            foreach (var id in stale) _cursors.Remove(id);
        }

        private static string NameOf(string id4)
        {
            var roster = NetClient.Instance?.Roster;
            if (roster != null)
                foreach (var p in roster)
                    if (p.Id4 == id4) return p.Username;
            return "player";
        }

        private static Color ColorFor(string id4)
        {
            int h = id4 != null ? id4.GetHashCode() : 0;
            float hue = ((h & 0x7fffffff) % 360) / 360f;
            return Color.HSVToRGB(hue, 0.65f, 1f);
        }

        private static Sprite DotSprite()
        {
            if (_dotSprite != null) return _dotSprite;
            const int s = 16;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            float r = s / 2f, cx = r, cy = r;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    tex.SetPixel(x, y, new Color(1, 1, 1, d <= r - 1 ? 1f : 0f));
                }
            tex.Apply();
            _dotSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 16f);
            return _dotSprite;
        }
    }
}

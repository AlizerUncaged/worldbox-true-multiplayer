using System.Collections.Generic;
using NeoModLoader.General;   // OT.InitializeCommonText
using UnityEngine;
using UnityEngine.UI;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Renders each remote player's cursor as WorldBox's God Finger sprite with the
    /// owning player's name centered horizontally underneath, and streams the local
    /// player's cursor to the room in real time.
    /// </summary>
    public class PlayerCursors : MonoBehaviour
    {
        private class Visual { public GameObject Root; public SpriteRenderer Finger; public Text Label; public Vector3 Target; }

        private readonly Dictionary<string, Visual> _cursors = new Dictionary<string, Visual>();
        private static Sprite _fingerSprite;
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
            // Smoothly glide every remote cursor toward its latest target each frame.
            float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f);
            foreach (var v in _cursors.Values)
                if (v.Root != null)
                    v.Root.transform.position = Vector3.Lerp(v.Root.transform.position, v.Target, t);

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
            bool isNew = !_cursors.TryGetValue(c.Id4, out var v);
            if (isNew)
            {
                v = CreateVisual(c.Id4);
                _cursors[c.Id4] = v;
            }
            // Update the interpolation target; Update() glides the finger toward it.
            v.Target = new Vector3(c.X, c.Y, -10f);
            if (isNew) v.Root.transform.position = v.Target;   // no glide-from-origin on first sight
            if (v.Finger != null) v.Finger.transform.localScale = Vector3.one * (c.Pressed ? 0.42f : 0.5f);
        }

        private Visual CreateVisual(string id4)
        {
            var root = new GameObject("cursor_" + id4);
            root.transform.SetParent(transform, false);

            // God Finger sprite (pivot offset so the fingertip is near the cursor point).
            var fingerGo = new GameObject("finger");
            fingerGo.transform.SetParent(root.transform, false);
            fingerGo.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            var sr = fingerGo.AddComponent<SpriteRenderer>();
            sr.sprite = FingerSprite();
            sr.color = Color.white;
            sr.sortingOrder = 32000;
            fingerGo.transform.localScale = Vector3.one * 0.5f;

            // Name centered horizontally directly under the finger.
            var canvasGo = new GameObject("label");
            canvasGo.transform.SetParent(root.transform, false);
            canvasGo.transform.localPosition = new Vector3(0f, -0.35f, 0f);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 32001;
            var crt = (RectTransform)canvasGo.transform;
            crt.sizeDelta = new Vector2(160, 26);
            crt.pivot = new Vector2(0.5f, 0.5f);
            canvasGo.transform.localScale = Vector3.one * 0.03f;

            var txtGo = new GameObject("text");
            txtGo.transform.SetParent(canvasGo.transform, false);
            var txt = txtGo.AddComponent<Text>();
            OT.InitializeCommonText(txt);
            txt.fontSize = 18;
            txt.alignment = TextAnchor.MiddleCenter;   // centered horizontally
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.color = ColorFor(id4);
            txt.text = NameOf(id4);
            var outline = txtGo.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.4f, -1.4f);
            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

            return new Visual { Root = root, Finger = sr, Label = txt };
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
            return Color.HSVToRGB(hue, 0.55f, 1f);
        }

        private static Sprite FingerSprite()
        {
            if (_fingerSprite != null) return _fingerSprite;
            try { _fingerSprite = SpriteTextureLoader.getSprite("ui/Icons/iconGodFinger"); } catch { }
            if (_fingerSprite == null) _fingerSprite = GenerateDot();  // fallback
            return _fingerSprite;
        }

        private static Sprite GenerateDot()
        {
            const int s = 16;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            float r = s / 2f;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Mathf.Sqrt((x - r) * (x - r) + (y - r) * (y - r));
                    tex.SetPixel(x, y, new Color(1, 1, 1, d <= r - 1 ? 1f : 0f));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 16f);
        }
    }
}

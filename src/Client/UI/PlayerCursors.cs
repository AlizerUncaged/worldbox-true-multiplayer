using System.Collections.Generic;
using NeoModLoader.General;   // OT.InitializeCommonText
using UnityEngine;
using UnityEngine.UI;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Renders each remote player's cursor as WorldBox's God Finger with the owning
    /// player's name centered beneath it, and streams the local cursor to the room.
    ///
    /// Cursors live on a persistent screen-space overlay canvas (world→screen each
    /// frame) so they survive world reloads (join snapshots) and are never culled by
    /// the world layers.
    /// </summary>
    public class PlayerCursors : MonoBehaviour
    {
        private class Visual
        {
            public RectTransform Root;
            public Image Finger;
            public Text Label;
            public Vector2 WorldPos;     // smoothed world position
            public Vector2 WorldTarget;  // latest received world position
            public bool HasPos;
        }

        private readonly Dictionary<string, Visual> _cursors = new Dictionary<string, Visual>();
        private Canvas _canvas;
        private static Sprite _fingerSprite;
        private float _sendTimer;
        private float _dbgTimer;
        private const float SendInterval = 0.05f;   // 20 Hz

        public static void Spawn()
        {
            var go = new GameObject("WBMP_Cursors");
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerCursors>();
        }

        private void Awake()
        {
            var canvasGo = new GameObject("WBMP_CursorCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 31000;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            if (NetClient.Instance != null)
            {
                NetClient.Instance.OnRemoteCursor += OnRemoteCursor;
                NetClient.Instance.OnRosterChanged += PruneToRoster;
            }
        }

        private void Update()
        {
            var net = NetClient.Instance;
            var cam = Camera.main;

            // Reposition every remote cursor: smooth world pos → screen point.
            float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f);
            foreach (var v in _cursors.Values)
            {
                if (v.Root == null) continue;
                if (!v.HasPos || cam == null) { v.Root.gameObject.SetActive(false); continue; }
                v.WorldPos = Vector2.Lerp(v.WorldPos, v.WorldTarget, t);
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(v.WorldPos.x, v.WorldPos.y, 0f));
                bool visible = sp.z > 0f;   // in front of the camera
                v.Root.gameObject.SetActive(visible);
                if (visible) v.Root.position = new Vector3(sp.x, sp.y, 0f);
            }

            if (net == null || !net.InRoom || net.State != ConnState.Connected || cam == null) return;
            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer < SendInterval) return;
            _sendTimer = 0f;
            var mp = Input.mousePosition; mp.z = -cam.transform.position.z;  // depth to the world plane
            Vector3 world = cam.ScreenToWorldPoint(mp);
            net.SendCursor(world.x, world.y, Input.GetMouseButton(0));

            _dbgTimer += SendInterval;
            if (_dbgTimer >= 3f)
            {
                _dbgTimer = 0f;
                ModLog.Info($"[cursor] me={net.Username} send world=({world.x:F1},{world.y:F1}) remoteCursors={_cursors.Count} camOrtho={cam.orthographic}");
            }
        }

        private void OnRemoteCursor(CursorUpdate c)
        {
            if (c.Id4 == NetClient.Instance.Id4) return;
            if (!_cursors.TryGetValue(c.Id4, out var v))
            {
                v = CreateVisual(c.Id4);
                _cursors[c.Id4] = v;
                ModLog.Info($"[cursor] first from {NameOf(c.Id4)} ({c.Id4}) world=({c.X:F1},{c.Y:F1})");
            }
            v.WorldTarget = new Vector2(c.X, c.Y);
            if (!v.HasPos) { v.WorldPos = v.WorldTarget; v.HasPos = true; }
        }

        private Visual CreateVisual(string id4)
        {
            var rootGo = new GameObject("cursor_" + id4, typeof(RectTransform));
            rootGo.transform.SetParent(_canvas.transform, false);
            var root = (RectTransform)rootGo.transform;
            root.sizeDelta = new Vector2(40, 40);

            var fingerGo = new GameObject("finger", typeof(RectTransform));
            fingerGo.transform.SetParent(root, false);
            var fr = (RectTransform)fingerGo.transform;
            fr.sizeDelta = new Vector2(40, 40);
            fr.anchoredPosition = new Vector2(10, -16);   // tip near the actual point
            var finger = fingerGo.AddComponent<Image>();
            finger.sprite = FingerSprite();
            finger.preserveAspect = true;
            finger.raycastTarget = false;

            var labelGo = new GameObject("name", typeof(RectTransform));
            labelGo.transform.SetParent(root, false);
            var lr = (RectTransform)labelGo.transform;
            lr.sizeDelta = new Vector2(160, 20);
            lr.anchoredPosition = new Vector2(0, -34);    // centered under the finger
            var label = labelGo.AddComponent<Text>();
            OT.InitializeCommonText(label);
            UiKit.ApplyGameFont(label);
            label.fontSize = 13;
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.color = ColorFor(id4);
            label.raycastTarget = false;
            label.text = NameOf(id4);
            var ol = labelGo.AddComponent<Outline>();
            ol.effectColor = new Color(0f, 0f, 0f, 0.9f);
            ol.effectDistance = new Vector2(1.3f, -1.3f);

            return new Visual { Root = root, Finger = finger, Label = label };
        }

        private void PruneToRoster()
        {
            var net = NetClient.Instance;
            var present = new HashSet<string>();
            foreach (var p in net.Roster) present.Add(p.Id4);
            var stale = new List<string>();
            foreach (var kv in _cursors)
            {
                if (!present.Contains(kv.Key)) { if (kv.Value.Root != null) Destroy(kv.Value.Root.gameObject); stale.Add(kv.Key); }
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
            return _fingerSprite;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Siccity.GLTFUtility;
using UnityEngine;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Hidden easter egg: type "its all ogre now" anywhere in-game to summon real 3D glTF models
    /// rendered over WorldBox's 2D world via a dedicated overlay camera on an unused layer:
    ///   - nanally.glb : centered turntable spin (Genshin-ish light rig).
    ///   - shrek.glb   : chaotic, fast DVD-screensaver bounce around the whole screen.
    /// Click a model to dismiss it; dismissing the last one tears the whole rig down and frees the
    /// ~60MB of textures/meshes. Nothing is loaded until summoned, so idle cost is just a tiny
    /// per-frame phrase check.
    /// </summary>
    public class Model3D : MonoBehaviour
    {
        private const int ModelLayer = 31;     // an unused layer the game's cameras don't draw
        private const string Phrase = "its all ogre now";

        private enum Beh { Turntable, DvdBounce }

        private class Entry
        {
            public GameObject pivot;     // visual center; we move/rotate this
            public Beh beh;
            public float spin;           // turntable
            // DVD bounce state (viewport space)
            public Vector2 vpos, vvel;
            public float depth;
            public Vector2 margin;
            public Vector3 tumbleAxis;
            public float tumbleSpeed;
        }

        private Camera _cam;
        private GameObject _rig;
        private readonly List<Entry> _entries = new List<Entry>();
        private bool _active;
        private readonly StringBuilder _typed = new StringBuilder(32);

        public static Model3D Instance { get; private set; }

        public static void Spawn()
        {
            if (Instance != null) return;
            var go = new GameObject("WBMP_Model3D");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<Model3D>();
        }

        private void Update()
        {
            if (!_active)
            {
                ListenForPhrase();          // the ONLY work done while idle
                return;
            }

            float dt = Time.unscaledDeltaTime;
            HandleClickDismiss();
            if (!_active) return;           // a dismiss may have torn everything down

            foreach (var e in _entries)
            {
                if (e.pivot == null) continue;
                if (e.beh == Beh.Turntable)
                {
                    e.spin += dt * 37.5f;
                    e.pivot.transform.rotation = Quaternion.Euler(0f, e.spin, 0f);
                }
                else
                {
                    UpdateDvd(e, dt);
                }
            }
        }

        // ---- phrase detection -------------------------------------------------

        private void ListenForPhrase()
        {
            string s = Input.inputString;
            if (string.IsNullOrEmpty(s)) return;
            foreach (char c in s)
            {
                char l = char.ToLowerInvariant(c);
                if (l != ' ' && (l < 'a' || l > 'z')) continue;   // ignore backspace/enter/punct/digits
                _typed.Append(l);
            }
            if (_typed.Length > Phrase.Length) _typed.Remove(0, _typed.Length - Phrase.Length);
            if (_typed.Length == Phrase.Length && _typed.ToString() == Phrase)
            {
                _typed.Length = 0;
                ModLog.Info("[3D] easter egg phrase detected — summoning");
                Activate();
            }
        }

        // ---- click to dismiss -------------------------------------------------

        private void HandleClickDismiss()
        {
            if (!Input.GetMouseButtonDown(0) || _cam == null) return;
            var ray = _cam.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, 100f, 1 << ModelLayer)) return;

            // Find the entry whose pivot owns the hit collider.
            Transform t = hit.collider.transform;
            Entry found = null;
            while (t != null && found == null)
            {
                foreach (var e in _entries) if (e.pivot != null && e.pivot.transform == t) { found = e; break; }
                t = t.parent;
            }
            if (found == null) return;

            ModLog.Info("[3D] dismissed a model by click");
            if (found.pivot != null) Destroy(found.pivot);
            _entries.Remove(found);
            if (_entries.Count == 0) Teardown();
        }

        // ---- activate / teardown ---------------------------------------------

        public void Activate()
        {
            if (_active) return;
            try
            {
                SetupCameraAndLights();

                var n = LoadModel("nanally.glb", 2.0f, playAnim: true);
                if (n != null)
                {
                    n.transform.position = Vector3.zero;
                    _entries.Add(new Entry { pivot = n, beh = Beh.Turntable });
                }

                var s = LoadModel("shrek.glb", 1.1f, playAnim: false);
                if (s != null)
                {
                    _entries.Add(new Entry
                    {
                        pivot = s,
                        beh = Beh.DvdBounce,
                        depth = 6.5f,
                        margin = new Vector2(0.10f, 0.12f),
                        vpos = new Vector2(0.5f, 0.5f),
                        vvel = RandomDir() * 0.95f,
                        tumbleAxis = UnityEngine.Random.onUnitSphere,
                        tumbleSpeed = UnityEngine.Random.Range(180f, 360f)
                    });
                }

                if (_entries.Count == 0) { Teardown(); return; }
                _active = true;
                ModLog.Info($"[3D] activated with {_entries.Count} model(s)");
            }
            catch (Exception ex) { ModLog.Error("[3D] activate failed: " + ex); Teardown(); }
        }

        /// <summary>Destroy everything and free GPU/CPU memory; back to idle phrase-listening.</summary>
        public void Teardown()
        {
            foreach (var e in _entries) if (e.pivot != null) Destroy(e.pivot);
            _entries.Clear();
            if (_rig != null) Destroy(_rig);
            if (_cam != null) Destroy(_cam.gameObject);
            _rig = null; _cam = null;
            _active = false;
            _typed.Length = 0;
            // Free the meshes/textures GLTFUtility created (not asset-referenced → collectable now).
            try { Resources.UnloadUnusedAssets(); } catch { }
            ModLog.Info("[3D] torn down (resources freed)");
        }

        // ---- loading ----------------------------------------------------------

        private static string GlbPath(string file)
        {
            var candidates = new List<string>();
            try { var loc = Assembly.GetExecutingAssembly().Location; if (!string.IsNullOrEmpty(loc)) candidates.Add(Path.Combine(Path.GetDirectoryName(loc), file)); } catch { }
            try { var root = Directory.GetParent(Application.dataPath)?.FullName; if (root != null) candidates.Add(Path.Combine(root, "Mods", "WorldBoxMultiplayer", file)); } catch { }
            foreach (var c in candidates) { try { if (File.Exists(c)) return c; } catch { } }
            return candidates.Count > 0 ? candidates[0] : null;
        }

        private GameObject LoadModel(string file, float targetHeight, bool playAnim)
        {
            try
            {
                var path = GlbPath(file);
                if (path == null || !File.Exists(path)) { ModLog.Warn("[3D] not found: " + file + " (" + path + ")"); return null; }

                var settings = new ImportSettings();
                var sh = FindUsableShader();
                ApplyShaderOverrides(settings, sh);

                AnimationClip[] clips;
                var model = Importer.LoadFromFile(path, settings, out clips);
                if (model == null) { ModLog.Warn("[3D] null model: " + file); return null; }
                FixupMaterials(model, sh);

                var bounds = ComputeBounds(model);
                float scale = targetHeight / Mathf.Max(0.001f, bounds.size.y);
                var pivot = new GameObject("WBMP_3D_" + file);
                DontDestroyOnLoad(pivot);
                model.transform.SetParent(pivot.transform, false);
                model.transform.localScale = Vector3.one * scale;
                model.transform.localPosition = -bounds.center * scale;
                SetLayerRecursive(pivot, ModelLayer);

                // Click target: a box around the scaled model (centered on the pivot).
                var box = pivot.AddComponent<BoxCollider>();
                box.center = Vector3.zero;
                box.size = bounds.size * scale;

                if (playAnim) PlayAnimation(model, clips);
                ModLog.Info($"[3D] {file} ok: anims={(clips != null ? clips.Length : 0)} scale={scale:F3} bounds={bounds.size}");
                return pivot;
            }
            catch (Exception e) { ModLog.Error("[3D] load failed (" + file + "): " + e); return null; }
        }

        private void SetupCameraAndLights()
        {
            var camGo = new GameObject("WBMP_Model3DCam");
            DontDestroyOnLoad(camGo);
            _cam = camGo.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.Depth;
            _cam.cullingMask = 1 << ModelLayer;
            _cam.depth = 200;
            _cam.fieldOfView = 35f;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 60f;
            _cam.orthographic = false;
            _cam.transform.position = new Vector3(0f, 1f, -4f);
            _cam.transform.rotation = Quaternion.Euler(8f, 0f, 0f);

            _rig = new GameObject("WBMP_Model3DLightRig");
            DontDestroyOnLoad(_rig);
            AddLight(_rig, new Vector3(35f, 10f, 0f), 1.15f, new Color(1.00f, 0.98f, 0.95f));
            AddLight(_rig, new Vector3(12f, 55f, 0f), 0.70f, new Color(0.95f, 0.97f, 1.00f));
            AddLight(_rig, new Vector3(12f, -55f, 0f), 0.70f, new Color(0.95f, 0.97f, 1.00f));
            AddLight(_rig, new Vector3(-25f, 180f, 0f), 1.05f, new Color(1.00f, 0.92f, 0.96f));
            AddLight(_rig, new Vector3(80f, 0f, 0f), 0.55f, Color.white);
            AddLight(_rig, new Vector3(-45f, 20f, 0f), 0.45f, new Color(0.92f, 0.95f, 1.00f));
        }

        private void AddLight(GameObject parent, Vector3 euler, float intensity, Color color)
        {
            var go = new GameObject("L");
            go.transform.SetParent(parent.transform, false);
            go.transform.rotation = Quaternion.Euler(euler);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.color = color;
            l.shadows = LightShadows.None;
            l.cullingMask = 1 << ModelLayer;
        }

        private void PlayAnimation(GameObject model, AnimationClip[] clips)
        {
            if (clips == null || clips.Length == 0) return;
            try
            {
                var anim = model.GetComponent<Animation>() ?? model.AddComponent<Animation>();
                foreach (var clip in clips)
                {
                    if (clip == null) continue;
                    clip.legacy = true;
                    anim.AddClip(clip, clip.name);
                }
                anim.wrapMode = WrapMode.Loop;
                anim.Play(clips[0].name);
            }
            catch (Exception e) { ModLog.Warn("[3D] anim play failed: " + e.Message); }
        }

        private void UpdateDvd(Entry e, float dt)
        {
            e.vpos += e.vvel * dt;
            bool bounced = false;
            if (e.vpos.x < e.margin.x) { e.vpos.x = e.margin.x; e.vvel.x = Mathf.Abs(e.vvel.x); bounced = true; }
            if (e.vpos.x > 1f - e.margin.x) { e.vpos.x = 1f - e.margin.x; e.vvel.x = -Mathf.Abs(e.vvel.x); bounced = true; }
            if (e.vpos.y < e.margin.y) { e.vpos.y = e.margin.y; e.vvel.y = Mathf.Abs(e.vvel.y); bounced = true; }
            if (e.vpos.y > 1f - e.margin.y) { e.vpos.y = 1f - e.margin.y; e.vvel.y = -Mathf.Abs(e.vvel.y); bounced = true; }

            if (bounced)
            {
                float ang = Mathf.Atan2(e.vvel.y, e.vvel.x) + UnityEngine.Random.Range(-0.7f, 0.7f);
                float spd = Mathf.Clamp(e.vvel.magnitude * UnityEngine.Random.Range(0.85f, 1.3f), 0.7f, 1.8f);
                e.vvel = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * spd;
                e.tumbleAxis = UnityEngine.Random.onUnitSphere;
                e.tumbleSpeed = UnityEngine.Random.Range(220f, 540f);
            }

            e.pivot.transform.position = _cam.ViewportToWorldPoint(new Vector3(e.vpos.x, e.vpos.y, e.depth));
            e.pivot.transform.Rotate(e.tumbleAxis, e.tumbleSpeed * dt, Space.World);
        }

        // ---- helpers ----------------------------------------------------------

        private static Vector2 RandomDir()
        {
            float a = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }

        private static Shader FindUsableShader()
        {
            string[] names = { "Standard", "Legacy Shaders/Diffuse", "Mobile/Diffuse", "Unlit/Texture", "Sprites/Default" };
            foreach (var n in names) { var s = Shader.Find(n); if (s != null) return s; }
            ModLog.Warn("[3D] no usable shader found");
            return null;
        }

        private static void ApplyShaderOverrides(ImportSettings settings, Shader sh)
        {
            if (sh == null || settings == null) return;
            try
            {
                var ss = settings.shaderOverrides;
                foreach (var f in new[] { "metallic", "metallicBlend", "specular", "specularBlend" })
                    typeof(ShaderSettings).GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(ss, sh);
            }
            catch (Exception e) { ModLog.Warn("[3D] shader override failed: " + e.Message); }
        }

        private static void FixupMaterials(GameObject go, Shader sh)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) { if (sh != null) { mats[i] = new Material(sh); m = mats[i]; } else continue; }
                    if (m.shader == null && sh != null) m.shader = sh;
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.18f);
                    if (m.HasProperty("_GlossMapScale")) m.SetFloat("_GlossMapScale", 0.18f);
                }
                r.materials = mats;
            }
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }

        private static Bounds ComputeBounds(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends == null || rends.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }
    }
}

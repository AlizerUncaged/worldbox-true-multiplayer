using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Produces button/tab icons. Order of preference:
    ///   1. an embedded resource baked into the dll (self-contained),
    ///   2. a PNG dropped in the mod's <c>icons/</c> folder (swappable art),
    ///   3. a generated colored placeholder.
    /// 16:9 (or any non-square) art is letterboxed into a transparent square so it
    /// fits a square tab/button slot without distortion.
    /// </summary>
    public static class IconFactory
    {
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>Mod folder (set by ModMain) for optional loose icon PNGs.</summary>
        public static string ModFolder;

        /// <summary>Load an icon embedded in the dll (by its LogicalName), squared to fit.</summary>
        public static Sprite GetEmbedded(string resourceName, bool padToSquare = true)
        {
            string key = "emb:" + resourceName + (padToSquare ? ":sq" : "");
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var sprite = LoadEmbedded(resourceName, padToSquare);
            if (sprite != null) _cache[key] = sprite;
            return sprite;
        }

        public static Sprite Get(string nameNoExt, Color fallback)
        {
            if (_cache.TryGetValue(nameNoExt, out var s)) return s;
            Sprite sprite = TryLoadPng(nameNoExt) ?? GenerateSolid(fallback);
            _cache[nameNoExt] = sprite;
            return sprite;
        }

        private static Sprite LoadEmbedded(string name, bool padToSquare)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null) { ModLog.Warn("Embedded resource not found: " + name); return null; }
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                        tex.LoadImage(ms.ToArray());
                        if (padToSquare && tex.width != tex.height) tex = PadToSquare(tex);
                        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    }
                }
            }
            catch (Exception e) { ModLog.Warn("Embedded icon load failed (" + name + "): " + e.Message); return null; }
        }

        private static Texture2D PadToSquare(Texture2D src)
        {
            int size = Mathf.Max(src.width, src.height);
            var dst = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            dst.SetPixels(new Color[size * size]);                 // transparent (0,0,0,0)
            int ox = (size - src.width) / 2, oy = (size - src.height) / 2;
            dst.SetPixels(ox, oy, src.width, src.height, src.GetPixels());
            dst.Apply();
            return dst;
        }

        private static Sprite TryLoadPng(string nameNoExt)
        {
            if (string.IsNullOrEmpty(ModFolder)) return null;
            string path = Path.Combine(ModFolder, "icons", nameNoExt + ".png");
            if (!File.Exists(path)) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                tex.LoadImage(File.ReadAllBytes(path));
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch { return null; }
        }

        private static Sprite GenerateSolid(Color color)
        {
            const int size = 40;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var border = new Color(color.r * 0.5f, color.g * 0.5f, color.b * 0.5f, 1f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool edge = x < 3 || y < 3 || x >= size - 3 || y >= size - 3;
                    tex.SetPixel(x, y, edge ? border : color);
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }
    }
}

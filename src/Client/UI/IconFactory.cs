using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Produces button/tab icons. Prefers a PNG dropped in the mod's <c>icons/</c>
    /// folder (so art can be swapped without recompiling); otherwise generates a
    /// simple colored placeholder sprite at runtime.
    /// </summary>
    public static class IconFactory
    {
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>Mod folder set by ModMain so we can look for icon PNGs next to the dll.</summary>
        public static string ModFolder;

        public static Sprite Get(string nameNoExt, Color fallback)
        {
            if (_cache.TryGetValue(nameNoExt, out var s)) return s;

            Sprite sprite = TryLoadPng(nameNoExt) ?? GenerateSolid(fallback);
            _cache[nameNoExt] = sprite;
            return sprite;
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

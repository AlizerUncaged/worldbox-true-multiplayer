#if DEBUG
using System;
using System.Collections;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// DEBUG-only frame recorder for promo clips: captures this game's own rendered frames
    /// (never the desktop) at a fixed wall-clock rate and writes numbered JPGs, encoded off the
    /// main thread. Works while the window is unfocused or covered. Started from the dev bridge.
    /// </summary>
    internal sealed class DevRecorder : MonoBehaviour
    {
        private static DevRecorder _instance;
        private static int _pending;
        public static bool Recording { get; private set; }
        public static int FramesWritten;

        public static string Start(string dir, float seconds, int fps)
        {
            if (Recording) return "already recording";
            if (_instance == null)
            {
                var go = new GameObject("WBMP_DevRecorder");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<DevRecorder>();
            }
            Directory.CreateDirectory(dir);
            FramesWritten = 0;
            _instance.StartCoroutine(_instance.Run(dir, seconds, Mathf.Clamp(fps, 1, 60)));
            return "recording " + seconds + "s at " + fps + " fps -> " + dir;
        }

        public static string Status => Recording ? $"recording ({FramesWritten} frames)" : $"idle ({FramesWritten} frames, {_pending} writing)";

        private IEnumerator Run(string dir, float seconds, int fps)
        {
            Recording = true;
            float start = Time.realtimeSinceStartup, interval = 1f / fps;
            int index = 0;
            var eof = new WaitForEndOfFrame();
            while (Time.realtimeSinceStartup - start < seconds)
            {
                yield return eof;
                // One captured frame per tick of the wall clock; duplicate it if the game ran
                // slower than the target rate so the clip keeps real-time speed.
                float elapsed = Time.realtimeSinceStartup - start;
                int due = Mathf.FloorToInt(elapsed / interval) + 1;
                if (index >= due) continue;
                Texture2D tex = null;
                try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch { }
                if (tex == null) continue;
                byte[] raw = tex.GetRawTextureData<byte>().ToArray();
                int w = tex.width, h = tex.height;
                GraphicsFormat fmt = tex.graphicsFormat;
                Destroy(tex);
                int first = index, count = due - index;
                index = due;
                Interlocked.Increment(ref _pending);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        byte[] jpg = ImageConversion.EncodeArrayToJPG(raw, fmt, (uint)w, (uint)h, 0, 92);
                        for (int i = 0; i < count; i++)
                            File.WriteAllBytes(Path.Combine(dir, $"frame_{first + i:D5}.jpg"), jpg);
                        Interlocked.Add(ref FramesWritten, count);
                    }
                    catch (Exception e) { ModLog.Warn("[rec] " + e.Message); }
                    finally { Interlocked.Decrement(ref _pending); }
                });
            }
            Recording = false;
        }
    }
}
#endif

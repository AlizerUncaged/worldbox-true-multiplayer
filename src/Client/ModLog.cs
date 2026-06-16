using UnityEngine;

namespace WorldboxMP.Client
{
    /// <summary>Tiny logging helper with a consistent prefix (visible in NML console + Player.log).</summary>
    public static class ModLog
    {
        private const string Prefix = "[WBMP] ";
        public static void Info(string msg) => Debug.Log(Prefix + msg);
        public static void Warn(string msg) => Debug.LogWarning(Prefix + msg);
        public static void Error(string msg) => Debug.LogError(Prefix + msg);
    }
}

using System;

namespace WorldboxMP.Client.Mirror
{
    /// <summary>
    /// Full-world snapshot bridge. The king serializes its current world; a
    /// joining follower loads it. Uses the game's own save pipeline (zlib JSON),
    /// which is the same data the game uploads to the cloud.
    /// </summary>
    public static class WorldSync
    {
        /// <summary>Serialize the current world to a compressed byte[] (king side).</summary>
        public static byte[] Save()
        {
            try
            {
                var saved = SaveManager.currentWorldToSavedMap();
                return saved.toZip();
            }
            catch (Exception e)
            {
                ModLog.Error("World snapshot save failed: " + e);
                return Array.Empty<byte>();
            }
        }

        /// <summary>Replace the local world with a received snapshot (follower side).</summary>
        public static void Load(byte[] data)
        {
            if (data == null || data.Length == 0) { ModLog.Warn("Empty world snapshot ignored."); return; }
            try
            {
                ModLog.Info($"Loading world snapshot ({data.Length} bytes)...");
                SaveManager.loadMapFromBytes(data);
            }
            catch (Exception e)
            {
                ModLog.Error("World snapshot load failed: " + e);
            }
        }
    }
}

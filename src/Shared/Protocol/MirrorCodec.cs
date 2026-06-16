using System.Collections.Generic;
using LiteNetLib.Utils;

namespace WorldboxMP.Shared.Protocol
{
    // Payload structs carried inside MirrorMessage.Payload. Shared so the server
    // can decode them for verbose logging and the client for apply.

    public struct GodPowerMsg { public string PowerId; public int X; public int Y; }
    public struct SpawnMsg { public long KingId; public string AssetId; public int X; public int Y; public float Height; }
    public struct EntityPos { public long KingId; public float X; public float Y; public float H; }
    public struct SimControlMsg { public bool Paused; public string SpeedId; }

    /// <summary>Encodes/decodes the kind-specific payloads of a <see cref="MirrorMessage"/>.</summary>
    public static class MirrorCodec
    {
        public static byte[] Write(GodPowerMsg m)
        {
            var w = new NetDataWriter();
            w.Put(m.PowerId ?? ""); w.Put(m.X); w.Put(m.Y);
            return w.CopyData();
        }
        public static GodPowerMsg ReadGodPower(byte[] data)
        {
            var r = new NetDataReader(data);
            return new GodPowerMsg { PowerId = r.GetString(), X = r.GetInt(), Y = r.GetInt() };
        }

        public static byte[] Write(SpawnMsg m)
        {
            var w = new NetDataWriter();
            w.Put(m.KingId); w.Put(m.AssetId ?? ""); w.Put(m.X); w.Put(m.Y); w.Put(m.Height);
            return w.CopyData();
        }
        public static SpawnMsg ReadSpawn(byte[] data)
        {
            var r = new NetDataReader(data);
            return new SpawnMsg { KingId = r.GetLong(), AssetId = r.GetString(), X = r.GetInt(), Y = r.GetInt(), Height = r.GetFloat() };
        }

        public static byte[] WriteEntityState(List<EntityPos> list)
        {
            var w = new NetDataWriter();
            w.Put((ushort)list.Count);
            foreach (var e in list) { w.Put(e.KingId); w.Put(e.X); w.Put(e.Y); w.Put(e.H); }
            return w.CopyData();
        }
        public static List<EntityPos> ReadEntityState(byte[] data)
        {
            var r = new NetDataReader(data);
            int n = r.GetUShort();
            var list = new List<EntityPos>(n);
            for (int i = 0; i < n; i++)
                list.Add(new EntityPos { KingId = r.GetLong(), X = r.GetFloat(), Y = r.GetFloat(), H = r.GetFloat() });
            return list;
        }

        public static byte[] Write(SimControlMsg m)
        {
            var w = new NetDataWriter();
            w.Put(m.Paused); w.Put(m.SpeedId ?? "");
            return w.CopyData();
        }
        public static SimControlMsg ReadSimControl(byte[] data)
        {
            var r = new NetDataReader(data);
            return new SimControlMsg { Paused = r.GetBool(), SpeedId = r.GetString() };
        }
    }
}

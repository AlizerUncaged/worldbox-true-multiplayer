using System.Collections.Generic;
using LiteNetLib.Utils;

namespace WorldboxMP.Shared.Protocol
{
    // Payload structs carried inside MirrorMessage.Payload. Shared so the server
    // can decode them for verbose logging and the client for apply.

    public struct GodPowerMsg { public string PowerId; public int X; public int Y; }
    public struct TerrainMsg { public int X; public int Y; public string TileId; public string TopId; }
    /// <summary>
    /// A full creature spawn. Carries the king actor's serialized ActorData (and its
    /// SubspeciesData, if any) so the follower can rebuild an identical creature —
    /// same subspecies, color, stats, speed, genes, traits, equipment, age, and id.
    /// </summary>
    public struct SpawnMsg
    {
        public long SubId;            // subspecies id referenced by the actor (0 = none)
        public string SubspeciesJson; // serialized SubspeciesData (empty if none/already known)
        public string ActorJson;      // serialized ActorData
    }
    public struct EntityPos { public long KingId; public float X; public float Y; public float H; }
    public struct SimControlMsg { public bool Paused; public string SpeedId; }
    public struct DeathMsg { public long KingId; }
    public struct FireMsg { public int X; public int Y; public bool On; }
    public struct TraitMsg { public long KingId; public string TraitId; public bool Add; }
    public struct MoveMsg { public long KingId; public int X; public int Y; }

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
            w.Put(m.SubId); w.Put(m.SubspeciesJson ?? ""); w.Put(m.ActorJson ?? "");
            return w.CopyData();
        }
        public static SpawnMsg ReadSpawn(byte[] data)
        {
            var r = new NetDataReader(data);
            return new SpawnMsg { SubId = r.GetLong(), SubspeciesJson = r.GetString(), ActorJson = r.GetString() };
        }

        public static byte[] Write(TerrainMsg m)
        {
            var w = new NetDataWriter();
            w.Put(m.X); w.Put(m.Y); w.Put(m.TileId ?? ""); w.Put(m.TopId ?? "");
            return w.CopyData();
        }
        public static TerrainMsg ReadTerrain(byte[] data)
        {
            var r = new NetDataReader(data);
            return new TerrainMsg { X = r.GetInt(), Y = r.GetInt(), TileId = r.GetString(), TopId = r.GetString() };
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

        public static byte[] WriteMoveBatch(List<MoveMsg> list)
        {
            var w = new NetDataWriter();
            w.Put((ushort)list.Count);
            foreach (var m in list) { w.Put(m.KingId); w.Put(m.X); w.Put(m.Y); }
            return w.CopyData();
        }
        public static List<MoveMsg> ReadMoveBatch(byte[] data)
        {
            var r = new NetDataReader(data);
            int n = r.GetUShort();
            var list = new List<MoveMsg>(n);
            for (int i = 0; i < n; i++)
                list.Add(new MoveMsg { KingId = r.GetLong(), X = r.GetInt(), Y = r.GetInt() });
            return list;
        }

        public static byte[] Write(DeathMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.KingId); return w.CopyData();
        }
        public static DeathMsg ReadDeath(byte[] data)
        {
            var r = new NetDataReader(data); return new DeathMsg { KingId = r.GetLong() };
        }

        public static byte[] Write(FireMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.X); w.Put(m.Y); w.Put(m.On); return w.CopyData();
        }
        public static FireMsg ReadFire(byte[] data)
        {
            var r = new NetDataReader(data); return new FireMsg { X = r.GetInt(), Y = r.GetInt(), On = r.GetBool() };
        }

        public static byte[] Write(TraitMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.KingId); w.Put(m.TraitId ?? ""); w.Put(m.Add); return w.CopyData();
        }
        public static TraitMsg ReadTrait(byte[] data)
        {
            var r = new NetDataReader(data); return new TraitMsg { KingId = r.GetLong(), TraitId = r.GetString(), Add = r.GetBool() };
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

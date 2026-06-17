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
    public struct ScalarMsg { public long Id; public int V; }
    public struct ActorStateMsg { public long Id; public string Json; }
    /// <summary>What a poll scalar batch carries (the 'kind' byte in WriteScalarBatch).</summary>
    public static class PollKind { public const byte Age = 0; }
    public struct WorldLawMsg { public string Id; public bool Value; }
    public struct TimeMsg { public double WorldTime; }
    public struct RenameMsg { public long Id; public string Name; }
    public struct HealthMsg { public long Id; public int Health; }
    public struct StatusMsg { public long Id; public string StatusId; public float Timer; public bool Add; }
    public struct EquipMsg { public long ActorId; public string ItemJson; }
    public struct ObjectMsg { public byte Kind; public string Json; }
    public struct ObjectRemoveMsg { public byte Kind; public long Id; }
    public struct MemberMsg { public long ActorId; public byte Kind; public long TargetId; }
    public struct CaptureMsg { public long CityId; public long KingdomId; public bool Captured; public bool Rebellion; }
    public struct KingdomPropMsg { public long KingdomId; public byte Prop; public long Value; }
    public struct WarEndMsg { public long WarId; public byte Winner; }
    public struct DisasterMsg { public int X; public int Y; public string DropId; }

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

        // Poll payload: a batch of per-entity (id, int value) scalars. Reused across poll
        // tiers (age now; needs/etc. later). The 'kind' byte distinguishes what the value is.
        public static byte[] WriteScalarBatch(byte kind, List<ScalarMsg> list)
        {
            var w = new NetDataWriter();
            w.Put(kind);
            w.Put((ushort)list.Count);
            foreach (var m in list) { w.Put(m.Id); w.Put(m.V); }
            return w.CopyData();
        }
        public static List<ScalarMsg> ReadScalarBatch(byte[] data, out byte kind)
        {
            var r = new NetDataReader(data);
            kind = r.GetByte();
            int n = r.GetUShort();
            var list = new List<ScalarMsg>(n);
            for (int i = 0; i < n; i++)
                list.Add(new ScalarMsg { Id = r.GetLong(), V = r.GetInt() });
            return list;
        }

        // Full per-actor state resync (force-override traits/vitals on followers).
        public static byte[] WriteActorResync(List<ActorStateMsg> list)
        {
            var w = new NetDataWriter();
            w.Put((ushort)list.Count);
            foreach (var m in list) { w.Put(m.Id); w.Put(m.Json); }
            return w.CopyData();
        }
        public static List<ActorStateMsg> ReadActorResync(byte[] data)
        {
            var r = new NetDataReader(data);
            int n = r.GetUShort();
            var list = new List<ActorStateMsg>(n);
            for (int i = 0; i < n; i++) list.Add(new ActorStateMsg { Id = r.GetLong(), Json = r.GetString() });
            return list;
        }

        public static byte[] Write(DisasterMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.X); w.Put(m.Y); w.Put(m.DropId ?? ""); return w.CopyData();
        }
        public static DisasterMsg ReadDisaster(byte[] data)
        {
            var r = new NetDataReader(data); return new DisasterMsg { X = r.GetInt(), Y = r.GetInt(), DropId = r.GetString() };
        }

        public static byte[] Write(ObjectRemoveMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.Kind); w.Put(m.Id); return w.CopyData();
        }
        public static ObjectRemoveMsg ReadObjectRemove(byte[] data)
        {
            var r = new NetDataReader(data); return new ObjectRemoveMsg { Kind = r.GetByte(), Id = r.GetLong() };
        }

        public static byte[] Write(MemberMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.ActorId); w.Put(m.Kind); w.Put(m.TargetId); return w.CopyData();
        }
        public static MemberMsg ReadMember(byte[] data)
        {
            var r = new NetDataReader(data); return new MemberMsg { ActorId = r.GetLong(), Kind = r.GetByte(), TargetId = r.GetLong() };
        }

        public static byte[] Write(CaptureMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.CityId); w.Put(m.KingdomId); w.Put(m.Captured); w.Put(m.Rebellion); return w.CopyData();
        }
        public static CaptureMsg ReadCapture(byte[] data)
        {
            var r = new NetDataReader(data); return new CaptureMsg { CityId = r.GetLong(), KingdomId = r.GetLong(), Captured = r.GetBool(), Rebellion = r.GetBool() };
        }

        public static byte[] Write(KingdomPropMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.KingdomId); w.Put(m.Prop); w.Put(m.Value); return w.CopyData();
        }
        public static KingdomPropMsg ReadKingdomProp(byte[] data)
        {
            var r = new NetDataReader(data); return new KingdomPropMsg { KingdomId = r.GetLong(), Prop = r.GetByte(), Value = r.GetLong() };
        }

        public static byte[] Write(WarEndMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.WarId); w.Put(m.Winner); return w.CopyData();
        }
        public static WarEndMsg ReadWarEnd(byte[] data)
        {
            var r = new NetDataReader(data); return new WarEndMsg { WarId = r.GetLong(), Winner = r.GetByte() };
        }

        public static byte[] Write(HealthMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.Id); w.Put(m.Health); return w.CopyData();
        }
        public static HealthMsg ReadHealth(byte[] data)
        {
            var r = new NetDataReader(data); return new HealthMsg { Id = r.GetLong(), Health = r.GetInt() };
        }

        public static byte[] Write(StatusMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.Id); w.Put(m.StatusId ?? ""); w.Put(m.Timer); w.Put(m.Add); return w.CopyData();
        }
        public static StatusMsg ReadStatus(byte[] data)
        {
            var r = new NetDataReader(data); return new StatusMsg { Id = r.GetLong(), StatusId = r.GetString(), Timer = r.GetFloat(), Add = r.GetBool() };
        }

        public static byte[] Write(EquipMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.ActorId); w.Put(m.ItemJson ?? ""); return w.CopyData();
        }
        public static EquipMsg ReadEquip(byte[] data)
        {
            var r = new NetDataReader(data); return new EquipMsg { ActorId = r.GetLong(), ItemJson = r.GetString() };
        }

        public static byte[] Write(ObjectMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.Kind); w.Put(m.Json ?? ""); return w.CopyData();
        }
        public static ObjectMsg ReadObject(byte[] data)
        {
            var r = new NetDataReader(data); return new ObjectMsg { Kind = r.GetByte(), Json = r.GetString() };
        }

        public static byte[] Write(TimeMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.WorldTime); return w.CopyData();
        }
        public static TimeMsg ReadTime(byte[] data)
        {
            var r = new NetDataReader(data); return new TimeMsg { WorldTime = r.GetDouble() };
        }

        public static byte[] Write(RenameMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.Id); w.Put(m.Name ?? ""); return w.CopyData();
        }
        public static RenameMsg ReadRename(byte[] data)
        {
            var r = new NetDataReader(data); return new RenameMsg { Id = r.GetLong(), Name = r.GetString() };
        }

        public static byte[] Write(WorldLawMsg m)
        {
            var w = new NetDataWriter(); w.Put(m.Id ?? ""); w.Put(m.Value); return w.CopyData();
        }
        public static WorldLawMsg ReadWorldLaw(byte[] data)
        {
            var r = new NetDataReader(data); return new WorldLawMsg { Id = r.GetString(), Value = r.GetBool() };
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

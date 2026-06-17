namespace WorldboxMP.Shared.Protocol
{
    /// <summary>Role of a player within a room (the room hierarchy).</summary>
    public enum RoomRole : byte
    {
        /// <summary>Regular member. Can play; cannot moderate.</summary>
        Player = 0,

        /// <summary>Can kick players and set laws. Cannot touch other admins or the king.</summary>
        Admin = 1,

        /// <summary>Room creator. Can do everything, including kick/demote admins. Exactly one per room.</summary>
        King = 2,
    }

    /// <summary>Whether a room is advertised in the public room list.</summary>
    public enum RoomVisibility : byte
    {
        /// <summary>Shown in the room selection list.</summary>
        Public = 0,

        /// <summary>Hidden from the list; joinable only via its shareable id.</summary>
        Unlisted = 1,
    }

    /// <summary>
    /// The category of a mirrored game action. The actual payload travels in a
    /// generic envelope so the protocol can grow without a new packet type per
    /// entity kind.
    /// </summary>
    public enum MirrorKind : byte
    {
        /// <summary>A god power applied at a tile (PlayerControl.clickedFinal).</summary>
        GodPower = 0,

        /// <summary>A unit/creature spawned (ActorManager.spawnNewUnit).</summary>
        SpawnUnit = 1,

        /// <summary>A terrain/tile change (MapAction.terraformTile).</summary>
        Terrain = 2,

        /// <summary>An entity position/state update (per-tick from the authoritative host).</summary>
        EntityState = 3,

        /// <summary>Pause / world-speed change (Config.paused / time scale).</summary>
        SimControl = 4,

        /// <summary>A unit died (Actor.die).</summary>
        Death = 5,

        /// <summary>A tile caught fire / was extinguished (WorldBehaviourActionFire).</summary>
        Fire = 6,

        /// <summary>A unit gained/lost a trait (Actor.addTrait/removeTrait).</summary>
        Trait = 7,

        /// <summary>
        /// Per-tile movement step(s): the king's actor committed to walk to a tile
        /// (Actor.moveTo). Batched per frame. Followers replay via moveTo so the game
        /// animates the walk (no polling, no snapping). Replaces the old EntityState.
        /// </summary>
        Move = 8,

        /// <summary>A world law was toggled (WorldLaws.setAgeEnabled).</summary>
        WorldLaw = 9,

        /// <summary>World time/year sync (MapStats.world_time), on each year boundary.</summary>
        Time = 10,

        /// <summary>An actor was renamed (NanoObject.setName).</summary>
        Rename = 11,

        /// <summary>Combat: an actor's health changed (Actor.getHit).</summary>
        Health = 12,

        /// <summary>A status effect was added/removed (addStatusEffect/finishStatusEffect).</summary>
        Status = 13,

        /// <summary>An item was equipped (ActorEquipment.setItem) — carries ItemData.</summary>
        Equip = 14,

        /// <summary>
        /// A structural object was created (kingdom/city/war/alliance/plot/religion/
        /// culture/language/building). Carries a kind tag + serialized XData; followers
        /// rebuild it via the matching manager.loadObject.
        /// </summary>
        Object = 15,

        /// <summary>A structural object was destroyed (manager.removeObject) — kind + id.</summary>
        ObjectRemove = 16,

        /// <summary>An actor joined a kingdom/religion/culture/language/city.</summary>
        Member = 17,

        /// <summary>A city changed owner (City.joinAnotherKingdom) — capture/rebellion.</summary>
        Capture = 18,

        /// <summary>A kingdom property changed (king / capital / color).</summary>
        KingdomProp = 19,

        /// <summary>A war ended (WarManager.endWar) — war id + winner.</summary>
        WarEnd = 20,

        /// <summary>A drop/disaster spawned (DropManager.spawn) — tile + drop id. Non-god-power only.</summary>
        Disaster = 21,
    }
}

namespace WorldboxMP.Shared.Protocol
{
    /// <summary>Sub-kinds for <see cref="MirrorKind.Member"/> (what an actor joined).</summary>
    public static class MemberKind
    {
        public const byte Kingdom = 1;
        public const byte Religion = 2;
        public const byte Culture = 3;
        public const byte Language = 4;
        public const byte City = 5;
    }

    /// <summary>Sub-kinds for <see cref="MirrorKind.KingdomProp"/>.</summary>
    public static class KingdomPropKind
    {
        public const byte King = 1;     // value = actor id
        public const byte Capital = 2;  // value = city id
        public const byte Color = 3;    // value = color id
    }
}

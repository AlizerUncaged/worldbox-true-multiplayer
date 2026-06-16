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
    }
}

using LiteNetLib;
using LiteNetLib.Utils;

namespace WorldboxMP.Shared.Protocol
{
    /// <summary>
    /// Shared networking constants and helpers. All packet DTOs implement
    /// <see cref="INetSerializable"/> and are routed by a
    /// <see cref="NetPacketProcessor"/> on each side (the processor identifies a
    /// packet by a hash of its full type name, so both sides agree automatically
    /// because the types live in this shared assembly).
    /// </summary>
    public static class NetProtocol
    {
        /// <summary>Connection key LiteNetLib uses to accept peers. Bump on breaking protocol changes.
        /// v2: mirror epoch, chunked snapshots on their own channel, follower resync requests.
        /// v3: multiple server lobbies, room caps, session tokens, RoomLeft.
        /// v4: event-based sync (actor record patches, vitals/overgrowth streams, snapshot
        ///     cut-over marker, actor roster), clans/families/armies mirrored.</summary>
        public const string ConnectKey = "WBMP/4";

        /// <summary>Default server port (overridable).</summary>
        public const int DefaultPort = 40441;

        /// <summary>LiteNetLib channel count on both sides. Channel 0 carries everything
        /// except world snapshots; channel 1 carries the (potentially huge) snapshot
        /// chunks so they can't head-of-line-block live mirror traffic.</summary>
        public const int ChannelCount = 2;

        /// <summary>Channel for <see cref="SnapshotChunk"/> transfers.</summary>
        public const byte SnapshotChannel = 1;

        /// <summary>Snapshot payload bytes per chunk. Small enough that live traffic
        /// interleaves between chunks; big enough to keep per-chunk overhead trivial.</summary>
        public const int SnapshotChunkSize = 48 * 1024;

        /// <summary>The fixed shareable id of the always-present first lobby. Further
        /// server lobbies get 0001, 0002, ... (player rooms never use digits-only ids).</summary>
        public const string Room0Id = "0000";

        /// <summary>Disconnect reason sent when the same account logs in somewhere else.
        /// The client must NOT auto-login again on this one (two games would kick each
        /// other forever).</summary>
        public const string SignedInElsewhere = "Signed in from another game - this one was disconnected.";

        /// <summary>Max room password length.</summary>
        public const int MaxPasswordLength = 16;

        /// <summary>Length of a shareable room id (uppercase alphanumeric).</summary>
        public const int ShareableIdLength = 4;

        /// <summary>Send a serializable packet over a peer with the given delivery method.</summary>
        public static void Send<T>(this NetPeer peer, NetPacketProcessor proc, T packet,
            DeliveryMethod method = DeliveryMethod.ReliableOrdered) where T : INetSerializable
        {
            var writer = new NetDataWriter();
            proc.WriteNetSerializable(writer, ref packet);
            peer.Send(writer, method);
        }

        /// <summary>Send a serializable packet on a specific LiteNetLib channel.</summary>
        public static void Send<T>(this NetPeer peer, NetPacketProcessor proc, T packet,
            byte channel, DeliveryMethod method) where T : INetSerializable
        {
            var writer = new NetDataWriter();
            proc.WriteNetSerializable(writer, ref packet);
            peer.Send(writer, channel, method);
        }
    }
}

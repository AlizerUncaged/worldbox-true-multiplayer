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
        /// <summary>Connection key LiteNetLib uses to accept peers. Bump on breaking protocol changes.</summary>
        public const string ConnectKey = "WBMP/1";

        /// <summary>Default server port (overridable).</summary>
        public const int DefaultPort = 40441;

        /// <summary>The fixed shareable id of the always-present first room.</summary>
        public const string Room0Id = "0000";

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
    }
}

using LiteNetLib;
using WorldboxMP.Server.Rooms;

namespace WorldboxMP.Server.Net
{
    /// <summary>Per-connection state for one connected client.</summary>
    public class Session
    {
        public NetPeer Peer { get; }

        /// <summary>External player id (Guid v4). Empty until authenticated.</summary>
        public string Id4 { get; set; } = "";
        public string Username { get; set; } = "";
        public bool IsAuthenticated => Id4.Length > 0;

        /// <summary>The room this session is currently in, if any.</summary>
        public Room CurrentRoom { get; set; }

        public Session(NetPeer peer) => Peer = peer;
    }
}

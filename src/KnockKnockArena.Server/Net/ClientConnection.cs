using System.Net.Sockets;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Shared.Protocol;

namespace KnockKnockArena.Server.Net
{
    public sealed class ClientConnection
    {
        private readonly TcpClient tcpClient;
        private readonly NetworkStream stream;
        private readonly object sendGate = new();
        public PlayerSession? Session;
        public string RemoteEndPoint { get; }

        public ClientConnection(TcpClient tcpClient)
        {
            this.tcpClient = tcpClient;
            stream = tcpClient.GetStream();
            RemoteEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }

        public string DisplayName =>
      Session == null ? RemoteEndPoint : $"{Session.Username}#{Session.PlayerId} ({RemoteEndPoint})";

        

    }
}
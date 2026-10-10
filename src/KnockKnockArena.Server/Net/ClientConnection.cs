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
        
        public string DisplayName =>
            Session == null ? RemoteEndPoint : $"{Session.Username}#{Session.PlayerId} ({RemoteEndPoint})";
 
        
        public ClientConnection(TcpClient tcpClient)
        {
            this.tcpClient = tcpClient;
            stream = tcpClient.GetStream();
            RemoteEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }
        
        public bool TryReadMessage(out MessageType type, out byte[] payload)
        {
            return TcpFraming.TryReadMessage(stream,out type, out payload);
        }
        
        public void Send(MessageType type, byte[] payload)
        {
            lock(sendGate)
                TcpFraming.WriteMessage(stream,type,payload);
        }
        
        public void Close()
        {
            try{tcpClient.Close();}
            catch
            {
                //Already closed
            }
        }

        
        

    }
}
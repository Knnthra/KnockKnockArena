using System.IO;

namespace KnockKnockArena.Shared.Protocol.Udp
{
    public sealed class DiscoveryRequestPacket
    {
        public const int Size = 6;

        public uint Nonce;

        public byte ClientVersion = ProtocolConstants.Version;
        public byte[] ToDatagram()
        {
            return UdpPacketIO.Build(UdpPacketType.DiscoveryRequest, writer => writer.Write(Nonce));
        }
        public static DiscoveryRequestPacket FromDatagramAnyVersion(byte[] datagram)
        {
            if (datagram.Length < Size)
                throw new ProtocolException("malformed payload");
            using (MemoryStream stream = new MemoryStream(datagram))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                reader.ReadByte();
                return new DiscoveryRequestPacket
                {
                    ClientVersion = reader.ReadByte(),
                    Nonce = reader.ReadUInt32(),
                };
            }
        }
    }
    
    public sealed class DiscoveryResponsePacket
    {
        public uint Nonce;
        public ushort GamePort;
        public ushort HttpPort;
        public byte PlayersOnline;
        public byte MaxPlayers;
        public string ServerName = "";
        public string MapName = "";
        public byte[] ToDatagram()
        {
            return UdpPacketIO.Build(UdpPacketType.DiscoveryResponse, writer =>
            {
                writer.Write(Nonce);
                writer.Write(GamePort);
                writer.Write(HttpPort);
                writer.Write(PlayersOnline);
                writer.Write(MaxPlayers);
                ProtocolSerialization.WriteString(writer, ServerName);
                ProtocolSerialization.WriteString(writer, MapName);
            });
        }
        public static byte PeekVersion(byte[] datagram)
        {
            if (datagram.Length < 2)
                throw new ProtocolException($"datagram too short ({datagram.Length} B)");
            return datagram[1];
        }
        
        public static DiscoveryResponsePacket FromDatagram(byte[] datagram)
        {
            return UdpPacketIO.Parse(datagram, reader => new DiscoveryResponsePacket
            {
                Nonce = reader.ReadUInt32(),
                GamePort = reader.ReadUInt16(),
                HttpPort = reader.ReadUInt16(),
                PlayersOnline = reader.ReadByte(),
                MaxPlayers = reader.ReadByte(),
                ServerName = ProtocolSerialization.ReadString(reader),
                MapName = ProtocolSerialization.ReadString(reader),
            });
        }
    }
}
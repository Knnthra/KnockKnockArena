namespace KnockKnockArena.Shared.Protocol.Udp
{

    public sealed class JoinPacket
    {
        public byte[] SessionToken = new byte[ProtocolConstants.SessionTokenLength];

        public byte[] ToDatagram()
        {
            return UdpPacketIO.Build(UdpPacketType.Join, writer =>
            {
                writer.Write(SessionToken);
            });
        }

        public static JoinPacket FromDatagram(byte[] datagram)
        {
            return UdpPacketIO.Parse(datagram, reader =>
            {
                byte[] token = reader.ReadBytes(ProtocolConstants.SessionTokenLength);
                if (token.Length != ProtocolConstants.SessionTokenLength)
                    throw new ProtocolException("malformed payload");
                return new JoinPacket { SessionToken = token };

            });
        }
    }

    public sealed class HeartbeatPacket
    {
        public byte PlayerId;

        public byte[] ToDatagram()
        {
            return UdpPacketIO.Build(UdpPacketType.Heartbeat, writer =>
            {
                writer.Write(PlayerId);
            });
        }

        public static HeartbeatPacket FromDatagram(byte[] datagram)
        {
            return UdpPacketIO.Parse(datagram, reader => new
                HeartbeatPacket
            {
               PlayerId = reader.ReadByte(),

            });
        }
    }

}
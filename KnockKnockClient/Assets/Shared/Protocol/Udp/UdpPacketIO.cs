using System;
using System.IO;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Shared.Protocol.Udp
{
    public static class UdpPacketIO
    {
        public static UdpPacketType PeekType(byte[] datagram)
        {
            if (datagram.Length < 2)
                throw new ProtocolException($"datagram too short ({datagram.Length} B)");

            return (UdpPacketType)datagram[0];
        }

        public static byte[] Build(UdpPacketType type, Action<BinaryWriter> writeFields)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write((byte)type);
                    writer.Write(ProtocolConstants.Version);
                    writeFields(writer);
                }

                return stream.ToArray();
            }
        }

        public static T Parse<T>(byte[] datagram, Func<BinaryReader, T> readFields)
        {
            using (MemoryStream stream = new MemoryStream(datagram))
            {
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    try
                    {
                        reader.ReadByte();
                        ProtocolSerialization.ReadAndValidateVersion(reader);
                        return readFields(reader);
                    }
                    catch(EndOfStreamException)
                    {
                        throw new ProtocolException("malformed payload");   
                    }
                }
            }
        }
    }
}
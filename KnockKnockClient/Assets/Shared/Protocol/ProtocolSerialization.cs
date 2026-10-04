using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace KnockKnockArena.Shared.Protocol
{
    public static class ProtocolSerialization
    {
        public static void WriteVersion(BinaryWriter writer)
        {
            writer.Write(ProtocolConstants.Version);
        }

        public static void ReadAndValidateVersion(BinaryReader reader)
        {
            byte version = reader.ReadByte();
            if (version != ProtocolConstants.Version)
                throw new ProtocolException($"bad version {version}");
        }

        public static byte[] WritePayload(System.Action<BinaryWriter> write)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    WriteVersion(writer);
                    write(writer);
                }
                return stream.ToArray();
            }
        }

        public static T ReadPayload<T>(byte[] payload, System.Func<BinaryReader, T> read)
        {
            using (MemoryStream stream = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                try
                {
                    ReadAndValidateVersion(reader);
                    return read(reader);
                }
                catch (EndOfStreamException)
                {
                    throw new ProtocolException("malformed payload");
                }
            }
        }
        
        public static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if(bytes.Length > ProtocolConstants.MaxStringLength)
                throw new ProtocolException($"string too long {bytes.Length}");
            
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
            
        }
        
        public static string ReadString(BinaryReader reader)
        {
            ushort length = reader.ReadUInt16();
            if(length > ProtocolConstants.MaxStringLength)
                throw new ProtocolException($"string too long {length}");
                
            byte[] bytes = reader.ReadBytes(length);
            if(bytes.Length != length)
                throw new ProtocolException("payload truncated");
                
            return Encoding.UTF8.GetString(bytes);
        }
    }

}
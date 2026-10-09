using System.ComponentModel;
using System.IO;

namespace KnockKnockArena.Shared.Protocol
{
    public static class TcpFraming
    {
        public static void WriteMessage(Stream stream, MessageType type, byte[] payload)
        {
            BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(payload.Length);
            writer.Write((byte)type);
            writer.Write(payload);
            writer.Flush();
        }

        private static bool TryReadExact(Stream stream, byte[] buffer, bool allowCleanEndOfStream)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    if (offset == 0 && allowCleanEndOfStream)
                        return false; 
                    throw new ProtocolException("connection closed mid-message", isFatal: true);
                    
                }
                offset += read;
            }
            return true;
        }
        
        public static bool TryReadMessage(Stream stream, out MessageType type, out byte[] payload)
        {
            type = default;
            payload = System.Array.Empty<byte>();
            byte[] header = new byte[5];
            if(!TryReadExact(stream,header,allowCleanEndOfStream: true))
                return false;
            
            int length = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
            if(length < 0 || length > ProtocolConstants.MaxPayloadLength)
                throw new ProtocolException($"invalid length {length}", isFatal: true);
                
            type = (MessageType)header[4];
            
            payload = new byte[length];
            TryReadExact(stream,payload, allowCleanEndOfStream: false);
            
            return true;
        }


    }
}
namespace KnockKnockArena.Shared.Protocol.Messages
{
    public sealed class ServerInfoMessage
    {
        public string ServerVersion = "";
        public byte TickRate;
        public byte MaxPlayers;
        public byte PlayersOnline;
        public string MapName = "";
        public uint MapHash;

        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                ProtocolSerialization.WriteString(writer, ServerVersion);
                writer.Write(TickRate);
                writer.Write(MaxPlayers);
                writer.Write(PlayersOnline);
                ProtocolSerialization.WriteString(writer, MapName);
                writer.Write(MapHash);
            });
        }

         public static ServerInfoMessage FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new ServerInfoMessage
            {
                ServerVersion = ProtocolSerialization.ReadString(reader),
                TickRate = reader.ReadByte(),
                MaxPlayers = reader.ReadByte(),
                PlayersOnline = reader.ReadByte(),
                MapName = ProtocolSerialization.ReadString(reader),
                MapHash = reader.ReadUInt32(),
            });
        }
    }
}
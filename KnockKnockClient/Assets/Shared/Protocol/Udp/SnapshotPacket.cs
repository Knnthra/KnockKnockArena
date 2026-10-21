using System.Collections.Generic;
using System.IO;

namespace KnockKnockArena.Shared.Protocol.Udp
{
    /// <summary>
    /// Server -> one client. The FULL game state, sent every tick (30 Hz) to every
    /// joined player. No delta compression — a deliberate choice so every packet can
    /// be read standalone in Wireshark.
    ///
    /// LastProcessedInputIndex is personal to the recipient (it is THEIR input
    /// counter), so each client gets its own copy of the snapshot.
    ///
    /// Datagram: [type=100] [version] [sequence:uint32] [serverTick:uint32]
    ///           [lastProcessedInputIndex:uint32] [entityCount:byte]
    ///           then per entity: [entityId:ushort] [entityType:byte] [type-specific data]
    ///
    /// Player entity: see PlayerEntityState, which serializes itself (39 bytes).
    /// Module 6: players only. Pickups join the snapshot in M07, rockets in M09.
    /// </summary>
    public sealed class SnapshotPacket
    {
        /// <summary>Increases by 1 per snapshot. Discard any snapshot older than the newest seen.</summary>
        public uint Sequence;

        public uint ServerTick;

        /// <summary>The recipient's newest input index the server had processed when this snapshot was built. 0 = none yet.</summary>
        public uint LastProcessedInputIndex;

        public List<PlayerEntityState> Players = new List<PlayerEntityState>();

        public byte[] ToDatagram()
        {
            return UdpPacketIO.Build(UdpPacketType.Snapshot, writer =>
            {
                writer.Write(Sequence);
                writer.Write(ServerTick);
                writer.Write(LastProcessedInputIndex);
                writer.Write((byte)Players.Count);
                foreach (PlayerEntityState player in Players)
                    player.WriteTo(writer);
            });
        }

        public static SnapshotPacket FromDatagram(byte[] datagram)
        {
            return UdpPacketIO.Parse(datagram, reader =>
            {
                SnapshotPacket snapshot = new SnapshotPacket
                {
                    Sequence = reader.ReadUInt32(),
                    ServerTick = reader.ReadUInt32(),
                    LastProcessedInputIndex = reader.ReadUInt32(),
                };

                byte entityCount = reader.ReadByte();
                for (int i = 0; i < entityCount; i++)
                {
                    ushort entityId = reader.ReadUInt16();
                    EntityType entityType = (EntityType)reader.ReadByte();
                    switch (entityType)
                    {
                        case EntityType.Player:
                            snapshot.Players.Add(PlayerEntityState.ReadData(reader, entityId));
                            break;
                        default:
                            // Entity data has no length prefix, so an unknown type
                            // cannot be skipped — that is what the version field is for.
                            throw new ProtocolException($"unknown entity type {(byte)entityType}");
                    }
                }

                return snapshot;
            });
        }
    }
}

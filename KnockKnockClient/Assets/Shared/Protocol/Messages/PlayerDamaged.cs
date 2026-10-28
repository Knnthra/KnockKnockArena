namespace KnockKnockArena.Shared.Protocol.Messages
{
    /// <summary>
    /// Server -> all clients. Someone took damage, and this is WHERE it landed.
    ///
    /// The snapshot already carries every player's health, so a client can see that
    /// someone got hurt — but not where the shot went in. Only the server knows that:
    /// it is the ray's entry point into the victim's box, computed during hit
    /// detection and otherwise thrown away. Without this event the blood has nowhere
    /// to go but the middle of the body.
    ///
    /// Same rule as RocketExploded (Video 6.7): EFFECTS PLAY ON EVENTS, state lives
    /// in snapshots. Do not try to drive this off a health drop between two
    /// snapshots — you would get the timing roughly right and the position wrong.
    ///
    /// Payload: [version:byte] [victimId:byte] [x:float] [z:float]
    ///          [attackerId:byte] [weaponId:byte] [damage:byte]
    /// </summary>
    public sealed class PlayerDamaged
    {
        public byte VictimId;

        /// <summary>Impact point on the XZ plane. The server has no height — the
        /// client picks one (chest height reads best).</summary>
        public float X;
        public float Z;

        /// <summary>Who dealt it. Equal to <see cref="VictimId"/> for self-damage
        /// from your own rocket.</summary>
        public byte AttackerId;

        public byte WeaponId;

        /// <summary>How much was taken. Not needed for the blood, but it is what a
        /// damage number would read, and it costs one byte.</summary>
        public byte Damage;

        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                writer.Write(VictimId);
                writer.Write(X);
                writer.Write(Z);
                writer.Write(AttackerId);
                writer.Write(WeaponId);
                writer.Write(Damage);
            });
        }

        public static PlayerDamaged FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new PlayerDamaged
            {
                VictimId = reader.ReadByte(),
                X = reader.ReadSingle(),
                Z = reader.ReadSingle(),
                AttackerId = reader.ReadByte(),
                WeaponId = reader.ReadByte(),
                Damage = reader.ReadByte(),
            });
        }
    }
}

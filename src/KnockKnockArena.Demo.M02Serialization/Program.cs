using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;

PlayerEntityState player = new()
{
    EntityId = 1,
    X = 132f,
    Z = 132f,
    AimX = 142f,
    AimZ = 132f,
    Health = 100,
    ActiveWeapon = WeaponId.Glock,
    OwnedWeapons = WeaponBits.StartingWeapons,
    Ammo = 24,
};

// byte[] payload = ProtocolSerialization.WritePayload(writer => player.WriteTo(writer));
// Console.WriteLine($"Serialized: {payload.Length} bytes (1 version byte + {PlayerEntityState.SerializedSize} entity bytes)");
// Console.WriteLine();
// Console.WriteLine(BitConverter.ToString(payload));

// PlayerEntityState back = ProtocolSerialization.ReadPayload(payload, PlayerEntityState.ReadFrom);
// bool same = back.EntityId == player.EntityId && back.X == player.X && back.Z == player.Z &&
//             back.AimX == player.AimX && back.AimZ == player.AimZ && back.Health == player.Health &&
//             back.ActiveWeapon == player.ActiveWeapon && back.OwnedWeapons == player.OwnedWeapons &&
//             back.Ammo == player.Ammo && back.Frags == player.Frags && back.Deaths == player.Deaths &&
//             back.State == player.State && back.DashTicksLeft == player.DashTicksLeft &&
//             back.DashCooldownTicks == player.DashCooldownTicks &&
//             back.DashDirectionX == player.DashDirectionX && back.DashDirectionZ == player.DashDirectionZ;

// Console.WriteLine(same ? "all fields are equal" : "MISMATCH!");

// byte[] stale = (byte[])payload.Clone();
// stale[0] = ProtocolConstants.Version - 1;
// try
// {
//     ProtocolSerialization.ReadPayload(stale, PlayerEntityState.ReadFrom);
// }
// catch (ProtocolException ex)
// {
//     Console.WriteLine($"rejected with \"{ex.Reason}\"");
// }


Console.WriteLine($"WriteString(\"test\") -> {Bytes(writer => ProtocolSerialization.WriteString(writer, "test"))}");
Console.WriteLine($"WriteString(\"hæk\")  -> {Bytes(writer => ProtocolSerialization.WriteString(writer, "hæk"))}");
Console.WriteLine($"BinaryWriter.Write(\"hæk\") -> {Bytes(writer => writer.Write("hæk"))}");
 

static string Bytes(Action<BinaryWriter> write)
{
    using MemoryStream stream = new();
    using (BinaryWriter writer = new (stream))
        write(writer);
    return BitConverter.ToString(stream.ToArray());
}

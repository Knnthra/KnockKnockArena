// M01 demo: the owned-weapons bitfield, one bit per weapon id.
//
//   dotnet run --project src/KnockKnockArena.Demo.M01Bits
//
// Walks one life of a player: spawn with Punch + Glock, pick up an AK47 and a
// rocket launcher, die, respawn. Every step prints the byte in binary, hex and
// decimal, so the bits can be read off the screen. The same byte travels in
// every snapshot as [ownedWeapons:byte] (M06/M07).
// The code follows the M01 deck (slides 23, 35, 36, 45 and 51), comments included.
using KnockKnockArena.Shared.Protocol.Udp;

// start: de våben, en ny spiller har
byte owned = WeaponBits.StartingWeapons;
Show("spawn (Punch + Glock)", owned);
// 1 til 5: samme sekvens som på slide 34
owned = WeaponBits.AddWeapon(owned, WeaponId.Ak47);
Show("picks up an AK47", owned);
owned = WeaponBits.AddWeapon(owned, WeaponId.RocketLauncher);
Show("picks up a rocket launcher", owned);
owned = WeaponBits.AddWeapon(owned, WeaponId.Ak47);
Show("picks up a second AK47", owned);
owned = WeaponBits.RemoveWeapon(owned, WeaponId.RocketLauncher);
Show("RemoveWeapon(RocketLauncher)", owned);
owned = WeaponBits.StartingWeapons;
Show("dies and respawns", owned);

// spørg én byte om hvert våben (slide 39)
byte query = 0b0000_1101;
Console.WriteLine("Which weapons does 0b0000_1101 hold?");
foreach (WeaponId w in Enum.GetValues(typeof(WeaponId)))
{
    string mask = WeaponBits.ToBinaryString((byte)(1 << (int)w));
    string answer = WeaponBits.HasWeapon(query, w) ? "yes" : "no";
    Console.WriteLine($"  {w,-15} mask {mask}  -> {answer}");
}

Console.WriteLine();
Console.WriteLine("The data types in a player's state:");
Row("type", "bytes", "range", "used for");
Row("byte", sizeof(byte).ToString(), $"{byte.MinValue} .. {byte.MaxValue}", "health, owned weapons (this bitfield)");
Row("ushort", sizeof(ushort).ToString(), $"{ushort.MinValue} .. {ushort.MaxValue}", "ammo, frags, deaths, entity id");
Row("uint", sizeof(uint).ToString(), $"{uint.MinValue} .. {uint.MaxValue}", "server tick, input index");
Row("float", sizeof(float).ToString(), $"+/-{float.MaxValue:0.0E+00}, ~7 digits", "x, z, aim point");
Row("int", sizeof(int).ToString(), $"{int.MinValue} .. {int.MaxValue}", "(not on the wire: ammo is never negative)");

byte health = 255;
health++;
Console.WriteLine($"  byte 255 + 1 = {health}: C# raises no error on overflow, so limits must be checked in code - as the server does with MaxHealth");
Console.WriteLine($"  the float 132.0 is the 4 bytes {BitConverter.ToString(BitConverter.GetBytes(132f))} (IEEE 754, see M02)");

// Bit flags: the movement keys in every Input packet are one byte of [Flags].
Console.WriteLine();
MovementBits keys = MovementBits.W | MovementBits.D | MovementBits.Sprint;
Console.WriteLine($"Keys held: W + D + Shift -> movement byte {WeaponBits.ToBinaryString((byte)keys)} = 0x{(byte)keys:X2} = {(byte)keys}");
Console.WriteLine($"  [Flags] prints it as \"{keys}\"; is Sprint set? {(keys & MovementBits.Sprint) != 0}; is S set? {(keys & MovementBits.S) != 0}");
MovementBits released = keys & ~MovementBits.Sprint;
Console.WriteLine($"  release Shift (AND NOT Sprint): {WeaponBits.ToBinaryString((byte)released)} = \"{released}\"");
ButtonBits buttons = (ButtonBits)0b0000_0011;
Console.WriteLine($"  a buttons byte of 00000011 means \"{buttons}\" - both travel in the 22-byte Input packet (M06)");

// Two's complement: the same eight bits read as an unsigned byte and a signed sbyte.
Console.WriteLine();
Console.WriteLine("Same eight bits, two types:");
byte allOnes = 0b1111_1111;
sbyte asSigned = unchecked((sbyte)allOnes);
Console.WriteLine($"  {WeaponBits.ToBinaryString(allOnes)} -> byte {allOnes}, sbyte {asSigned}");

void Row(string type, string bytes, string range, string usedFor)
    => Console.WriteLine($"  {type,-6}  {bytes,5}  {range,-33}  {usedFor}");

// skriver én linje: tekst, bits, hex, decimal og våbnenes navne
void Show(string label, byte value)
{
    var names = new List<string>();
    foreach (WeaponId w in Enum.GetValues(typeof(WeaponId)))
        if (WeaponBits.HasWeapon(value, w)) names.Add(w.ToString());

    Console.WriteLine($"{label,-30} {WeaponBits.ToBinaryString(value)}  " +
        $"0x{value:X2}  {value,3}  [{string.Join(", ", names)}]");
}

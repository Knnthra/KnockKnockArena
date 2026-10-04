# Modul 2 – test af jeres serialisering

Automatiske tests af `PlayerEntityState` og `ProtocolSerialization`. De kører mod **jeres egen** implementering og sammenligner jeres bytes med facit – byte for byte.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M02` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`:

   ```
   jeres-repo/
     src/KnockKnockArena.Shared/KnockKnockArena.Shared.csproj
     tests/KnockKnockArena.Tests.M01/
     tests/KnockKnockArena.Tests.M02/     <- denne mappe
   ```

2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M02
   ```

3. Målet er **85 grønne tests**: `Passed! - Failed: 0, Passed: 85`.

## Hvad testene forventer

Testene kalder jeres kode præcis sådan her – navne og typer skal passe:

```csharp
namespace KnockKnockArena.Shared.Protocol

public static class ProtocolConstants { public const byte Version = 6; public const int MaxStringLength = 1024; }

public sealed class ProtocolException : Exception
{
    public string Reason { get; }
    public bool IsFatal { get; }
}

public static class ProtocolSerialization
{
    public static byte[] WritePayload(Action<BinaryWriter> write);                  // versionsbyte + resten
    public static T ReadPayload<T>(byte[] payload, Func<BinaryReader, T> read);     // tjekker versionen først
    public static void WriteString(BinaryWriter writer, string value);             // [længde:ushort][UTF-8]
    public static string ReadString(BinaryReader reader);
}

namespace KnockKnockArena.Shared.Protocol.Udp

public enum EntityType : byte { Player = 1, Rocket = 2, Pickup = 3 }
public enum PlayerLifeState : byte { Alive = 0, Dead = 1 }

public sealed class PlayerEntityState
{
    public const int SerializedSize = 39;
    // felterne: EntityId, X, Z, AimX, AimZ, Health, ActiveWeapon, OwnedWeapons, Ammo,
    //           Frags, Deaths, State, DashTicksLeft, DashCooldownTicks, DashDirectionX, DashDirectionZ
    public void WriteTo(BinaryWriter writer);
    public static PlayerEntityState ReadFrom(BinaryReader reader);                 // med header, tjekker typen
    public static PlayerEntityState ReadData(BinaryReader reader, ushort entityId); // uden header
}
```

Fejlbeskederne (`Reason`) testes ikke: testene tjekker kun, at den rigtige undtagelse bliver kastet. Teksterne fra slidesene (`bad version 5`, `malformed payload`, `string too long 1025`, `payload truncated`, `expected a player entity, got type 2`) er et forslag, så loggen bliver let at søge i.

## Når en test fejler

To spillere bruges. Demo-spilleren er den fra `Demo.M02Serialization`. Den anden spiller har forskellige værdier i alle felter, så to felter i forkert rækkefølge ikke gemmer sig bag ens værdier. `EveryField_SitsAtItsOffset_LittleEndian` fortæller, hvilket felt der er galt:

```
x at offset 4: got 00-00-10-C0, expected 00-00-C0-3F
```

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| `EveryField_...` fejler for to felter, der har byttet værdier | Felterne er skrevet i forkert rækkefølge. Læs og skriv skal følge layoutet på slide 6 – også selv om jeres egen round trip virker, fordi begge sider tager fejl på samme måde |
| `DemoPlayer_IsFortyBytes...` fejler med fx 42 eller 39 bytes | 42: et `ushort`-felt er skrevet som `int` (`writer.Write((int)Ammo)`). 39: typebyten eller versionsbyten mangler |
| `ReadPayload_TurnsATruncatedPayload...` fejler med `EndOfStreamException` | `ReadPayload` fanger ikke `EndOfStreamException` og kaster `ProtocolException("malformed payload")` i stedet |
| `ReadPayload_RejectsAnotherVersion` fejler | Versionen tjekkes ikke, eller der tjekkes med `>` i stedet for `!=` – en ældre klient skal også afvises |
| `WriteString_IsUshortByteLengthThenUtf8` fejler for `"hæk"` med `03-00-…` | Længden er antal tegn (`value.Length`), ikke antal bytes. `æ` fylder to bytes i UTF-8 |
| `WriteString_...` giver `04-74-65-73-74` | I bruger `BinaryWriter.Write(string)`, der skriver sin egen 7-bit-længde på én byte. Protokollen bruger `[længde:ushort]` |
| `String_RoundTrips` fejler for `"hæk"` | `Encoding.ASCII` i stedet for `Encoding.UTF8` – `æ` bliver til `?` |

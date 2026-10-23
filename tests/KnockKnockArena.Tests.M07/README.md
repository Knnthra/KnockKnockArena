# Modul 7 – test af pickup-entiteten og interpolationsbufferen

Automatiske tests af `SnapshotPacket` med pickups og af jeres `InterpolationBuffer`. De kører mod **jeres egen** kode.

`InterpolationBuffer` ligger i Unity-projektets netkode (`KnockKnockClient/Assets/Scripts/Networking/`), men den er ren C# uden `UnityEngine`. Derfor kan testprojektet kompilere filen direkte – den skal bare ligge præcis dér.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M07` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M07
   ```

3. Målet er **20 grønne tests**: `Passed! - Failed: 0, Passed: 20`.

## Hvad testene forventer

```csharp
namespace KnockKnockArena.Shared.Protocol.Udp
public sealed class PickupEntityState { public ushort EntityId; public PickupType Type; public bool Active; }
public sealed class SnapshotPacket { ..., List<PlayerEntityState> Players, List<PickupEntityState> Pickups; ... }
// pickup-entiteten: [entityId:ushort] [entityType:byte = 3] [pickupType:byte] [active:bool] = 5 bytes

namespace KnockKnockArena.Networking
public sealed class InterpolationBuffer
{
    public void Add(uint tick, float x, float z, float aimX, float aimZ);
    public bool TryLatest(out float x, out float z, out float aimX, out float aimZ);
    public bool TrySample(double renderTick, out float x, out float z, out float aimX, out float aimZ);
}
```

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| `OnePickup_Is15Plus5Bytes` har de to sidste bytes byttet om | `Active` skrevet før pickup-typen |
| `PlayersAndPickups_CountTogether` får 1 i stedet for 14 | Antallet i headeren tæller kun spillerne |
| `APickupFirst_IsReadToo` eller `ATruncatedPickup…` giver `unknown entity type 3` | `case EntityType.Pickup` mangler i læsningen |
| `PastTheNewestSample…` giver en position længere fremme | Bufferen gætter fremad (extrapolation). Clamp til den nyeste prøve |
| `AnOlderTick_IsIgnored` fejler | `Add` gemmer prøver for ticks, der ikke er nyere end den sidste |
| `AJumpOver3Metres…` giver en position midt imellem | Et spring over 3 m på ét tick skal tegnes som et spring (respawn) |
| Kun sigte-værdierne er forkerte | Sigtet blandes ikke – det skal blandes med samme `alpha` som positionen |
| `KeyedOnServerTicks_NotOnArrival` giver 100,8 | `alpha` er regnet i prøvenumre i stedet for ticks: et hul efter et tabt snapshot blandes forkert |
| `KeepsAtMost64Samples` fejler | Den ældste prøve fjernes ikke, når der er over 64 |

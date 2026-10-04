# Modul 9 – test af beskederne, pickup-tabellen og input-reglen

Automatiske tests af de tre nye TCP-events (`PlayerDamaged`, `PlayerDied`, `RocketExploded`), raket-entiteten i snapshottet og serverens pickup-tabel (`PickupTable`, `data/pickups.json`) – og af serverens input-regel fra Modul 6 (implementering 7), som er det første testprojekt, der kan køre jeres `GameWorld`. De kører mod **jeres egen** Shared-kode og **jeres egen** server.

Et træf, et drab og en eksplosion er hændelser, som alle klienter skal have: skriver serveren en besked i én rækkefølge og klienten læser den i en anden, lander blodet et forkert sted, kill feed'en viser forkerte navne, eller raketten står på den forkerte side af banen.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M09` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M09
   ```

3. Målet er **33 grønne tests**: `Passed! - Failed: 0, Passed: 33`.

Testprojektet refererer både `src/KnockKnockArena.Shared` og `src/KnockKnockArena.Server` (pickup-tabellen og input-reglen er kun på serveren), og det har sin egen kopi af facits `pickups.json`.

Input-testene (`InputRuleTests`) kører jeres rigtige `GameWorld` ét `Tick` ad gangen på en lille bane uden vægge – intet netværk, ingen timing: en klient, der sender 60 inputs i sekundet, flytter sig 6 m på 30 ticks (ikke 12); inputs bruges i indeks-rækkefølge; et manglende input ventes der på i op til 3 ticks; gamle inputs og dubletter droppes (`stale input N`); køen holder 8 (`input queue full`); ticks uden input gemmer højst 4 pladser; og en død spillers inputs kvitteres stadig.

## Hvad testene forventer

```csharp
namespace KnockKnockArena.Shared.Protocol
public enum MessageType : byte { ..., PlayerDied = 105, PickupTaken = 106, RocketExploded = 107, PlayerDamaged = 108 }

namespace KnockKnockArena.Shared.Protocol.Messages
public sealed class PlayerDamaged  { byte VictimId; float X, Z; byte AttackerId, WeaponId, Damage; ToPayload(); static FromPayload(byte[]); }
public sealed class PlayerDied     { byte KillerId; string KillerUsername; byte VictimId; string VictimUsername; byte WeaponId; ... }
public sealed class RocketExploded { ushort RocketEntityId; float X, Z; byte OwnerId; ... }
// PlayerDamaged: [version] [victimId] [x:float] [z:float] [attackerId] [weaponId] [damage]              = 13 B
// PlayerDied:    [version] [killerId] [killerUsername:string] [victimId] [victimUsername:string] [weaponId]
// RocketExploded:[version] [rocketEntityId:ushort] [x:float] [z:float] [ownerId]                        = 12 B

namespace KnockKnockArena.Shared.Protocol.Udp
public sealed class RocketEntityState { ushort EntityId; float X, Z, DirectionX, DirectionZ; byte OwnerId; }
// SnapshotPacket.Rockets, skrevet mellem spillere og pickups:
// [entityId:ushort] [entityType:byte = 2] [x:float] [z:float] [dirX:float] [dirZ:float] [ownerId:byte] = 20 B

namespace KnockKnockArena.Server.Game
public sealed class PickupTable { static PickupTable Parse(string json); static PickupTable Defaults(); float RadiusFor(PickupType); }
public sealed class GameWorld   { GameWorld(); bool SpawnOrRebind(PlayerSession, IPEndPoint, out PlayerState); bool TryEnqueueInput(PlayerState, InputPacket, out string reason); void Tick(... seks lister ...); uint ServerTick; }
public sealed class PlayerState { float X, Z; uint LastProcessedInputIndex; PlayerLifeState LifeState; uint DeathTick; }
// Input-reglen: +1 i budget pr. tick (højst 4), -1 pr. brugt input; kø på 8 efter InputIndex;
// reason er en tekst, der ikke er tom (facit: "stale input N" og "input queue full"); op til 3 ticks venten på et hul
```

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| `PlayerDamaged_Is13Bytes` har x og z byttet om | Felterne skrives i en anden rækkefølge end protokollens |
| `PlayerDied_NameLengthIsInBytes_NotCharacters` fejler | Længden foran navnet er antal tegn, ikke antal UTF-8-bytes (Å fylder 2) |
| `RocketExploded_Is12Bytes` er 11 bytes | Raketens id er skrevet som en byte – det er en `ushort` (1000 og op) |
| `Entities_ArePlayersThenRocketsThenPickups` fejler | Raketterne skrives efter pickups, eller tælles ikke med i entity-tællingen |
| `Rockets_RoundTrip` får retningen byttet om | `DirectionX` og `DirectionZ` læses i forkert rækkefølge |
| `ATypeListedTwice_IsRejected` fejler | Dubletter tjekkes ikke – den sidste post vinder i stilhed |
| `TwentyMetres_IsTheLargestAllowed` fejler | Grænsen er `< 20` i stedet for `<= 20` |
| `AnUnknownType_IsRejected` fejler | En ukendt type springes over i stedet for at stoppe serveren |
| `SixtyInputsASecond_…` viser ca. 12 m | Serveren bruger alle inputs i køen hvert tick – der mangler et budget på ét pr. tick |
| `ReorderedInputs_…` fejler | Køen er en `ConcurrentQueue` i ankomstrækkefølge, ikke sorteret efter `InputIndex` |
| `AMissingInput_IsWaitedFor…` fejler | Hullet springes over med det samme, eller budgettet gemmer ikke pladsen |
| `…IsStaleIfItComesLater` / `ADuplicateInput_…` fejler | Et indeks ≤ `LastProcessedInputIndex` eller et, der allerede er i kø, lægges i køen |
| `TheQueue_HoldsEightInputs` fejler | Køens loft er ikke 8 |
| `TicksWithoutInput_SaveAtMostFourPlaces` fejler | Budgettet er ikke loftet ved 4 |

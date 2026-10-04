# Modul 6 – test af pakkerne og bevægelsesreglen

Automatiske tests af `InputPacket`, `SnapshotPacket` og `MovementSimulation`, plus selvtjekket `SimulationDigest` sammenlignet med facit. De kører mod **jeres egen** Shared-kode.

Prediction virker kun, hvis klienten regner **præcis** det samme som serveren – ned til den sidste bit i hver float. Det kan man ikke se på koden. Man skal måle det, og det gør selvtjekket.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M06` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kopiér den udleverede `SimulationDigest.cs` til `KnockKnockClient/Assets/Shared/Simulation/`. Testene kalder den, og I kører den samme fil i Unity (menuen **KnockKnock → Validate: dump map + simulation bits**, fra `MapBitsDump.cs`).
3. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M06
   ```

4. Målet er **35 grønne tests**: `Passed! - Failed: 0, Passed: 35`.

## Hvad testene forventer

```csharp
namespace KnockKnockArena.Shared.Protocol.Udp
public enum UdpPacketType : byte { Join = 0, Input = 1, Heartbeat = 2, ..., Snapshot = 100, ... }
public sealed class InputPacket { PlayerId, InputIndex, Tick, Movement, AimX, AimZ, Buttons, WeaponId; ToDatagram(); static FromDatagram(byte[]); }   // 22 B
public sealed class SnapshotPacket { Sequence, ServerTick, LastProcessedInputIndex, List<PlayerEntityState> Players; ... }             // 15 B + 39 B pr. spiller

namespace KnockKnockArena.Shared.Simulation
public static class MovementSimulation
{
    public struct DashState { byte TicksLeft; byte CooldownTicks; float DirectionX; float DirectionZ; }
    public static void Step(ref float x, ref float z, ref DashState dash, MovementBits movement, float aimX, float aimZ, float deltaTime);
    public static void ResolveCollisions(ref float x, ref float z);
}
```

Testprojektet har sin egen kopi af kursets bane (`arena.json`, "Main", hash `0x98A578E2`) og facits output (`facit_digest.txt`, 161 linjer).

## Når digest-testen fejler

Testen melder den **første** linje, der afviger. Rækkefølgen i outputtet følger kæden, så linjen siger, hvor det gik galt:

| Første afvigende linje | Betyder | Typisk årsag |
|---|---|---|
| `hash`, `bounds`, `wall …`, `spawn …` | Banen er læst forkert | Banefilen er ikke facits – eller I har skrevet jeres egen parser i stedet for `ArenaMapData.Parse` |
| `sim final` / `sim digest` | Bevægelsesreglen regner anderledes | Hastighederne i forkert rækkefølge (Sprint før Walk), retningen ikke normaliseret |
| `tour final` / `tour digest` | Collision mod væggene regner anderledes | Væggene gennemløbt i en anden rækkefølge end filens |

Selvtjekket sigter kun i "pæne" retninger (akserne og diagonalerne), og dér giver en udregning i `double` tilfældigvis de samme bits som float-kæden. Derfor er der en test mere, `OddAimWalk_EqualsTheAnswerKey`: 6000 ticks mod skæve sigtepunkter (en fast talfølge, ingen trigonometri) og et digest, der skal være facits `0x78A74F3D`. Den fanger et skridt regnet i `double`, en normalisering i `double` og en retning fra `Atan2`/`Cos`/`Sin`.

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| `Input_Is22Bytes…` giver en anden dump | Felterne i en anden rækkefølge, fx `InputIndex` før `PlayerId` |
| `Snapshot_WithoutPlayers_Is15Bytes` giver 14 bytes | Antallet af entiteter (`entityCount`) er ikke skrevet |
| `OneSecondForward…` med `Walk` og `Sprint` giver 9 m | `Sprint` tjekket før `Walk`. Walk vinder |
| `SprintBackwards_IsARun` giver 9 m | Sprint gælder kun, når bevægelsen er fremad (W uden S) |
| `Diagonal_IsNotFaster` giver 8,49 m | Retningen er ikke normaliseret |
| `Walls_TieBreak…` fejler | Tie-break i en anden rækkefølge end vest, øst, syd, nord |
| `Dash_Covers10Metres…` fejler på cooldown | Cooldown tælles ned det forkerte sted – den tælles først i `Step`, fra dash'ets START |
| Kun `OddAimWalk…` fejler | Et udtryk regnet i `double`, eller trigonometri i reglen. Cast til `(float)` efter hver operation |

**Testene kører på .NET.** En manglende `(float)`-cast giver ofte samme bits på .NET, men ikke i Unity (Mono regner mellemresultater i højere præcision). Derfor skal selvtjekket også køre i Unity: menuen giver de samme 161 linjer i Console, hvis jeres regel er bitperfekt dér.

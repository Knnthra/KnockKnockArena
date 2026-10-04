# Modul 8 – test af skud-koden, begge sider deler

Automatiske tests af `Raycast`, `ShotsFiredPacket`, våbentabellen (`WeaponStats.ParseTable`) og spread-modellen (`SpreadModel`). De kører mod **jeres egen** Shared-kode.

Serveren afgør hvert skud med strålen og våbentabellen; klienten spejler de samme tal i sigtekorset og tegner andres skud ud fra `ShotsFired`. Læser de to sider tabellen forskelligt, eller skriver de pakken forskelligt, passer det, I ser, ikke til det, serveren gjorde.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M08` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M08
   ```

3. Målet er **24 grønne tests**: `Passed! - Failed: 0, Passed: 24`.

Testprojektet har sin egen kopi af facits `weapons.json`.

## Hvad testene forventer

```csharp
namespace KnockKnockArena.Shared.Simulation
public static class Raycast
{
    public static bool RayIntersectsAabb(float originX, float originZ, float directionX, float directionZ,
        float minX, float minZ, float maxX, float maxZ, out float distance);
    public static float ArenaHitDistance(float originX, float originZ, float directionX, float directionZ);
}
public readonly struct WeaponStats { ...; static WeaponStats[] ParseTable(string json); static void Load(WeaponStats[]); static WeaponStats For(WeaponId); }
public static class SpreadModel { CurrentSpreadDeg(weapon, bloom); OnShot(weapon, bloom); Decay(weapon, bloom, deltaTime); }

namespace KnockKnockArena.Shared.Protocol.Udp
public struct ShotRecord { byte ShooterId; WeaponId Weapon; float EndX, EndZ; bool HitPlayer; }
public sealed class ShotsFiredPacket { uint ServerTick; List<ShotRecord> Shots; ToDatagram(); static FromDatagram(byte[]); }
// [type 101] [version] [serverTick:uint32] [count:byte] then per shot [shooter:byte] [weapon:byte] [endX:float] [endZ:float] [hit:byte] = 11 B
```

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| `ABoxBehindTheShooter_IsNotHit` og `FromInsideTheBox…` fejler | Strålen går begge veje: `tMin` skal starte i 0, så kasser bag skytten ikke rammes |
| `FromInsideTheBox…` giver den fjerne side | Afstanden er `tMin`, ikke `tMax` |
| Kun `ABoxBehindTheShooter_IsNotHit` fejler | Tjekket `tMin > tMax` mangler på den ene akse |
| `OneShot_Is7Plus11Bytes` har `EndZ` før `EndX` | Felterne i en anden rækkefølge end protokollens |
| `AnUnknownWeapon_IsRejected` fejler | Våben-byten tjekkes ikke – et id over 3 er ikke et våben |
| `TheGlock_IsReadFromTheFile` giver 0 for spread | Et valgfrit felt læses med et forkert navn og falder tilbage til 0 |
| `Bloom_IsCappedAtTheMaximumSpread` giver 4,0 | Loftet er `maxSpreadDeg − baseSpreadDeg` for bloom, ikke `maxSpreadDeg` |
| `Bloom_RecoversOverTime…` giver et negativt tal | Bloom må ikke gå under 0 |

# Modul 9 – test af jeres input-regel

Testene kører **jeres egen** `GameWorld` ét `Tick` ad gangen på en lille bane uden vægge. Der er intet netværk og ingen timing. De prøver den kode, I selv har skrevet: input-reglen fra modul 6 og 7 og reglen om den døde spillers inputs fra implementering 3 i modul 9.

## Sådan bruger I dem

1. Pak mappen `KnockKnockArena.Tests.M09` ud i `tests/`, så den ligger ved siden af `src/`.
2. Tilføj testprojektet til solution'en, og kør testene fra repoets rod:

   ```
   dotnet sln add tests/KnockKnockArena.Tests.M09
   dotnet test tests/KnockKnockArena.Tests.M09
   ```

3. Målet er **8 grønne tests**: `Passed! - Failed: 0, Passed: 8`.

## Hvad testene prøver

- En klient, der sender 60 inputs i sekundet, flytter sig 6 m på 30 ticks og ikke 12.
- Inputs bruges i indeks-rækkefølge.
- Der ventes op til 3 ticks på et manglende input.
- Gamle inputs og dubletter droppes (`stale input N`).
- Queuen holder 8 inputs (`input queue full`).
- Ticks uden input gemmer højst 4 pladser.
- En død spillers inputs bliver stadig kvitteret.

Testprojektet refererer både `src/KnockKnockArena.Shared` og `src/KnockKnockArena.Server`, fordi input-reglen kun findes på serveren.

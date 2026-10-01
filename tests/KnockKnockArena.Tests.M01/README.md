# Modul 1 – test af jeres WeaponBits

Automatiske tests af våben-bitfeltet. De kører mod **jeres egen** implementering.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M01` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`:

   ```
   jeres-repo/
     src/KnockKnockArena.Shared/KnockKnockArena.Shared.csproj
     tests/KnockKnockArena.Tests.M01/     <- denne mappe
   ```

2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M01
   ```

   (Vil I have dem i solution'en: `dotnet sln add tests/KnockKnockArena.Tests.M01`.)

3. Målet er **51 grønne tests**: `Passed! - Failed: 0, Passed: 51`.

## Hvad testene forventer

Testene kalder jeres kode præcis sådan her – navne og typer skal passe:

```csharp
namespace KnockKnockArena.Shared.Protocol.Udp

public enum WeaponId : byte { Punch = 0, Glock = 1, Ak47 = 2, RocketLauncher = 3 }

public static class WeaponBits
{
    public const byte StartingWeapons;                          // Punch + Glock = 3
    public static bool HasWeapon(byte owned, WeaponId weapon);  // er bit N sat?
    public static byte AddWeapon(byte owned, WeaponId weapon);  // sæt bit N
    public static byte RemoveWeapon(byte owned, WeaponId weapon); // ryd bit N
    public static string ToBinaryString(byte owned);            // "00000011"
}
```

Kompilerer testprojektet ikke, er det næsten altid et navn eller en signatur, der ikke passer.

## Når en test fejler

Mange tests kører over **alle 256 byte-værdier**, så en hjælper, der kun virker for de værdier, I selv prøvede, bliver fanget. Fejlmeddelelsen viser byten binært, fx:

```
RemoveWeapon(00000011, Ak47) gave 00000111, expected 00000011
```

Typiske fejl, testene fanger:

| Symptom | Årsag |
|---|---|
| `RemoveWeapon_OfAWeaponNotOwned_ChangesNothing` fejler | I bruger XOR (`^`), som *skifter* bitten – så "fjern" tilføjer et våben, man ikke havde. Brug AND med den inverterede maske: `owned & ~(1 << w)` |
| `HasWeapon_...` fejler for alt andet end Punch | I sammenligner med `== 1`. `owned & (1 << w)` er 2, 4 eller 8 for de andre våben – sammenlign med `!= 0` |
| `ToBinaryString_...` fejler for små tal | `Convert.ToString(3, 2)` giver `"11"`. Fyld op til otte cifre: `.PadLeft(8, '0')` |
| `AddWeapon_LeavesEveryOtherBitAlone...` fejler | I *sætter* byten i stedet for at OR'e bitten ind, så de andre våben forsvinder |

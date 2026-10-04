# Modul 3 – test af jeres AddressResolver

Automatiske tests af DNS-opløseren: det, spilleren skriver i Host-feltet, lavet om til den IPv4-adresse, en socket kan bruge. De kører mod **jeres egen** implementering.

Ingen test afhænger af internettet: `localhost` besvares af maskinen selv, IP-adresser går aldrig til DNS, og `.invalid` er et navn, DNS garanteret aldrig kender.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M03` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`:

   ```
   jeres-repo/
     src/KnockKnockArena.Shared/KnockKnockArena.Shared.csproj
     tests/KnockKnockArena.Tests.M01/
     tests/KnockKnockArena.Tests.M02/
     tests/KnockKnockArena.Tests.M03/     <- denne mappe
   ```

2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M03
   ```

3. Målet er **22 grønne tests**: `Passed! - Failed: 0, Passed: 22`.

## Hvad testene forventer

```csharp
namespace KnockKnockArena.Shared.Protocol

public static class AddressResolver
{
    public static IPAddress Resolve(string host);   // kaster ProtocolException(error)
    public static bool TryResolve(string host, out IPAddress address, out string error);
}
```

Fejlbeskederne testes ikke ordret; testene tjekker kun, at `error` ikke er tom, når opløsningen fejler. Teksterne her er dem, facit viser spilleren:

| Input | `error` |
|---|---|
| `""`, `"   "`, `null` | `no host given` |
| `"::1"` | `'::1' is an IPv6 address; the server listens on IPv4` |
| `"no-such-host.invalid"` | `unknown host 'no-such-host.invalid' (DNS has no address for it)` (uden net: `could not resolve 'no-such-host.invalid': …`) |

Lykkes opløsningen, er `error` tom (`""`). Fejler den, er `address` `IPAddress.None`.

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| `Localhost_GivesTheIPv4Loopback` giver `::1` | I tager den første adresse, DNS giver. For `localhost` er det ofte `::1` – serveren lytter på IPv4. Tag den første **IPv4**-adresse |
| `SpacesAroundTheHost_AreIgnored` fejler | Input trimmes ikke. En spiller, der kopierer `localhost ` med et mellemrum, skal stadig kunne forbinde |
| `NoHost_IsRefused` fejler for `""` med en adresse | `Dns.GetHostAddresses("")` giver maskinens egne adresser – tjek for tom tekst før DNS |
| `NoHost_IsRefused` fejler for `null` med `NullReferenceException` | `host.Trim()` på `null`. Brug `(host ?? "").Trim()` |
| `AnIPv6Literal_IsRefused...` fejler | IPv6-adressen godtages, eller den sendes til DNS i stedet for at blive afvist med beskeden ovenfor |
| `TryResolve_DoesNotThrow_ForAnUnknownName` fejler | `SocketException` fra DNS fanges ikke. `TryResolve` må aldrig kaste |
| `Resolve_Throws...` fejler | `Resolve` kaster en almindelig `Exception` – den skal kaste `ProtocolException` med samme tekst |

# Modul 11 – test af konti

Automatiske tests af jeres kontoer: hvad `accounts.json` gemmer (et PBKDF2-hash og et salt, aldrig passwordet), reglerne for en ny konto, login-tjekket og scoren på kontoen – og det hele udefra: `POST /api/accounts` med statuskoderne 201, 400 og 409, leaderboardet og et rigtigt login over TCP med `LoginRequest` fra Modul 4.

`AccountStoreTests` bruger jeres `AccountStore` direkte, hver test i sin egen tomme mappe. `AccountsOverTheWireTests` starter **jeres egen** server som en proces på to ledige porte med sin egen tomme datamappe og taler HTTP og TCP til den.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M11` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M11
   ```

3. Målet er **30 grønne tests**: `Passed! - Failed: 0, Passed: 30`.

Serveren startes med `--port`, `--http-port`, `--map`, `--weapons` og `--data-dir` – uden `--dev-accounts`. Testene rører aldrig jeres egen `data/accounts.json`.

## Hvad testene forventer

```csharp
namespace KnockKnockArena.Server.Auth
public sealed class AccountStore
{
    AccountStore(string dataDir, bool devAccounts = false);         // dataDir/accounts.json
    bool TryCreate(string username, string password, out string error);
    bool Validate(string username, string password, out string accountUsername);
    void AccumulateStats(string username, int frags, int deaths);
    List<LeaderboardEntry> GetLeaderboard(int max);                 // sorteret efter K/D, så frags
}
public sealed record LeaderboardEntry(string Username, long Frags, long Deaths, double Kd);
```

- `accounts.json`: en JSON-liste med `Username`, `PasswordHashBase64`, `SaltBase64`, `TotalFrags`, `TotalDeaths`
- Hash: `Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32)`; salt: 16 tilfældige bytes pr. konto
- En ny `accounts.json` er tom (`[]`); kun `devAccounts: true` (`--dev-accounts`) giver `alice`/`password1`, `bob`/`password2` og `test`/`test`
- Et ukendt navn koster samme tid i `Validate` som et forkert password (PBKDF2 mod et dummy-hash)
- Brugernavn 3–16 tegn (bogstaver, cifre, `_`), uden forskel på store og små bogstaver; password 4–64 tegn
- Fejltekster: `username must be 3-16 characters: letters, digits, underscore`, `password must be 4-64 characters`, `username already taken`
- `POST /api/accounts` → 201 med `Location: /api/accounts/{navn}` og `{"username"}`, 409 og 400 med `{"error"}`; `GET /api/accounts` → 405; `GET /api/leaderboard` → 200 med `[{"username","frags","deaths","kd"}]`
- TCP-login: forkert password og ukendt navn giver begge `invalid credentials`; samme konto to gange giver `already logged in`; et login som `CAROL` giver sessionen kontoens stavemåde: den næste spiller, der logger ind, får `PlayerJoined` (roster replay fra Modul 4) med navnet `carol`

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| `TheStoredHash_IsPbkdf2Sha256…` fejler | Andre parametre end 100.000 iterationer, SHA-256 og 32 bytes – eller hashet er ikke over passwordet og saltet |
| `TheSamePassword_GetsADifferentSaltAndHash` fejler | Saltet er fast (eller mangler): samme password giver samme hash |
| `TheFile_NeverHoldsThePassword` fejler | Passwordet gemmes i klartekst ved siden af hashet |
| `Validate_AWrongPasswordOrAnUnknownName_Fails` fejler | Tjekket sammenligner kun brugernavnet, eller et tomt password slipper igennem |
| `ATakenName_IsRefused_WhateverTheCase` fejler | Navnet sammenlignes med forskel på store og små bogstaver |
| `AnExistingName_Is409` giver 400 | Fejlen "username already taken" mappes ikke til 409 |
| `Login_WithAWrongPasswordOrUnknownName_IsInvalidCredentials` fejler med "test" | Login tjekker stadig det faste password fra Modul 4 |
| `TheScore_IsAddedAndKept_AcrossARestart` fejler | Scoren gemmes ikke i filen – den forsvinder ved genstart |
| `TheLeaderboard_IsSortedByKd` fejler | Leaderboardet sorteres efter frags i stedet for K/D |
| `Validate_AnUnknownName_TakesAsLongAsAWrongPassword` fejler | `Validate` returnerer med det samme, når navnet ikke findes – svartiden røber, hvilke navne der findes |
| `ANewStore_HasNoAccounts_UnlessDevAccountsAreAskedFor` fejler | En ny fil får udviklingskontiene uden `--dev-accounts` (eller aldrig med) |
| `Login_WithTheAccountsPassword_Succeeds_InTheAccountsOwnSpelling` fejler, men login lykkes | Sessionen får navnet, som det blev skrevet (`CAROLLOGIN`), ikke kontoens (`carollogin`) – brug `accountUsername` fra `Validate` |

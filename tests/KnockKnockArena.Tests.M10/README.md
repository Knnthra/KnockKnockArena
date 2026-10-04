# Modul 10 – test af REST-API'en og server browserens valg

Automatiske tests af jeres REST-API udefra og af den delte `ServerStatus`. Testene starter **jeres egen** server som en rigtig proces (den, der bygges ved siden af testene) på to ledige porte, med facits `arena.json` og `weapons.json`, og taler HTTP til den – som en browser eller server browseren gør.

Config-opslaget er hele pointen med modulet: klienten parser præcis de bytes, serveren parsede, og tjekker hashen. Serverer jeres `/api/config` noget andet end filen – trimmet, omformateret, en anden fil – eller en forkert hash, fanger testene det, før en klient gør.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M10` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M10
   ```

3. Målet er **29 grønne tests**: `Passed! - Failed: 0, Passed: 29`.

Serveren startes med `--port`, `--http-port`, `--map` og `--weapons`, så jeres `ServerConfig` skal kende de fire argumenter. Den får sin egen tomme arbejdsmappe og lukkes med `quit` bagefter.

## Hvad testene forventer

```
GET /api/status          200, application/json: serverName, protocolVersion, playersOnline, maxPlayers,
                         gamePort, httpPort, mapName (+ det I ellers sender) - ServerStatus.Parse kan læse det
GET /api/config          200, application/json: protocolVersion, tickRate, mapName,
                         mapHash = ArenaMapData.FormatHash(hash af banen i svaret),
                         arena = arena.json's tekst byte for byte, weapons = weapons.json's tekst byte for byte
GET /api/config/arena    200, application/json: arena.json's tekst
GET /api/config/weapons  weapons.json's tekst
GET /api/nope, GET /     404
POST /api/status         405 med Allow: GET
DELETE /api/config       405
```

```csharp
namespace KnockKnockArena.Shared.Protocol
public sealed class ServerStatus
{
    string ServerName; int GamePort, HttpPort, ProtocolVersion, PlayersOnline, MaxPlayers; string MapName;
    bool Compatible { get; }                                   // samme protokolversion
    static ServerStatus Parse(string json);                    // et manglende felt er en JsonException
    static int PickQuickJoin(IReadOnlyList<ServerStatus?> s);  // fyldteste kompatible med plads, ellers -1
    static bool TryParseEntry(string entry, out string host, out int httpPort);   // uden port: 8080
}
```

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| Alle `RestApiTests` fejler: "no REST answer on port …" | Serveren starter ikke med `--http-port`, eller Kestrel lytter ikke på den port |
| `Config_CarriesTheArenaFileByteForByte` fejler | `arena` er ikke filens rå tekst – trimmet, omformateret eller serialiseret igen |
| `Config_MapHash_IsTheHashOfTheArenaItCarries` fejler | `mapHash` er formateret anderledes end `ArenaMapData.FormatHash` (fx uden `0x`) |
| `ConfigArena_IsTheRawFile` fejler på content type | `Results.Text` uden `"application/json"` |
| `PostToStatus_Is405_AndSaysGetIsAllowed` fejler | Status er mappet til flere verber end `GET` |
| `Status_TellsWhereToJoin` fejler på `gamePort` | Status-objektet sender REST-porten som spilport |
| `QuickJoin_PicksTheFullestServerWithRoom` fejler | Quick join vælger den tomeste i stedet for den fyldteste |
| `QuickJoin_ATie_KeepsTheFirst` fejler | `>=` i stedet for `>`: ved uafgjort vinder den sidste |

# Modul 12 – test af Docker-konfigurationen

Automatiske tests af det, der gør jeres server klar til Docker: at den kan konfigureres med **miljøvariabler alene** (sådan giver `docker run -e` den besked), at et argument vinder over miljøet, og at den **annoncerer den offentlige port** bag port mapping – i `/api/status` og i LAN-svaret. Og af jeres `Dockerfile` og `.dockerignore`, læst som tekst: hvad imaget bygges af, hvilke porte det åbner, hvor kontiene bor, og hvad der aldrig må komme med.

Testene kræver **ikke** Docker. `ConfigFromEnvironmentTests` starter **jeres egen** server som en proces for hver test – med kun de miljøvariabler, testen sætter – og taler HTTP og UDP til den. `DockerfileTests` læser `Dockerfile` og `.dockerignore` i repo-roden.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M12` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M12
   ```

3. Målet er **21 grønne tests**: `Passed! - Failed: 0, Passed: 21`.

## Hvad testene forventer

- Miljøvariablerne `KKARENA_PORT`, `KKARENA_HTTP_PORT`, `KKARENA_NAME`, `KKARENA_DATA_DIR`, `KKARENA_MAP`, `KKARENA_WEAPONS`, `KKARENA_PUBLIC_PORT` og `KKARENA_PUBLIC_HTTP_PORT`; kommandolinjen (`--name` osv.) vinder over dem
- `/api/status` med `serverName`, `gamePort`, `httpPort` og `mapName` – `gamePort`/`httpPort` er de offentlige porte, hvis de er sat, ellers de bundne
- Loglinjen `advertising port 36395 and httpPort 8095 …`, når de offentlige porte er sat
- LAN-svaret (M05's `DiscoveryResponsePacket`) med de samme porte som `/api/status`
- En relativ `KKARENA_MAP` findes i datamappen (`KKARENA_DATA_DIR`)
- `Dockerfile` i repo-roden: to stages (`dotnet/sdk` først, `dotnet/aspnet` sidst), `EXPOSE` for `36363/tcp`, `36363/udp` og `8080/tcp`, `ENV … KKARENA_DATA_DIR=/data` og `VOLUME /data`, og ingen `COPY` af `accounts.json` eller hele `data/`
- Nedlukning: `quit` (samme kode som SIGTERM og Ctrl+C) afbryder de aktive spillere – loggen skriver `Shutting down` og `Player left: <navn>#<id> (server shutting down)` – og processen er ude inden 10 sekunder
- `.dockerignore`: holder `data/accounts.json`, `KnockKnockClient/Library`, `bin/`-mapper og `.git/` ude – og lukker banen, våbnene, den delte kode og serverens kode ind

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| `TheEnvironment_AloneConfiguresTheServer` fejler | En miljøvariabel læses ikke (eller med et forkert navn) – eller `accounts.json` havner ikke i `KKARENA_DATA_DIR` |
| `AnArgument_WinsOverTheEnvironment` fejler | Miljøvariablerne læses **efter** kommandolinjen og overskriver den |
| `ThePublicPorts_AreAdvertised_InTheStatus` fejler | `/api/status` sender den bundne port i stedet for `AdvertisedPort` |
| `ThePublicPort_IsAdvertised_InTheLanAnswer` fejler | LAN-svaret i `UdpGameServer` sender stadig `_config.Port` |
| `WithoutPublicPorts_TheBoundPortsAreAdvertised` fejler | `AdvertisedPort` giver 0, når der ingen offentlig port er |
| `ARelativeMapPath_IsFoundInTheDataDir` fejler | En relativ banesti findes i arbejdsmappen i stedet for datamappen |
| `ThePorts_AreExposed("36363/udp")` fejler | `EXPOSE 36363` uden `/udp` – det er kun TCP |
| `TheImage_IsBuiltWithTheSdk_AndRunsOnTheAspNetRuntime` fejler | Én stage med SDK'en (et 1,3 GB-image), eller `dotnet/runtime` uden ASP.NET (Kestrel mangler) |
| `TheAccounts_LiveInAVolume_OutsideTheImage` fejler | Ingen volume: kontiene forsvinder med containeren |
| `Quit_DisconnectsTheActivePlayers_AndEndsWithinDockersTenSeconds` fejler | Nedlukningen afbryder ikke spillerne (så kommer deres score aldrig på kontoen), eller `quit` stopper ikke serveren |
| `TheDockerignore_KeepsOut("data/accounts.json")` fejler | `.dockerignore` lukker hele `data/` ind – password-hashes i build context og måske i imaget |

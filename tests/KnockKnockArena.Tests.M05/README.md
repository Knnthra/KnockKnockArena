# Modul 5 – test af jeres UDP-pakker

Automatiske tests af `UdpPacketIO`, `JoinPacket`, `HeartbeatPacket` og `DiscoveryPackets`. De kører mod **jeres egen** implementering og sammenligner byte for byte med facit.

På UDP er der ingen framing: et datagram ER pakken, `[type:byte] [version:byte] [felter]`. Serveren og alle andre klienter læser felterne blindt på deres pladser, så én forkert byte er en pakke, ingen forstår.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M05` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M05
   ```

3. Målet er **41 grønne tests**: `Passed! - Failed: 0, Passed: 41`.

## Hvad testene forventer

```csharp
namespace KnockKnockArena.Shared.Protocol.Udp

public enum UdpPacketType : byte { Join = 0, Heartbeat = 2, DiscoveryRequest = 3, DiscoveryResponse = 102 }

public static class UdpPacketIO
{
    public static UdpPacketType PeekType(byte[] datagram);                         // "datagram too short (N B)"
    public static byte[] Build(UdpPacketType type, Action<BinaryWriter> writeFields);
    public static T Parse<T>(byte[] datagram, Func<BinaryReader, T> readFields);    // version tjekket, "malformed payload"
}

public sealed class JoinPacket { public byte[] SessionToken; ToDatagram(); static FromDatagram(byte[]); }
public sealed class HeartbeatPacket { public byte PlayerId; ToDatagram(); static FromDatagram(byte[]); }
public sealed class DiscoveryRequestPacket { public const int Size = 6; public uint Nonce; public byte ClientVersion; ... FromDatagramAnyVersion(byte[]); }
public sealed class DiscoveryResponsePacket { Nonce, GamePort, HttpPort, PlayersOnline, MaxPlayers, ServerName, MapName; ... PeekVersion(byte[]); }
```

Fejlbeskederne testes ikke; facit bruger `datagram too short (N B)`, `malformed payload` og `bad version N`.

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| Næsten alt fejler, dumpene starter med `06-…` | Versionen er skrevet før typen. Typen står først – modtageren dispatcher på den |
| `Join_Truncated_IsMalformed` fejler med `bad token` eller intet | `ReadBytes(16)` kaster ikke, når datagrammet er for kort – den returnerer færre bytes. Tjek længden, og kast `ProtocolException("malformed payload")` |
| `..._FromAnotherVersion_IsRejected` fejler | `Parse` læser versionsbyten uden at tjekke den |
| `DiscoveryRequest_FromAnotherVersion_IsStillRead` fejler | Discovery-requesten tjekker versionen. Den skal besvares fra alle versioner, så en gammel klient ser serveren som inkompatibel i stedet for slet ikke |
| `Heartbeat_Is3Bytes…` giver 4 bytes | `PlayerId` er skrevet som `ushort`. Den er en `byte` |
| `DiscoveryResponse_IsTheAnswerKeysBytes` fejler midt i | To felter i forkert rækkefølge, fx `HttpPort` før `GamePort` |

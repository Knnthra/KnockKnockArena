# Modul 4 – test af jeres TCP-framing

Automatiske tests af `TcpFraming` og de syv beskeder fra Modul 4. De kører mod **jeres egen** implementering.

TCP leverer en **strøm** af bytes, ikke beskeder: ét `Read` kan give en halv header, eller slutningen af én besked og starten på den næste. På `localhost` sker det næsten aldrig, så jeres kode kan virke hjemme og fejle over et rigtigt net. Testene gør det med vilje: de giver jeres `TcpFraming` en strøm, der udleverer **én byte pr. `Read`**, og strømme, der skærer beskederne over tilfældige steder.

## Sådan bruger I dem

1. Kopiér mappen `KnockKnockArena.Tests.M04` ind i jeres repo, så den ligger i `tests/` ved siden af `src/`.
2. Kør testene fra repo-roden:

   ```
   dotnet test tests/KnockKnockArena.Tests.M04
   ```

3. Målet er **58 grønne tests**: `Passed! - Failed: 0, Passed: 58`.

## Hvad testene forventer

```csharp
namespace KnockKnockArena.Shared.Protocol

public static class TcpFraming
{
    public static void WriteMessage(Stream stream, MessageType type, byte[] payload);
    public static bool TryReadMessage(Stream stream, out MessageType type, out byte[] payload); // false = pænt lukket
}

public enum MessageType : byte
{
    LoginRequest = 0, ChatSend = 1,
    LoginResponse = 100, ServerInfo = 101, ChatBroadcast = 102, PlayerJoined = 103, PlayerLeft = 104,
}
```

Og beskederne i `KnockKnockArena.Shared.Protocol.Messages` med `ToPayload()` og `FromPayload(byte[])`, samt `ProtocolConstants.MaxPayloadLength = 16 * 1024` og `SessionTokenLength = 16`.

Fejlbeskederne testes ikke; facit bruger `connection closed mid-message` og `invalid length N`. Begge fejl skal have `IsFatal = true`, fordi strømmen ikke kan stoles på bagefter, og det tester testene.

## Når en test fejler

Typiske fejl, testene fanger (alle er prøvet af mod testene):

| Symptom | Årsag |
|---|---|
| Næsten alle `TryReadMessage_...` fejler (44 tests) | Ét `stream.Read` antages at give alle de bytes, I bad om. Det gør det ikke: læs i en løkke, til bufferen er fuld |
| `WriteMessage_LoginTestTest_IsTheWikiDump` giver `0E-00-00-00-…` | Længden tæller typebyten med. Den tæller kun payloaden |
| `invalid length` med et kæmpe tal for en helt normal besked | Længden læses big-endian. Den er little-endian som alt andet |
| `TryReadMessage_InvalidLength_IsFatal` fejler | Længden tjekkes ikke, eller fejlen er ikke fatal. En forkert længde betyder, at I ikke ved, hvor næste besked starter – forbindelsen skal lukkes |
| `TryReadMessage_ReturnsFalse_WhenTheStreamEndsBetweenMessages` fejler | En pæn lukning mellem to beskeder behandles som en fejl. Den skal give `false` |
| `WriteMessage_LeavesTheStreamOpen` fejler | `using` om `BinaryWriter` lukker strømmen – på en `NetworkStream` er det forbindelsen |

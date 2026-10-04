using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol;

namespace KnockKnockArena.Tests.M10;

/// <summary>
/// Module 10: what the server browser does with an answer from GET /api/status - parse it
/// with the shared parser, split "host:httpPort" entries from its list, and Quick join's
/// choice: the fullest compatible server that still has room.
/// </summary>
public class ServerStatusTests
{
    private const string Answer =
        "{\"serverName\":\"Arena A\",\"version\":\"0.6.0\",\"protocolVersion\":6,\"uptimeSeconds\":12," +
        "\"playersOnline\":3,\"playersInGame\":3,\"maxPlayers\":12,\"gamePort\":36363,\"httpPort\":8080," +
        "\"tickRate\":30,\"mapName\":\"Main\",\"mapHash\":\"0x98A578E2\"}";

    private static ServerStatus Server(int online, int max = 12, int protocol = ProtocolConstants.Version) =>
        new() { ServerName = $"s{online}", PlayersOnline = online, MaxPlayers = max, ProtocolVersion = protocol };

    [Fact]
    public void Parse_ReadsTheFieldsTheBrowserShows()
    {
        ServerStatus status = ServerStatus.Parse(Answer);

        Assert.Equal("Arena A", status.ServerName);
        Assert.Equal(36363, status.GamePort);
        Assert.Equal(8080, status.HttpPort);
        Assert.Equal(3, status.PlayersOnline);
        Assert.Equal(12, status.MaxPlayers);
        Assert.Equal("Main", status.MapName);
        Assert.True(status.Compatible);
    }

    [Fact]
    public void Parse_AMissingField_IsRejected()
    {
        Assert.Throws<JsonException>(() => ServerStatus.Parse(Answer.Replace("\"gamePort\":36363,", "")));
    }

    [Fact]
    public void AnotherProtocolVersion_IsNotCompatible()
    {
        Assert.False(ServerStatus.Parse(Answer.Replace("\"protocolVersion\":6", "\"protocolVersion\":5")).Compatible);
    }

    [Theory]
    [InlineData("localhost:8081", "localhost", 8081)]
    [InlineData("  10.0.0.7:9000 ", "10.0.0.7", 9000)]
    [InlineData("arena.example", "arena.example", 8080)]
    public void TryParseEntry_SplitsHostAndHttpPort(string entry, string host, int port)
    {
        Assert.True(ServerStatus.TryParseEntry(entry, out string parsedHost, out int parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":8080")]
    [InlineData("localhost:0")]
    [InlineData("localhost:70000")]
    [InlineData("localhost:http")]
    public void TryParseEntry_RejectsWhatIsNotHostAndPort(string entry)
    {
        Assert.False(ServerStatus.TryParseEntry(entry, out _, out _));
    }

    [Fact]
    public void QuickJoin_PicksTheFullestServerWithRoom()
    {
        Assert.Equal(1, ServerStatus.PickQuickJoin(new ServerStatus?[] { Server(2), Server(7), Server(4) }));
    }

    [Fact]
    public void QuickJoin_SkipsFullAndIncompatibleServers_AndNoAnswer()
    {
        ServerStatus?[] servers = { Server(12), Server(9, protocol: 5), null, Server(1) };

        Assert.Equal(3, ServerStatus.PickQuickJoin(servers));
    }

    [Fact]
    public void QuickJoin_ATie_KeepsTheFirst()
    {
        Assert.Equal(0, ServerStatus.PickQuickJoin(new ServerStatus?[] { Server(5), Server(5) }));
    }

    [Fact]
    public void QuickJoin_NothingFits_IsMinusOne()
    {
        Assert.Equal(-1, ServerStatus.PickQuickJoin(new ServerStatus?[] { Server(12), null }));
    }
}

using System.Net;
using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Tests.M10;

/// <summary>
/// Module 10: the REST API seen from the outside. Status codes (200, 404, 405), the content
/// type, and what GET /api/status and GET /api/config carry - the config must be the arena
/// and the weapon table the server runs on, byte for byte, with the hash the client checks.
/// </summary>
public class RestApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _server;

    public RestApiTests(ServerFixture server) => _server = server;

    private HttpResponseMessage Send(HttpMethod method, string path) =>
        _server.Http.SendAsync(new HttpRequestMessage(method, path)).GetAwaiter().GetResult();

    private string Body(HttpResponseMessage response) => response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    [Fact]
    public void Status_Is200Json()
    {
        HttpResponseMessage response = Send(HttpMethod.Get, "/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Status_TellsWhereToJoin()
    {
        JsonValue status = Json.Parse(Body(Send(HttpMethod.Get, "/api/status")));

        Assert.Equal(_server.GamePort, status["gamePort"].AsInt());
        Assert.Equal(_server.HttpPort, status["httpPort"].AsInt());
        Assert.Equal(ProtocolConstants.Version, status["protocolVersion"].AsInt());
        Assert.Equal(0, status["playersOnline"].AsInt());
        Assert.True(status["maxPlayers"].AsInt() > 0);
        Assert.Equal("Main", status["mapName"].AsString());
    }

    [Fact]
    public void Status_ParsesWithTheSharedServerStatus()
    {
        ServerStatus status = ServerStatus.Parse(Body(Send(HttpMethod.Get, "/api/status")));

        Assert.True(status.Compatible);
        Assert.Equal(_server.GamePort, status.GamePort);
    }

    [Fact]
    public void Config_Is200Json()
    {
        HttpResponseMessage response = Send(HttpMethod.Get, "/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Config_CarriesTheArenaFileByteForByte()
    {
        JsonValue config = Json.Parse(Body(Send(HttpMethod.Get, "/api/config")));

        Assert.Equal(File.ReadAllText(_server.ArenaPath), config["arena"].AsString());
    }

    [Fact]
    public void Config_CarriesTheWeaponTableByteForByte()
    {
        JsonValue config = Json.Parse(Body(Send(HttpMethod.Get, "/api/config")));

        Assert.Equal(File.ReadAllText(_server.WeaponsPath), config["weapons"].AsString());
        Assert.Equal(4, WeaponStats.ParseTable(config["weapons"].AsString()).Length);
    }

    [Fact]
    public void Config_MapHash_IsTheHashOfTheArenaItCarries()
    {
        JsonValue config = Json.Parse(Body(Send(HttpMethod.Get, "/api/config")));
        ArenaMapData served = ArenaMapData.Parse(config["arena"].AsString());

        Assert.Equal(ArenaMapData.FormatHash(served.Hash), config["mapHash"].AsString());
        Assert.Equal("Main", config["mapName"].AsString());
        Assert.Equal(ProtocolConstants.Version, config["protocolVersion"].AsInt());
        Assert.Equal(GameConstants.TickRate, config["tickRate"].AsInt());
    }

    [Fact]
    public void ConfigArena_IsTheRawFile()
    {
        HttpResponseMessage response = Send(HttpMethod.Get, "/api/config/arena");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(File.ReadAllText(_server.ArenaPath), Body(response));
    }

    [Fact]
    public void ConfigWeapons_IsTheRawFile()
    {
        Assert.Equal(File.ReadAllText(_server.WeaponsPath), Body(Send(HttpMethod.Get, "/api/config/weapons")));
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/")]
    [InlineData("/api/status/extra")]
    public void AnUnknownPath_Is404(string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, Send(HttpMethod.Get, path).StatusCode);
    }

    [Fact]
    public void PostToStatus_Is405_AndSaysGetIsAllowed()
    {
        HttpResponseMessage response = Send(HttpMethod.Post, "/api/status");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Fact]
    public void DeleteConfig_Is405()
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, Send(HttpMethod.Delete, "/api/config").StatusCode);
    }
}

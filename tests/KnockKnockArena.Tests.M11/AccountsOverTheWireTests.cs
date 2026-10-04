using System.Net;
using System.Net.Sockets;
using System.Text;
using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;

namespace KnockKnockArena.Tests.M11;

/// <summary>
/// Module 11: accounts from the outside - POST /api/accounts with its status codes, the
/// leaderboard, and a real login over TCP against the account (the LoginRequest from M04).
/// The server runs as a process with its own empty data directory.
/// </summary>
public class AccountsOverTheWireTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _server;

    public AccountsOverTheWireTests(ServerFixture server) => _server = server;

    private HttpResponseMessage Post(string json) => _server.Http
        .PostAsync("/api/accounts", new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

    private HttpResponseMessage Get(string path) => _server.Http.GetAsync(path).GetAwaiter().GetResult();

    private static string Body(HttpResponseMessage response) => response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    private static string Account(string username, string password) =>
        "{\"username\":\"" + username + "\",\"password\":\"" + password + "\"}";

    private LoginResponse Login(string username, string password, TcpClient? keepOpen = null)
    {
        TcpClient tcp = keepOpen ?? new TcpClient();
        if (!tcp.Connected)
            tcp.Connect(IPAddress.Loopback, _server.GamePort);
        NetworkStream stream = tcp.GetStream();
        stream.ReadTimeout = 5000;
        TcpFraming.WriteMessage(stream, MessageType.LoginRequest, new LoginRequest { Username = username, Password = password }.ToPayload());
        while (TcpFraming.TryReadMessage(stream, out MessageType type, out byte[] payload))
        {
            if (type == MessageType.LoginResponse)
            {
                LoginResponse response = LoginResponse.FromPayload(payload);
                if (keepOpen == null)
                    tcp.Dispose();
                return response;
            }
        }
        throw new InvalidOperationException("the server closed the connection without a LoginResponse");
    }

    [Fact]
    public void ANewAccount_Is201_WithLocation()
    {
        HttpResponseMessage response = Post(Account("carol201", "hemmelig1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/accounts/carol201", response.Headers.Location?.OriginalString);
        Assert.Equal("carol201", Json.Parse(Body(response))["username"].AsString());
    }

    [Fact]
    public void AnExistingName_Is409()
    {
        Post(Account("carol409", "hemmelig1"));

        HttpResponseMessage response = Post(Account("CAROL409", "andet_pw"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(Json.Parse(Body(response))["error"].AsString()));
    }

    [Theory]
    [InlineData("c!", "hemmelig1")]
    [InlineData("carol400", "abc")]
    public void AnInvalidAccount_Is400_WithTheReason(string username, string password)
    {
        HttpResponseMessage response = Post(Account(username, password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(Json.Parse(Body(response))["error"].AsString()));
    }

    [Fact]
    public void GetOnAccounts_Is405()
    {
        HttpResponseMessage response = Get("/api/accounts");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public void TheLeaderboard_Is200_AndListsTheAccounts()
    {
        Post(Account("carolboard", "hemmelig1"));

        HttpResponseMessage response = Get("/api/leaderboard");
        string body = Body(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Json.Parse(body).Items, e => e["username"].AsString() == "carolboard" && e["frags"].AsInt() == 0);
    }

    [Fact]
    public void Login_WithTheAccountsPassword_Succeeds_InTheAccountsOwnSpelling()
    {
        Post(Account("carollogin", "hemmelig1"));
        Post(Account("carolwatch", "hemmelig1"));
        using TcpClient carol = new();

        LoginResponse response = Login("CAROLLOGIN", "hemmelig1", carol);

        Assert.True(response.Success, response.Error);
        // The session is the account's, spelled as the account is - not as it was typed. A
        // second player is told who is already online (M04's roster replay: PlayerJoined,
        // sent right after its own LoginResponse), with the session's name.
        using TcpClient watcher = new();
        Assert.True(Login("carolwatch", "hemmelig1", watcher).Success);
        Assert.Equal("carollogin", NameInRoster(watcher, response.PlayerId));
    }

    private static string NameInRoster(TcpClient client, byte playerId)
    {
        NetworkStream stream = client.GetStream();
        while (TcpFraming.TryReadMessage(stream, out MessageType type, out byte[] payload))
        {
            if (type == MessageType.PlayerJoined)
            {
                PlayerJoined joined = PlayerJoined.FromPayload(payload);
                if (joined.PlayerId == playerId)
                    return joined.Username;
            }
        }
        throw new InvalidOperationException($"the server closed the connection without a PlayerJoined for player #{playerId}");
    }

    [Theory]
    [InlineData("carolwrong", "hemmelig2")]
    [InlineData("nobody_here", "hemmelig1")]
    [InlineData("carolwrong", "test")]
    [InlineData("test", "test")] // no dev accounts on a server without --dev-accounts
    public void Login_WithAWrongPasswordOrUnknownName_IsInvalidCredentials(string username, string password)
    {
        Post(Account("carolwrong", "hemmelig1"));

        LoginResponse response = Login(username, password);

        Assert.False(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.Error));
    }

    [Fact]
    public void ASecondLogin_OfTheSameAccount_IsRefused()
    {
        Post(Account("caroltwice", "hemmelig1"));
        using TcpClient first = new();
        Assert.True(Login("caroltwice", "hemmelig1", first).Success);

        LoginResponse second = Login("caroltwice", "hemmelig1");

        Assert.False(second.Success);
        Assert.False(string.IsNullOrWhiteSpace(second.Error));
    }
}

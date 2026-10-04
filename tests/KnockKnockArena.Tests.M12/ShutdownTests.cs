using System.Net;
using System.Net.Sockets;
using System.Text;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;

namespace KnockKnockArena.Tests.M12;

/// <summary>
/// Module 12: a proper shutdown. `docker stop` sends SIGTERM and waits 10 s before SIGKILL;
/// SIGTERM, Ctrl+C and `quit` all end the server the same way - every active player is
/// disconnected through the normal path (so the session's score reaches the account), and
/// the process exits in time. The test uses `quit`: a signal cannot be sent the same way
/// on every OS, and the shutdown after it is the same code.
/// </summary>
public class ShutdownTests
{
    [Fact]
    public void Quit_DisconnectsTheActivePlayers_AndEndsWithinDockersTenSeconds()
    {
        (int port, int httpPort) = ServerProcess.FreePorts();
        using ServerProcess server = new(httpPort, new Dictionary<string, string>
        {
            ["KKARENA_PORT"] = port.ToString(),
            ["KKARENA_HTTP_PORT"] = httpPort.ToString(),
            ["KKARENA_MAP"] = ServerProcess.ArenaPath,
            ["KKARENA_WEAPONS"] = ServerProcess.WeaponsPath,
        });
        server.Http.PostAsync("/api/accounts", new StringContent("{\"username\":\"carol\",\"password\":\"hemmelig1\"}",
            Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

        using TcpClient carol = new();
        carol.Connect(IPAddress.Loopback, port);
        NetworkStream stream = carol.GetStream();
        stream.ReadTimeout = 5000;
        TcpFraming.WriteMessage(stream, MessageType.LoginRequest, new LoginRequest { Username = "carol", Password = "hemmelig1" }.ToPayload());
        LoginResponse? login = null;
        while (login == null && TcpFraming.TryReadMessage(stream, out MessageType type, out byte[] payload))
            if (type == MessageType.LoginResponse)
                login = LoginResponse.FromPayload(payload);
        Assert.True(login?.Success, login?.Error);

        Assert.True(server.Quit(10), "the server was still running 10 s after quit - docker stop would SIGKILL it");
        Assert.Contains("Shutting down", server.Log);
        Assert.Contains($"Player left: carol#{login!.PlayerId} (server shutting down)", server.Log);
    }
}

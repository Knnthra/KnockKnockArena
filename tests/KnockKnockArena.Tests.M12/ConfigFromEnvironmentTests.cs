using System.Net;
using System.Net.Sockets;
using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Tests.M12;

/// <summary>
/// Module 12: the server configured the Docker way - with environment variables only, the
/// way `docker run -e` hands them over - and arguments on top. Behind port mapping the server
/// binds one port and must ADVERTISE another: the one the client outside can reach.
/// Every test starts its own server process on free ports.
/// </summary>
public class ConfigFromEnvironmentTests
{
    private static Dictionary<string, string> Content(int port, int httpPort) => new()
    {
        ["KKARENA_PORT"] = port.ToString(),
        ["KKARENA_HTTP_PORT"] = httpPort.ToString(),
        ["KKARENA_MAP"] = ServerProcess.ArenaPath,
        ["KKARENA_WEAPONS"] = ServerProcess.WeaponsPath,
    };

    private static JsonValue Status(ServerProcess server) => Json.Parse(server.Get("/api/status"));

    [Fact]
    public void TheEnvironment_AloneConfiguresTheServer()
    {
        (int port, int httpPort) = ServerProcess.FreePorts();
        Dictionary<string, string> env = Content(port, httpPort);
        env["KKARENA_NAME"] = "Arena Env";
        string dataDir = Path.Combine(Path.GetTempPath(), "kka-tests-m12-data-" + Guid.NewGuid().ToString("N"));
        env["KKARENA_DATA_DIR"] = dataDir;
        try
        {
            using ServerProcess server = new(httpPort, env);

            JsonValue status = Status(server);
            Assert.Equal("Arena Env", status["serverName"].AsString());
            Assert.Equal(port, status["gamePort"].AsInt());
            Assert.Equal(httpPort, status["httpPort"].AsInt());
            Assert.True(File.Exists(Path.Combine(dataDir, "accounts.json")), "no accounts.json in KKARENA_DATA_DIR");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AnArgument_WinsOverTheEnvironment()
    {
        (int port, int httpPort) = ServerProcess.FreePorts();
        Dictionary<string, string> env = Content(port, httpPort);
        env["KKARENA_NAME"] = "From the environment";

        using ServerProcess server = new(httpPort, env, "--name", "From an argument");

        Assert.Equal("From an argument", Status(server)["serverName"].AsString());
    }

    [Fact]
    public void WithoutPublicPorts_TheBoundPortsAreAdvertised()
    {
        (int port, int httpPort) = ServerProcess.FreePorts();

        using ServerProcess server = new(httpPort, Content(port, httpPort));

        JsonValue status = Status(server);
        Assert.Equal(port, status["gamePort"].AsInt());
        Assert.Equal(httpPort, status["httpPort"].AsInt());
    }

    [Fact]
    public void ThePublicPorts_AreAdvertised_InTheStatus()
    {
        (int port, int httpPort) = ServerProcess.FreePorts();
        Dictionary<string, string> env = Content(port, httpPort);
        env["KKARENA_PUBLIC_PORT"] = "36395";
        env["KKARENA_PUBLIC_HTTP_PORT"] = "8095";

        using ServerProcess server = new(httpPort, env);

        JsonValue status = Status(server);
        Assert.Equal(36395, status["gamePort"].AsInt());
        Assert.Equal(8095, status["httpPort"].AsInt());
        Assert.Contains("advertising port 36395 and httpPort 8095", server.Log);
    }

    [Fact]
    public void ThePublicPort_IsAdvertised_InTheLanAnswer()
    {
        (int port, int httpPort) = ServerProcess.FreePorts();
        Dictionary<string, string> env = Content(port, httpPort);
        env["KKARENA_PUBLIC_PORT"] = "36395";
        env["KKARENA_PUBLIC_HTTP_PORT"] = "8095";
        using ServerProcess server = new(httpPort, env);

        // The LAN discovery request from M05, sent straight to the server's UDP port.
        using UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Client.ReceiveTimeout = 3000;
        byte[] request = new DiscoveryRequestPacket { Nonce = 4711 }.ToDatagram();
        udp.Send(request, request.Length, new IPEndPoint(IPAddress.Loopback, port));
        IPEndPoint from = new(IPAddress.Any, 0);
        DiscoveryResponsePacket answer = DiscoveryResponsePacket.FromDatagram(udp.Receive(ref from));

        Assert.Equal(4711u, answer.Nonce);
        Assert.Equal(36395, answer.GamePort);
        Assert.Equal(8095, answer.HttpPort);
    }

    [Fact]
    public void ARelativeMapPath_IsFoundInTheDataDir()
    {
        // In a container the data dir is the mounted volume: KKARENA_MAP=arena2.json means
        // /data/arena2.json, so a new map needs a file in the volume, not a new image.
        (int port, int httpPort) = ServerProcess.FreePorts();
        string dataDir = Path.Combine(Path.GetTempPath(), "kka-tests-m12-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        File.Copy(ServerProcess.ArenaPath, Path.Combine(dataDir, "arena2.json"));
        Dictionary<string, string> env = Content(port, httpPort);
        env["KKARENA_DATA_DIR"] = dataDir;
        env["KKARENA_MAP"] = "arena2.json";
        try
        {
            using ServerProcess server = new(httpPort, env);

            Assert.Contains("arena2.json", server.Log);
            Assert.Equal("Main", Status(server)["mapName"].AsString());
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KnockKnockArena.Tests.M11;

/// <summary>
/// Starts YOUR server as a real process - the dll built next to the tests - on free ports,
/// with the answer key's arena.json and weapons.json and its own empty data directory (so
/// accounts.json is made fresh), and stops it again after the tests.
/// The tests talk HTTP and TCP to it, like the client would.
/// </summary>
public sealed class ServerFixture : IDisposable
{
    public int GamePort { get; }
    public int HttpPort { get; }
    public string ArenaPath { get; } = Path.Combine(AppContext.BaseDirectory, "arena.json");
    public string WeaponsPath { get; } = Path.Combine(AppContext.BaseDirectory, "weapons.json");
    public HttpClient Http { get; }

    private readonly Process _server;
    private readonly string _workDir;

    public ServerFixture()
    {
        GamePort = FreePort();
        HttpPort = FreePort(except: GamePort);
        _workDir = Path.Combine(Path.GetTempPath(), "kka-tests-m11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        string serverDll = Path.Combine(AppContext.BaseDirectory, "KnockKnockArena.Server.dll");
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = _workDir,
            RedirectStandardInput = true,   // kept open: the server reads its console commands
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in new[] { serverDll, "--port", GamePort.ToString(), "--http-port", HttpPort.ToString(),
                     "--map", ArenaPath, "--weapons", WeaponsPath, "--data-dir", Path.Combine(_workDir, "data") })
            start.ArgumentList.Add(arg);
        _server = Process.Start(start) ?? throw new InvalidOperationException("could not start the server");
        _server.OutputDataReceived += (_, _) => { };
        _server.ErrorDataReceived += (_, _) => { };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        Http = new HttpClient { BaseAddress = new Uri($"http://localhost:{HttpPort}"), Timeout = TimeSpan.FromSeconds(5) };

        // Up when /api/status answers - at most 15 s. A server that never answers is stopped
        // here: the tests fail, but no server is left running behind them.
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            if (_server.HasExited)
                throw new InvalidOperationException($"the server exited with code {_server.ExitCode} (see its arguments above)");
            try
            {
                if (Http.GetAsync("/api/status").GetAwaiter().GetResult().IsSuccessStatusCode)
                    break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            if (waited.Elapsed > TimeSpan.FromSeconds(15))
            {
                Dispose();
                throw new InvalidOperationException($"no answer from GET /api/status on port {HttpPort} after 15 s");
            }
            Thread.Sleep(200);
        }
    }

    /// <summary>A port that is free for TCP AND UDP - the game port is both.</summary>
    private static int FreePort(int except = 0)
    {
        while (true)
        {
            TcpListener listener = new(IPAddress.Any, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                if (port == except || port < 1024)
                    continue;
                using UdpClient udp = new(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException)
            {
                // taken for UDP: try another
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _server.StandardInput.WriteLine("quit");
            if (!_server.WaitForExit(3000))
                _server.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        Http.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }
}

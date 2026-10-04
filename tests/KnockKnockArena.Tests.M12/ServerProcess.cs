using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KnockKnockArena.Tests.M12;

/// <summary>
/// Starts YOUR server as a real process - the dll built next to the tests - with the given
/// environment variables and arguments, in its own empty work directory, and stops it again.
/// Every KKARENA_* variable of the test run itself is removed first, so only what a test
/// sets counts. Up when GET /api/status answers on <see cref="HttpPort"/>.
/// </summary>
public sealed class ServerProcess : IDisposable
{
    public static string ArenaPath { get; } = Path.Combine(AppContext.BaseDirectory, "arena.json");
    public static string WeaponsPath { get; } = Path.Combine(AppContext.BaseDirectory, "weapons.json");

    public int HttpPort { get; }
    public string WorkDir { get; }
    public HttpClient Http { get; }

    private readonly Process _server;
    private readonly List<string> _log = new();

    public ServerProcess(int httpPort, IDictionary<string, string> environment, params string[] args)
        : this(httpPort, Path.Combine(Path.GetTempPath(), "kka-tests-m12-" + Guid.NewGuid().ToString("N")), environment, args)
    {
    }

    public ServerProcess(int httpPort, string workDir, IDictionary<string, string> environment, params string[] args)
    {
        HttpPort = httpPort;
        WorkDir = workDir;
        Directory.CreateDirectory(WorkDir);

        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = WorkDir,
            RedirectStandardInput = true,   // kept open: the server reads its console commands
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("KKARENA_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(key);
        foreach ((string key, string value) in environment)
            start.Environment[key] = value;
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "KnockKnockArena.Server.dll"));
        foreach (string arg in args)
            start.ArgumentList.Add(arg);

        _server = Process.Start(start) ?? throw new InvalidOperationException("could not start the server");
        _server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_log) _log.Add(e.Data); };
        _server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_log) _log.Add(e.Data); };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        Http = new HttpClient { BaseAddress = new Uri($"http://localhost:{HttpPort}"), Timeout = TimeSpan.FromSeconds(5) };

        // Up when /api/status answers - at most 15 s. A server that never answers is stopped
        // here: the test fails, but no server is left running behind it.
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            if (_server.HasExited)
                throw new InvalidOperationException($"the server exited with code {_server.ExitCode}:\n{Log}");
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
                throw new InvalidOperationException($"no answer from GET /api/status on port {HttpPort} after 15 s:\n{Log}");
            }
            Thread.Sleep(200);
        }
    }

    /// <summary>The server's console output so far.</summary>
    public string Log
    {
        get { lock (_log) return string.Join("\n", _log); }
    }

    public string Get(string path) => Http.GetStringAsync(path).GetAwaiter().GetResult();

    /// <summary>Two different ports that are free for TCP AND UDP - the game port is both.</summary>
    public static (int, int) FreePorts()
    {
        int first = FreePort(0);
        return (first, FreePort(first));
    }

    private static int FreePort(int except)
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

    /// <summary>Sends `quit` - the same shutdown as SIGTERM and Ctrl+C - and waits for the
    /// process to end. True if it ended within <paramref name="seconds"/>; afterwards
    /// <see cref="Log"/> holds all of its output.</summary>
    public bool Quit(int seconds)
    {
        _server.StandardInput.WriteLine("quit");
        if (!_server.WaitForExit(seconds * 1000))
            return false;
        _server.WaitForExit(); // drains the redirected output
        return true;
    }

    public void Dispose()
    {
        try
        {
            if (!_server.HasExited)
            {
                _server.StandardInput.WriteLine("quit");
                if (!_server.WaitForExit(3000))
                    _server.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { } // it ended between the check and the write
        Http.Dispose();
        try { Directory.Delete(WorkDir, recursive: true); } catch (IOException) { }
    }
}

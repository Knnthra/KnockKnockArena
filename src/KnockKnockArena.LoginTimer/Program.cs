// Times logins over TCP, as an attacker with a stopwatch would (Modul 11): it sends a LoginRequest, waits for the
// LoginResponse and measures the time between them, once per login, each login on a new TCP connection. The first
// login warms up the connection code and is not counted. It prints the server's answer and the median, the fastest
// and the slowest time.
//
// Usage: dotnet run --project src/KnockKnockArena.LoginTimer -- username password [logins host port]
//        defaults: 20 logins against 127.0.0.1 36363

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length < 2)
{
    Console.WriteLine("usage: dotnet run --project src/KnockKnockArena.LoginTimer -- username password [logins host port]");
    return 2;
}
string username = args[0], password = args[1];
int logins = args.Length > 2 ? int.Parse(args[2]) : 20;
string host = args.Length > 3 ? args[3] : "127.0.0.1";
int port = args.Length > 4 ? int.Parse(args[4]) : 36363;

List<double> times = new();
string answer = "";
for (int i = 0; i <= logins; i++)
{
    // IPv4 directly: "localhost" tries ::1 first, and a refused IPv6 attempt costs about 2 s on Windows
    using TcpClient tcp = new(AddressFamily.InterNetwork);
    tcp.Connect(host, port);
    NetworkStream stream = tcp.GetStream();
    stream.ReadTimeout = 5000;

    // the stopwatch runs from the request sent to the answer read: the time a client can see
    Stopwatch watch = Stopwatch.StartNew();
    TcpFraming.WriteMessage(stream, MessageType.LoginRequest, new LoginRequest { Username = username, Password = password }.ToPayload());
    LoginResponse? response = null;
    while (response == null && TcpFraming.TryReadMessage(stream, out MessageType type, out byte[] payload))
        if (type == MessageType.LoginResponse)
            response = LoginResponse.FromPayload(payload);
    watch.Stop();

    answer = response == null ? "no answer" : response.Success ? "Login OK" : response.Error;
    if (i > 0)
        times.Add(watch.Elapsed.TotalMilliseconds);
}

times.Sort();
double median = times.Count % 2 == 1 ? times[times.Count / 2] : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2;
Console.WriteLine($"{username} / {password}: {answer} - {logins} logins, median {median:0.0} ms (min {times[0]:0.0}, max {times[^1]:0.0})");
return 0;

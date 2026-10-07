using System.Net;
using KnockKnockArena.Shared.Protocol;
using System.Net.Sockets;
using KnockKnockArena.Shared.Protocol.Udp;
using System.Diagnostics;

const int DefaultPort = 27003;

string mode = args.Length > 0 ? args[0] : "resolve";

switch (mode)
{
    case "resolve":
        Resolve(args.Length > 1 ? args[1..] : new[] { "localhost", Dns.GetHostName(), "example.com", "no-such-host.invalid" });
        break;
    case "echo":
        return Echo(args.Length > 1 ? int.Parse(args[1]) : DefaultPort);
    case "busy":
        return Busy(args.Length > 1 ? int.Parse(args[1]) : DefaultPort);
    case "send":
        if (args.Length < 2)
        {
            Console.WriteLine("usage: send <host> [port] [count]");
            return 1;
        }
        return Send(args[1], args.Length > 2 ? int.Parse(args[2]) : DefaultPort, args.Length > 3 ? int.Parse(args[3]) : 10);
    case "peer":
        if (args.Length < 4)
        {
            Console.WriteLine("usage: peer <localPort> <host> <remotePort> [name]");
            return 1;
        }
        return Peer(int.Parse(args[1]), args[2], int.Parse(args[3]), args.Length > 4 ? args[4] : $"peer-{args[1]}");
    default:
        Console.WriteLine("modes: resolve | echo | busy | send | peer");
        return 1;
}

return 0;

static void Resolve(string[] hosts)
{
    foreach (string host in hosts)
    {
        if (AddressResolver.TryResolve(host, out IPAddress address, out string error))
        {
            string all = IPAddress.TryParse(host, out _) ? "(an IP literal: no DNS lookup)"
            : "all: " + string.Join(",", Dns.GetHostAddresses(host).Select(a => a.ToString()));

            Console.WriteLine($"{host,-24} -> {address,-16} {all}");

        }
        else
        {
            Console.WriteLine($"{host,-24} -> error {error}");
        }

    }
}

static int Echo(int port)
{
    UdpClient socket;

    try
    {
        socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }
    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
    {
        Console.WriteLine($"Port {port} is already in use: another program (or another echo) has it. ({ex.SocketErrorCode})");

        return 1;
    }

    Console.WriteLine($"UDP echo listening on 0.0.0.0:{port}. Ctrl+C to stop.");

    using (socket)
    {
        while (true)
        {
            IPEndPoint remote = new(IPAddress.Any, 0);

            byte[] datagram;

            try
            {
                datagram = socket.Receive(ref remote);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            Console.WriteLine(
    $"{datagram.Length} B from {remote}: {BitConverter.ToString(datagram, 0, Math.Min(datagram.Length, 12))}...");
            socket.Send(datagram, datagram.Length, remote);
        }
    }
}

static int Busy(int port)
{
    using UdpClient first = new(new IPEndPoint(IPAddress.Any, port));
    Console.WriteLine($"First socket bound to UDP port {port}.");
    try
    {
        using UdpClient second = new(new IPEndPoint(IPAddress.Any, port));
        Console.WriteLine("Second socket bound too (unexpected).");
        return 1;

    }
    catch (SocketException ex)
    {
        Console.WriteLine($"Second socket on the same port: {ex.SocketErrorCode} - \"{ex.Message}\"");
        Console.WriteLine("One port, one owner: this is what a second server on 36363 would see.");
        return 0;
    }

}

static int Send(string host, int port, int count)
{
    if (!AddressResolver.TryResolve(host, out IPAddress address, out string error))
    {
        Console.WriteLine(error);
        return 1;
    }

    PlayerEntityState player = new()
    {
        EntityId = 1,
        X = 132f,
        Z = 132f,
        AimX = 142f,
        AimZ = 132f,
        Health = 100,
        ActiveWeapon = WeaponId.Glock,
        OwnedWeapons = WeaponBits.StartingWeapons,
        Ammo = 24,
    };

    byte[] payload = ProtocolSerialization.WritePayload(writer => player.WriteTo(writer));

    using UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
    socket.Client.ReceiveTimeout = 1000;
    IPEndPoint server = new(address, port);
    Console.WriteLine($"{host} -> {address}. Sending {payload.Length} B of PlayerEntityState from local port " +
                  $"{((IPEndPoint)socket.Client.LocalEndPoint!).Port} to {server}, {count} times.");

    List<double> rtts = new();

    for (int i = 0; i < count; i++)
    {
        Stopwatch clock = Stopwatch.StartNew();
        socket.Send(payload, payload.Length, server);
        try
        {
            IPEndPoint from = new(IPAddress.Any, 0);
            byte[] reply = socket.Receive(ref from);
            double ms = clock.Elapsed.TotalMilliseconds;
            bool same = reply.AsSpan().SequenceEqual(payload);
            rtts.Add(ms);
            Console.WriteLine(
    $"  #{i}: {reply.Length} B back from {from} in {ms:0.000} ms{(same ? "" : "  (bytes differ!)")}");
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset)
        {
            Console.WriteLine($"  #{i}: no reply ({(ex.SocketErrorCode == SocketError.TimedOut
       ? "timed out after 1 s"
       : "port unreachable: is the echo running?")})");
        }
        Thread.Sleep(200);
    }

    if (rtts.Count > 0)
    {
        Console.WriteLine($"Round trip: min {rtts.Min():0.000} ms, avg {rtts.Average():0.000} ms, max {rtts.Max():0.000} ms, " +
                  $"{count - rtts.Count} lost of {count}");
    }

    return 0;

}

static int Peer(int localPort, string host, int remotePort, string name)
{
    if (!AddressResolver.TryResolve(host, out IPAddress address, out string error))
    {
        Console.WriteLine(error);
        return 1;
    }

    using UdpClient socket = new(new IPEndPoint(IPAddress.Any, localPort));
    IPEndPoint other = new(address, remotePort);
    Console.WriteLine($"{name}: listening on port {localPort}, talking to {other}. No server - type a line to send it.");

    Thread reciever = new(() =>
    {
        while (true)
        {
            try
            {
                IPEndPoint from = new(IPAddress.Any, 0);
                byte[] datagram = socket.Receive(ref from);
                Console.WriteLine($"  [{from}] {System.Text.Encoding.UTF8.GetString(datagram)}");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                Console.WriteLine("  (the other peer is not listening yet: port unreachable)");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                return;
            }
        }
    })
    { IsBackground = true };
    reciever.Start();

    string? line;


    while ((line = Console.ReadLine()) != null)
    {
        line = line.Trim();
        if (line.Length == 0)
            continue;
        byte[] chat = System.Text.Encoding.UTF8.GetBytes($"{name}: {line}");
        socket.Send(chat, chat.Length, other);
    }
    Thread.Sleep(500);
    return 0;
}



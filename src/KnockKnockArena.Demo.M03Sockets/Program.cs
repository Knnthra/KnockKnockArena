// M03 demo: the project's first network code. DNS, ports, sockets, a round trip.
//
//   dotnet run --project src/KnockKnockArena.Demo.M03Sockets -- resolve [host ...]
//   dotnet run --project src/KnockKnockArena.Demo.M03Sockets -- echo [port]
//   dotnet run --project src/KnockKnockArena.Demo.M03Sockets -- send <host> [port] [count]
//   dotnet run --project src/KnockKnockArena.Demo.M03Sockets -- busy [port]
//   dotnet run --project src/KnockKnockArena.Demo.M03Sockets -- interfaces
//   dotnet run --project src/KnockKnockArena.Demo.M03Sockets -- peer <localPort> <host> <remotePort> [name]
//   dotnet run --project src/KnockKnockArena.Demo.M03Sockets -- relay [port]
//
// resolve  what "localhost", this machine's name and a real hostname resolve to
//          (the shared AddressResolver the game client uses), and the error for a
//          name DNS does not know.
// echo     a UDP socket bound to a port that sends every datagram straight back.
//          Start it twice on the same port to see "the port is taken".
// send     fires raw PlayerEntityState bytes (the M02 serialization) at an echo
//          server and times each round trip: ping, measured by our own code.
//          In Wireshark (filter: udp.port == 27003) the 40 payload bytes sit inside
//          UDP, IP and Ethernet headers — encapsulation.
// busy     binds the port once, then tries again: the error the second server gets.
// interfaces  every network card: MAC address, IPv4 address, subnet mask and the
//          directed broadcast address worked out from them (host bits all set to 1).
//          "The machine" has several addresses — ipconfig shows the same list.
// peer     peer-to-peer chat: two EQUAL instances, each bound to its own port, send
//          KnockKnock chat lines straight to each other — no server in between. Type a
//          line to send it. Compare with the game: there the server is the authority;
//          here nobody is, and a peer behind NAT cannot be reached at all (M03: NAT).
// relay    a relay server for two peers (M10): it forwards every datagram from one peer to
//          the other. Both peers only send OUT, to the relay's known address - and a router
//          lets the answers to an outgoing packet back in. That is the way around NAT when
//          two peers cannot reach each other. The price: every datagram crosses the relay.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;

const int DefaultPort = 27003;

string mode = args.Length > 0 ? args[0] : "resolve";
switch (mode)
{
    case "resolve":
        Resolve(args.Length > 1 ? args[1..] : new[] { "localhost", Dns.GetHostName(), "example.com", "no-such-host.invalid" });
        return 0;
    case "echo":
        return Echo(args.Length > 1 ? int.Parse(args[1]) : DefaultPort);
    case "send":
        if (args.Length < 2)
        {
            Console.WriteLine("usage: send <host> [port] [count]");
            return 1;
        }
        return Send(args[1], args.Length > 2 ? int.Parse(args[2]) : DefaultPort, args.Length > 3 ? int.Parse(args[3]) : 10);
    case "busy":
        return Busy(args.Length > 1 ? int.Parse(args[1]) : DefaultPort);
    case "interfaces":
        Interfaces();
        return 0;
    case "peer":
        if (args.Length < 4)
        {
            Console.WriteLine("usage: peer <localPort> <host> <remotePort> [name]");
            return 1;
        }
        return Peer(int.Parse(args[1]), args[2], int.Parse(args[3]), args.Length > 4 ? args[4] : $"peer-{args[1]}");

    case "relay":
        return Relay(args.Length > 1 ? int.Parse(args[1]) : 27020);
    default:
        Console.WriteLine("modes: resolve | echo | send | busy | interfaces | peer | relay");
        return 1;
}

static void Resolve(string[] hosts)
{
    foreach (string host in hosts)
    {
        if (AddressResolver.TryResolve(host, out IPAddress address, out string error))
        {
            // Show everything DNS returned too, so the IPv4 choice is visible.
            string all = IPAddress.TryParse(host, out _)
                ? "(an IP literal: no DNS lookup)"
                : "all: " + string.Join(", ", Dns.GetHostAddresses(host).Select(a => a.ToString()));
            Console.WriteLine($"{host,-24} -> {address,-16} {all}");
        }
        else
        {
            Console.WriteLine($"{host,-24} -> error: {error}");
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
                continue; // Windows reports an ICMP "port unreachable" from an earlier send here
            }
            Console.WriteLine($"{datagram.Length} B from {remote}: {BitConverter.ToString(datagram, 0, Math.Min(datagram.Length, 12))}...");
            socket.Send(datagram, datagram.Length, remote);
        }
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
        EntityId = 1, X = 132f, Z = 132f, AimX = 142f, AimZ = 132f, Health = 100,
        ActiveWeapon = WeaponId.Glock, OwnedWeapons = WeaponBits.StartingWeapons, Ammo = 24,
    };
    byte[] payload = ProtocolSerialization.WritePayload(writer => player.WriteTo(writer));

    using UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
    socket.Client.ReceiveTimeout = 1000;
    IPEndPoint server = new(address, port);
    Console.WriteLine($"{host} -> {address}. Sending {payload.Length} B of PlayerEntityState from local port " +
                      $"{((IPEndPoint)socket.Client.LocalEndPoint!).Port} to {server}, {count} times.");

    List<double> rtts = new();
    for (int i = 1; i <= count; i++)
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
            Console.WriteLine($"  #{i}: {reply.Length} B back from {from} in {ms:0.000} ms{(same ? "" : "  (bytes differ!)")}");
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset)
        {
            // ConnectionReset: nobody listens on that port (ICMP port unreachable).
            Console.WriteLine($"  #{i}: no reply ({(ex.SocketErrorCode == SocketError.TimedOut ? "timed out after 1 s" : "port unreachable: is the echo running?")})");
        }
        Thread.Sleep(200);
    }

    if (rtts.Count > 0)
        Console.WriteLine($"Round trip: min {rtts.Min():0.000} ms, avg {rtts.Average():0.000} ms, max {rtts.Max():0.000} ms, " +
                          $"{count - rtts.Count} lost of {count}");
    return 0;
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

static void Interfaces()
{
    foreach (System.Net.NetworkInformation.NetworkInterface nic in
             System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
    {
        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            continue;
        System.Net.NetworkInformation.IPInterfaceProperties properties = nic.GetIPProperties();
        var ipv4 = properties.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
        if (ipv4.Count == 0)
            continue; // Windows also lists filter drivers (WFP, QoS) as cards: no IPv4, nothing to show
        byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
        string macText = mac.Length == 0 ? "(none)" : string.Join(":", mac.Select(b => b.ToString("x2")));
        Console.WriteLine($"{nic.Name} ({nic.NetworkInterfaceType})");
        Console.WriteLine($"  MAC  {macText}");
        foreach (System.Net.NetworkInformation.UnicastIPAddressInformation unicast in ipv4)
        {
            byte[] ip = unicast.Address.GetAddressBytes();
            byte[] mask = unicast.IPv4Mask.GetAddressBytes();
            byte[] broadcast = new byte[4];
            for (int i = 0; i < 4; i++)
                broadcast[i] = (byte)(ip[i] | ~mask[i]); // host bits (0 in the mask) set to 1
            Console.WriteLine($"  IPv4 {unicast.Address}/{unicast.PrefixLength}  mask {unicast.IPv4Mask}  broadcast {new IPAddress(broadcast)}");
        }
        // The default gateway: the router that gets every packet for another network.
        var gateways = properties.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
        Console.WriteLine($"  gateway {(gateways.Count == 0 ? "(none)" : string.Join(", ", gateways.Select(g => g.Address)))}");
    }
    Console.WriteLine("Loopback has no MAC: its datagrams never reach a network card. Wi-Fi and Ethernet do.");
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

    Thread receiver = new(() =>
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
            catch (ObjectDisposedException)
            {
                return; // socket closed on exit
            }
        }
    }) { IsBackground = true };
    receiver.Start();

    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        line = line.Trim();
        if (line.Length == 0)
            continue;
        byte[] chat = System.Text.Encoding.UTF8.GetBytes($"{name}: {line}");
        socket.Send(chat, chat.Length, other);
    }
    Thread.Sleep(500); // let the last replies arrive before closing
    return 0;
}

static int Relay(int port)
{
    using UdpClient socket = new(new IPEndPoint(IPAddress.Any, port));
    Console.WriteLine($"relay: listening on port {port}. Forwarding between the first two peers that write.");

    // The relay learns each peer's address from the peer's own first datagram: the
    // address and port its router shows the outside world. Answers to that address get
    // through the router, because the peer sent first.
    IPEndPoint? first = null, second = null;
    while (true)
    {
        IPEndPoint from = new(IPAddress.Any, 0);
        byte[] datagram;
        try
        {
            datagram = socket.Receive(ref from);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            continue; // a peer that went away (ICMP port unreachable) - keep relaying
        }

        if (first == null)
            first = from;
        else if (second == null && !from.Equals(first))
            second = from;

        IPEndPoint? to = from.Equals(first) ? second : from.Equals(second) ? first : null;
        
        if (to == null)
        {
            Console.WriteLine(from.Equals(first)
                ? $"relay: {from} is the first peer - waiting for the second (dropped, nobody to send it to)"
                : $"relay: {from} is neither peer - dropped");
            continue;
        }
        socket.Send(datagram, datagram.Length, to);
        Console.WriteLine($"relay: {datagram.Length} B {from} -> {to}");
        
    }
}

// A target that runs by itself, so one person can test shooting at a moving player (Modul 8): it logs in like a
// client (TCP login, UDP join), then sends one input per tick. Enter switches between standing still and running
// left and right; it faces south, so A and D move it along x.
//
// Usage: dotnet run --project src/KnockKnockArena.Runner [-- host port username password ticksPerDirection]
//        defaults: 127.0.0.1 36363 anna test 90 (3 s each way)

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 36363;
string username = args.Length > 2 ? args[2] : "anna";
string password = args.Length > 3 ? args[3] : "test";
int ticksPerDirection = args.Length > 4 ? int.Parse(args[4]) : 90;

IPAddress address = (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == AddressFamily.InterNetwork);

// ---- TCP: log in and keep reading, so the server's messages never pile up
using TcpClient tcp = new();
tcp.Connect(address, port);
NetworkStream stream = tcp.GetStream();
using ManualResetEventSlim loginDone = new();
byte playerId = 0;
byte[] token = Array.Empty<byte>();
string? loginError = null;
new Thread(() =>
{
    try
    {
        while (TcpFraming.TryReadMessage(stream, out MessageType type, out byte[] payload))
        {
            if (type != MessageType.LoginResponse) continue;
            LoginResponse login = LoginResponse.FromPayload(payload);
            if (login.Success) { playerId = login.PlayerId; token = login.SessionToken; }
            else loginError = login.Error;
            loginDone.Set();
        }
    }
    catch (IOException) { }
    Console.WriteLine("[runner] The server closed the connection.");
    Environment.Exit(0);
}) { IsBackground = true }.Start();

TcpFraming.WriteMessage(stream, MessageType.LoginRequest, new LoginRequest { Username = username, Password = password }.ToPayload());
if (!loginDone.Wait(TimeSpan.FromSeconds(5)) || loginError != null)
{
    Console.WriteLine($"[runner] Login failed: {loginError ?? "no answer"}");
    return 1;
}
Console.WriteLine($"[runner] Logged in as {username}#{playerId}");

// ---- UDP: join until the first snapshot, then read snapshots for our own position
using UdpClient udp = new(AddressFamily.InterNetwork);
udp.Connect(address, port);
object gate = new();
bool inGame = false;
float x = 0f, z = 0f;
uint serverTick = 0;
new Thread(() =>
{
    while (true)
    {
        byte[] datagram;
        try { IPEndPoint? from = null; datagram = udp.Receive(ref from); }
        catch (SocketException) { continue; }
        if (datagram.Length < 1 || datagram[0] != (byte)UdpPacketType.Snapshot) continue;
        SnapshotPacket snapshot;
        try { snapshot = SnapshotPacket.FromDatagram(datagram); }
        catch (ProtocolException) { continue; }
        lock (gate)
        {
            serverTick = snapshot.ServerTick;
            foreach (PlayerEntityState p in snapshot.Players)
                if (p.EntityId == playerId) { x = p.X; z = p.Z; inGame = true; }
        }
    }
}) { IsBackground = true }.Start();

byte[] join = new JoinPacket { SessionToken = token }.ToDatagram();
while (!inGame) { udp.Send(join, join.Length); Thread.Sleep(500); }
Console.WriteLine($"[runner] In the game. Press Enter to start or stop running ({ticksPerDirection} ticks each way), Ctrl+C to quit.");

// ---- one input per tick; Enter toggles running
bool running = false;
new Thread(() =>
{
    while (Console.ReadLine() != null)
    {
        running = !running;
        Console.WriteLine(running ? "[runner] Running left and right." : "[runner] Standing still.");
    }
}) { IsBackground = true }.Start();

uint inputIndex = 0;
int tick = 0;
while (true)
{
    Thread.Sleep(1000 / GameConstants.TickRate);
    float myX, myZ; uint seen;
    lock (gate) { myX = x; myZ = z; seen = serverTick; }
    MovementBits movement = MovementBits.None;
    if (running)
        movement = (tick++ / ticksPerDirection) % 2 == 0 ? MovementBits.D : MovementBits.A;
    InputPacket input = new()
    {
        PlayerId = playerId,
        InputIndex = ++inputIndex,
        Tick = seen,
        Movement = movement,
        AimX = myX,          // face south: the aim point is 30 m south of us
        AimZ = myZ - 30f,
    };
    byte[] datagram = input.ToDatagram();
    udp.Send(datagram, datagram.Length);
    if (inputIndex % 30 == 0)
        Console.WriteLine($"[runner] {(running ? "running" : "standing")} at ({myX.ToString("0.0", CultureInfo.InvariantCulture)}, {myZ.ToString("0.0", CultureInfo.InvariantCulture)})");
}

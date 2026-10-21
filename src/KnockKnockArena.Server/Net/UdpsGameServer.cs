using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Server.Game;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Net;

public sealed class UdpGameServer
{
    private readonly ServerConfig _config;
    private readonly SessionManager _sessions;
    private readonly GameWorld _world;
    
    private UdpClient? _udp;
    
    
    public Action<byte, string>? KickPlayer;
    
    public UdpGameServer(ServerConfig config, SessionManager sessions, GameWorld world)
    {
        _config = config;
        _sessions = sessions;
        _world = world;
    }
    
    public void Start()
    {
        _udp = new UdpClient(_config.Port);
        DisableIcmpConnectionReset(_udp);
        Log.Info($"UDP listening on port {_config.Port}, tick rate {GameConstants.TickRate} Hz");
        new Thread(ReceiveLoop) { IsBackground = true, Name = "udp-receive" }.Start();
        new Thread(TickLoop) { IsBackground = true, Name = "udp-tick" }.Start();
    }
    
    private static void DisableIcmpConnectionReset(UdpClient udp)
    {
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try
        {
            udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch (Exception)
        {
        }
    }
    
    private void Dispatch(byte[] datagram, IPEndPoint remote)
    {
        UdpPacketType type = UdpPacketIO.PeekType(datagram);
        Log.Packet($"RECV UDP {type} ({datagram.Length} B) from {remote}");
    
        switch (type)
        {
            case UdpPacketType.Join:
                HandleJoin(JoinPacket.FromDatagram(datagram), remote);
                break;
            case UdpPacketType.Input:
                HandleInput(InputPacket.FromDatagram(datagram), remote);
                break;
            case UdpPacketType.Heartbeat:
                HandleHeartbeat(HeartbeatPacket.FromDatagram(datagram), remote);
                break;
            case UdpPacketType.DiscoveryRequest:
                HandleDiscovery(DiscoveryRequestPacket.FromDatagramAnyVersion(datagram), remote);
                break;
            default:
                throw new ProtocolException($"unknown type {(byte)type}");
        }
    }
    
    private void ReceiveLoop()
    {
        while (true)
        {
            IPEndPoint remote = new(IPAddress.Any, 0);
            byte[] datagram;
            try
            {
                datagram = _udp!.Receive(ref remote);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            try
            {
                Dispatch(datagram, remote);
            }
            catch (ProtocolException ex)
            {
                Log.Warn($"REJECT UDP from {remote}: {ex.Reason}");
            }
        }
    }
    
    private void HandleJoin(JoinPacket join, IPEndPoint remote)
    {
        if (!_sessions.TryGetByToken(join.SessionToken, out PlayerSession? session))
            throw new ProtocolException("bad token");
        bool joined = _world.SpawnOrRebind(session!, remote, out PlayerState player);
        if (joined)
            Log.Info($"UDP join: {session!.Username}#{session.PlayerId} from {remote} " +
                     $"({_world.PlayerCount} in game)");
                     else if (!player.EndPoint.Equals(remote))
                         Log.Info($"UDP rebind: {session!.Username}#{session.PlayerId} now at {remote}");
    }
    
    private void HandleInput(InputPacket input, IPEndPoint remote)
    {
        if (!_world.TryGetByEndPoint(remote, out PlayerState player))
            throw new ProtocolException("unknown endpoint");
        if (input.PlayerId != player.Session.PlayerId)
            throw new ProtocolException($"bad player id {input.PlayerId}");
            
            player.LastUdpPacketUtc = DateTime.UtcNow;
            if (!_world.TryEnqueueInput(player, input))
                throw new ProtocolException("input queue full");
        
    }
    
    
    private void HandleHeartbeat(HeartbeatPacket heartbeat, IPEndPoint remote)
    {
        if (!_world.TryGetByEndPoint(remote, out PlayerState player))
            throw new ProtocolException("unknown endpoint");
        if (heartbeat.PlayerId != player.Session.PlayerId)
            throw new ProtocolException($"bad player id {heartbeat.PlayerId}");
            player.LastUdpPacketUtc = DateTime.UtcNow;
    }
    
    private void HandleDiscovery(DiscoveryRequestPacket request, IPEndPoint remote)
    {
        byte[] answer = new DiscoveryResponsePacket
        {
            Nonce = request.Nonce,
            GamePort = (ushort)_config.Port,
            HttpPort = 0,
            PlayersOnline = (byte)_sessions.Count,
            MaxPlayers = (byte)_config.MaxPlayers,
            ServerName = _config.ServerName,
            MapName = ArenaMap.Name,
            
        }.ToDatagram();
        _udp!.Send(answer, answer.Length, remote);
        Log.Packet($"Discovery answer to {remote} (client speaks v{request.ClientVersion}, {answer.Length} B)");
    }
    
    private void TickLoop()
    {
        Stopwatch clock = Stopwatch.StartNew();
        double tickIntervalMs = 1000.0 / GameConstants.TickRate;
        double nextTickAtMs = tickIntervalMs;
        List<(IPEndPoint EndPoint, byte[] Datagram)> snapshotSends = new();
        List<PlayerState> timedOut = new();
        while (true)
        {
            double waitMs = nextTickAtMs - clock.Elapsed.TotalMilliseconds;
            if (waitMs > 1)
            {
                Thread.Sleep((int)waitMs);
                continue;
            }
            if (waitMs < -1000)
            {
                Log.Warn("Tick loop fell behind; resyncing clock");
                nextTickAtMs = clock.Elapsed.TotalMilliseconds;
            }
            nextTickAtMs += tickIntervalMs;
            snapshotSends.Clear();
            timedOut.Clear();
            _world.Tick(snapshotSends, timedOut);
            
            foreach ((IPEndPoint endPoint, byte[] datagram) in snapshotSends)
            {
                try
                {
                    _udp!.Send(datagram, datagram.Length, endPoint);
                }
                catch (SocketException)
                {
                }
            }
            foreach (PlayerState player in timedOut)
            {
                Log.Info($"UDP timeout: {player.Session.Username}#{player.Session.PlayerId} " +
                         $"(no packets for {GameConstants.UdpTimeoutSeconds:0} s)");
                KickPlayer?.Invoke(player.Session.PlayerId, "udp timeout");
            }
        }
    }
}
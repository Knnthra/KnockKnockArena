using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KnockKnockArena.Shared.Protocol.Udp
{
    public sealed class DiscoveredServer
    {
        public IPAddress Address = IPAddress.None;

        public int SourcePort;

        public byte ProtocolVersion;

        public DiscoveryResponsePacket? Info;

        public bool Compatible => Info != null;

        public override string ToString() => Info == null
            ? $"{Address}:{SourcePort}  (protocol v{ProtocolVersion} — incompatible, this client speaks v{ProtocolConstants.Version})"
            : $"{Info.ServerName}  {Address}:{Info.GamePort}  {Info.PlayersOnline}/{Info.MaxPlayers} players  " +
              $"map \"{Info.MapName}\"  REST :{Info.HttpPort}  v{ProtocolVersion}";


    }
    public static class LanDiscovery
    {
        public static List<IPAddress> BroadcastTargets()
        {
            List<IPAddress> targets = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface nic in
                         System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation unicast in
                             nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                            unicast.IPv4Mask == null || IPAddress.IsLoopback(unicast.Address))
                            continue;
                        byte[] ip = unicast.Address.GetAddressBytes();
                        byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                        byte[] broadcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                            broadcast[i] = (byte)(ip[i] | ~mask[i]);
                        IPAddress directed = new IPAddress(broadcast);
                        if (!targets.Contains(directed))
                            targets.Add(directed);

                    }
                }
            }
            catch (Exception)
            {
            }
            return targets;
        }
        
        private static bool SameServer(DiscoveredServer a, DiscoveredServer b)
        {
            if (a.Info == null || b.Info == null)
                return a.Info == null && b.Info == null && a.SourcePort == b.SourcePort;
                return a.Info.ServerName == b.Info.ServerName && a.Info.GamePort == b.Info.GamePort &&
                       a.Info.HttpPort == b.Info.HttpPort;
        }
        
        public static List<DiscoveredServer> Search(int gamePort, TimeSpan listenFor)
        {
            List<DiscoveredServer> found = new List<DiscoveredServer>();
            uint nonce = (uint)new Random().Next();
            using (UdpClient socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                socket.EnableBroadcast = true;
                byte[] request = new DiscoveryRequestPacket { Nonce = nonce }.ToDatagram();
                foreach (IPAddress target in BroadcastTargets())
                {
                    try
                    {
                        socket.Send(request, request.Length, new IPEndPoint(target, gamePort));
                    }
                    catch (SocketException)
                    {
                    }
                    
                }
                Stopwatch clock = Stopwatch.StartNew();
                while (true)
                {
                    int remainingMs = (int)(listenFor.TotalMilliseconds - clock.ElapsedMilliseconds);
                    if (remainingMs <= 0)
                        break;
                    socket.Client.ReceiveTimeout = remainingMs;
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] datagram;
                    try
                    {
                        datagram = socket.Receive(ref remote);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        break;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        continue;
                    }
                    try
                    {
                        if (datagram.Length < 2 || (UdpPacketType)datagram[0] != UdpPacketType.DiscoveryResponse)
                            continue;
                        DiscoveredServer server = new DiscoveredServer
                        {
                            Address = remote.Address,
                            SourcePort = remote.Port,
                            ProtocolVersion = DiscoveryResponsePacket.PeekVersion(datagram),
                        };
                        if (server.ProtocolVersion == ProtocolConstants.Version)
                        {
                            DiscoveryResponsePacket info = DiscoveryResponsePacket.FromDatagram(datagram);
                            if (info.Nonce != nonce)
                                continue;
                            server.Info = info;
                        }   
                        if (!found.Exists(s => SameServer(s, server)))
                            found.Add(server);
                    }
                    catch (ProtocolException)
                    {
                    }
                    
                }
            }
          return found;
        }
    }
   
}
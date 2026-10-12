using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Networking
{
    public sealed class TcpConnectedEvent { }

    public sealed class TcpDisconnectedEvent
    {
        public string Reason = "";
    }

    public sealed class NetworkClient : IDisposable
    {
        public readonly ConcurrentQueue<object> TcpMessages = new ConcurrentQueue<object>();
        public byte PlayerId { get; private set; }
        public bool InGame => _inGame;
        public long TcpMessagesSkipped;
        private TcpClient? _tcp;
        private UdpClient? _udp;
        private IPEndPoint? _serverEndPoint;
        private byte[] _sessionToken = Array.Empty<byte>();
        private NetworkStream? _tcpStream;
        private IPAddress _serverAddress = IPAddress.None;
        private int _port;
        private volatile bool _running = true;
        private volatile bool _inGame;
        private const int JoinAttempts = 4;
        public long JoinsSent;
        public DateTime LastUdpSendUtc { get; private set; } = DateTime.UtcNow;
        private const int HeartbeatIntervalMs = 1000;
        private const int HeartbeatCheckIntervalMs = 200;
        public int HeartbeatsSent;
        
        private void RecieveTcpLoop()
        {
            try
            {
                while (_running && TcpFraming.TryReadMessage(_tcpStream, out MessageType type, out byte[] payload))
                {
                    switch (type)
                    {
                        case MessageType.LoginResponse:
                            LoginResponse login = LoginResponse.FromPayload(payload);
                            if (login.Success)
                            {
                                PlayerId = login.PlayerId;
                                _sessionToken = login.SessionToken;
                            }
                            TcpMessages.Enqueue(login);
                            break;
                        case MessageType.ServerInfo:
                            TcpMessages.Enqueue(ServerInfoMessage.FromPayload(payload));
                            break;
                        case MessageType.ChatBroadcast:
                            TcpMessages.Enqueue(ChatBroadcast.FromPayload(payload));
                            break;
                        case MessageType.PlayerJoined:
                            TcpMessages.Enqueue(PlayerJoined.FromPayload(payload));
                            break;
                        case MessageType.PlayerLeft:
                            TcpMessages.Enqueue(PlayerLeft.FromPayload(payload));
                            break;
                        default:
                            Interlocked.Increment(ref TcpMessagesSkipped);
                            break;
                    }
                }
                TcpMessages.Enqueue(new TcpDisconnectedEvent { Reason = "server closed the connection" });
            }
            catch (Exception ex)
            {
                if (_running)
                    TcpMessages.Enqueue(new TcpDisconnectedEvent { Reason = ex.Message });
            }
        }

        public void ConnectAndLogin(string host, int port, string username, string password)
        {
            _port = port;
            Thread tcpThread = new Thread(() =>
            {
                try
                {
                    _serverAddress = AddressResolver.Resolve(host);
                    _tcp = new TcpClient(AddressFamily.InterNetwork);
                    _tcp.Connect(_serverAddress, port);
                    _tcpStream = _tcp.GetStream();
                    TcpFraming.WriteMessage(_tcpStream, MessageType.LoginRequest, new LoginRequest
                    {
                        Username = username,
                        Password = password,
                    }.ToPayload());
                    TcpMessages.Enqueue(new TcpConnectedEvent());

                    RecieveTcpLoop();

                }
                catch (Exception ex)
                {
                    TcpMessages.Enqueue(new TcpDisconnectedEvent
                    {
                        Reason = ex.Message
                    });
                }
            })
            { IsBackground = true, Name = "kka-tcp", };
            tcpThread.Start();
        }

        public void SendChat(string text)
        {
            NetworkStream? stream = _tcpStream;

            if (stream == null)
                return;

            TcpFraming.WriteMessage(stream, MessageType.ChatSend, new ChatSend { Text = text }.ToPayload());
        }


        public void StartGame()
        {
            _serverEndPoint = new IPEndPoint(_serverAddress, _port);
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

            try
            {
                _udp.Client.IOControl(unchecked((int)0x9800000C) /* SIO_UDP_CONNRESET */,
                    new byte[] { 0 }, null);
            }
            catch
            {
            }
            byte[] joinDatagram = new JoinPacket { SessionToken = _sessionToken }.ToDatagram();
            Thread joinThread = new Thread(() =>
            {
                for (int i = 0; i < JoinAttempts && _running; i++)
                {
                    UdpSend(joinDatagram);
                    Interlocked.Increment(ref JoinsSent);
                    Thread.Sleep(500);
                }
                _inGame = true;

            })
            {
                IsBackground = true,
                Name = "kka-join",
            };
            joinThread.Start();
            
            Thread heartbeatThread = new Thread(HeartbeatLoop)
            {
                IsBackground = true,
                Name = "kka-heartbeat",
            };
            heartbeatThread.Start();
            try { _udp?.Close(); } catch { }

        }


        private void HeartbeatLoop()
        {
            while (_running)
            {
                Thread.Sleep(HeartbeatCheckIntervalMs);

                if (!_inGame)
                    continue;

                double quietMs = (DateTime.UtcNow - LastUdpSendUtc).TotalMilliseconds;
                if (quietMs < HeartbeatIntervalMs)
                    continue;
                UdpSend(new HeartbeatPacket { PlayerId = PlayerId }.ToDatagram());
                Interlocked.Increment(ref HeartbeatsSent);
            }
        }

        private void UdpSend(byte[] datagram)
        {
            UdpClient? udp = _udp;
            IPEndPoint? server = _serverEndPoint;
            if (udp == null || server == null)
                return;

            LastUdpSendUtc = DateTime.UtcNow;
            try { udp.Send(datagram, datagram.Length, server); }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException) { }
        }


        public void Dispose()
        {
            _running = false;
            try { _tcp?.Close(); }
            catch { }
        }
    }
}

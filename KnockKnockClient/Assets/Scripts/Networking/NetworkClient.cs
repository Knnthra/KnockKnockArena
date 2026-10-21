using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Networking
{
    /// <summary>Queued marker: the TCP connection is up and the login has been sent.</summary>
    public sealed class TcpConnectedEvent { }

    /// <summary>Queued marker: the TCP connection is gone (server closed it, or an error).</summary>
    public sealed class TcpDisconnectedEvent
    {
        public string Reason = "";
    }

    /// <summary>
    /// The connection to the game server: TCP (login, chat, events) and UDP (join,
    /// inputs, snapshots). Pure C# — no Unity API anywhere in this class.
    ///
    /// Threading model (the pattern from T04/T05, exemplary on purpose):
    /// blocking receives run on background threads, which ONLY parse and enqueue.
    /// The Unity side drains <see cref="TcpMessages"/> and polls
    /// <see cref="TryTakeSnapshot"/> from Update() on the main thread.
    /// </summary>
    public sealed class NetworkClient : IDisposable
    {
        /// <summary>Parsed TCP messages plus TcpConnectedEvent/TcpDisconnectedEvent markers.
        /// Drained on the Unity main thread.</summary>
        public readonly ConcurrentQueue<object> TcpMessages = new ConcurrentQueue<object>();

        /// <summary>Delay applied to all UDP traffic in BOTH directions (so the round
        /// trip is twice this). Makes latency problems visible on localhost.</summary>
        public int SimulatedLatencyMs;

        public byte PlayerId { get; private set; }

        /// <summary>True once the first snapshot has arrived (= the UDP join succeeded).</summary>
        public bool InGame => _inGame;

        /// <summary>Input packets sent so far (the index of the newest one).</summary>
        public uint InputIndex => _inputIndex;

        // Diagnostics (read from the main thread for the HUD).
        public long TcpMessagesSkipped;
        public long JoinsSent;
        public long UdpDatagramsReceived;
        public long SnapshotsAccepted;
        public long UdpParseErrors;
        public long UdpForeignSourceDrops;
        public volatile string UdpLastForeignSource = "";
        public volatile string UdpLastError = "";

        private readonly object _snapshotGate = new object();
        private SnapshotPacket? _latestSnapshot;
        private bool _snapshotIsNew;
        private uint _newestSequence;

        private TcpClient? _tcp;
        private NetworkStream? _tcpStream;
        private UdpClient? _udp;
        private IPEndPoint? _serverEndPoint;
        private byte[] _sessionToken = Array.Empty<byte>();
        private uint _inputIndex;
        private IPAddress _serverAddress = IPAddress.None;
        private int _port;
        private volatile bool _running = true;
        private volatile bool _inGame;

        /// <summary>
        /// Connects and logs in on a background thread (TcpClient.Connect blocks, so
        /// it must not run on the Unity main thread). Progress arrives as queued
        /// events: TcpConnectedEvent, then LoginResponse/ServerInfo/etc., or
        /// TcpDisconnectedEvent on failure.
        /// </summary>
        public void ConnectAndLogin(string host, int port, string username, string password)
        {
            _port = port;

            Thread tcpThread = new Thread(() =>
            {
                try
                {
                    // Resolve once, here on the background thread (DNS can block): the
                    // Host field may hold "localhost", a hostname or an IP (M03). TCP and
                    // UDP then use the same IPv4 address.
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

                    ReceiveTcpLoop();
                }
                catch (Exception ex)
                {
                    TcpMessages.Enqueue(new TcpDisconnectedEvent { Reason = ex.Message });
                }
            })
            {
                IsBackground = true,
                Name = "kka-tcp",
            };
            tcpThread.Start();
        }

        private void ReceiveTcpLoop()
        {
            try
            {
                while (_running && TcpFraming.TryReadMessage(_tcpStream, out MessageType type, out byte[] payload))
                {
                    // Parse on the receive thread, enqueue the typed object; the main
                    // thread never touches the socket, this thread never touches Unity.
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
                            // Unknown types are skipped: framing already consumed the
                            // payload, so the stream stays in sync. Counted, so the HUD
                            // can show it (a newer server sends types we do not know).
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

        public void SendChat(string text)
        {
            NetworkStream? stream = _tcpStream;
            if (stream == null)
                return;
            TcpFraming.WriteMessage(stream, MessageType.ChatSend, new ChatSend { Text = text }.ToPayload());
        }

        /// <summary>
        /// Starts the UDP side after a successful login: receive thread + a join loop
        /// that resends the session token every 0.5 s until the first snapshot lands
        /// (there is no join-ack — snapshots ARE the confirmation).
        /// </summary>
        public void StartGame()
        {
            // Deliberately UNCONNECTED UDP with an explicit IPv4 bind and an
            // explicitly resolved IPv4 server address. UdpClient.Connect() installs
            // an OS-level source filter, and subtle address-family differences
            // (dual-mode sockets, "localhost" -> ::1) make replies vanish silently
            // on some runtimes. We filter by source ourselves — visibly (counter).
            _serverEndPoint = new IPEndPoint(_serverAddress, _port);
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

            // Windows: an ICMP "port unreachable" from a send would otherwise surface
            // as ConnectionReset on subsequent Receive calls (same pitfall as the
            // server, FEJLLOG #7). Not supported on all platforms — best effort.
            try
            {
                _udp.Client.IOControl(unchecked((int)0x9800000C) /* SIO_UDP_CONNRESET */,
                    new byte[] { 0 }, null);
            }
            catch
            {
                // Ignored: the receive loop also tolerates the exception.
            }

            Thread udpReceiveThread = new Thread(ReceiveUdpLoop)
            {
                IsBackground = true,
                Name = "kka-udp",
            };
            udpReceiveThread.Start();

            byte[] joinDatagram = new JoinPacket { SessionToken = _sessionToken }.ToDatagram();
            Thread joinThread = new Thread(() =>
            {
                // Module 6 replaces M05's guess ("send 4 joins and assume we are in"):
                // the server now answers every tick with a snapshot, so the first one
                // IS the confirmation. Resend until it lands - however many that takes.
                while (_running && !_inGame)
                {
                    UdpSend(joinDatagram);
                    Interlocked.Increment(ref JoinsSent);
                    Thread.Sleep(500);
                }
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
        }

        private void ReceiveUdpLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (_running)
                {
                    byte[] datagram;
                    try
                    {
                        datagram = _udp!.Receive(ref remote);
                    }
                    catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                    {
                        if (!_running)
                            break;
                        continue;
                    }

                    Interlocked.Increment(ref UdpDatagramsReceived);

                    // Only the game server may talk to us on this socket.
                    if (!remote.Address.Equals(_serverEndPoint!.Address) || remote.Port != _serverEndPoint.Port)
                    {
                        Interlocked.Increment(ref UdpForeignSourceDrops);
                        UdpLastForeignSource = $"{remote} (expected {_serverEndPoint})";
                        continue;
                    }

                    int delayMs = SimulatedLatencyMs;
                    if (delayMs <= 0)
                        HandleDatagram(datagram);
                    else
                        Task.Delay(delayMs).ContinueWith(_ => HandleDatagram(datagram));
                }
            }
            catch (Exception ex)
            {
                // Anything unexpected must be VISIBLE, not a silently dead thread.
                UdpLastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        // HandleDatagram skrives her i videoen (Modul 6 kort version, implementering 5)
        private void HandleDatagram(byte[] datagram)
        {
            try
            {
                UdpPacketType type = UdpPacketIO.PeekType(datagram);
                if (type != UdpPacketType.Snapshot)
                    return;
                SnapshotPacket snapshot = SnapshotPacket.FromDatagram(datagram);
                lock (_snapshotGate)
                {
                    if (snapshot.Sequence <= _newestSequence)
                        return;
                    _newestSequence = snapshot.Sequence;
                    _latestSnapshot = snapshot;
                    _snapshotIsNew = true;
                }
                _inGame = true;
                Interlocked.Increment(ref SnapshotsAccepted);
            }
            catch (ProtocolException)
            {
                Interlocked.Increment(ref UdpParseErrors);
            }
            
        }

        // TryTakeSnapshot skrives her i videoen (Modul 6 kort version, implementering 5)
        public bool TryTakeSnapshot(out SnapshotPacket snapshot)
        {
            lock (_snapshotGate)
            {
                if (!_snapshotIsNew)
                {
                    snapshot = null!;
                    return false;
                }
                _snapshotIsNew = false;
                snapshot = _latestSnapshot!;
                return true;
            }
        }

        // SendInput skrives her i videoen (Modul 6 kort version, implementering 5)
        public uint SendInput(MovementBits movement, float aimX, float aimZ, ButtonBits buttons, byte weaponId)
        {
            uint tickEstimate;
            lock (_snapshotGate)
                tickEstimate = _latestSnapshot?.ServerTick ?? 0;
                uint index = ++_inputIndex;
                UdpSend(new InputPacket
                {
                    PlayerId = PlayerId,
                    InputIndex = index,
                    Tick = tickEstimate,
                    Movement = movement,
                    AimX = aimX,
                    AimZ = aimZ,
                    Buttons = buttons,
                    WeaponId = weaponId,
                }.ToDatagram());
                return index;
        }
        
        
        /// <summary>
        /// Keeps the session alive when there is nothing else to send.
        ///
        /// While the game loop is running, inputs go out 30 times a second and any
        /// packet resets the server's timeout, so this never fires. It earns its keep
        /// in the gaps: the player sitting in a menu, a dead player waiting to
        /// respawn, a window that lost focus. The server cannot tell "quiet" from
        /// "gone" — UDP has no connection to lose — so without a sign of life it
        /// drops you after UdpTimeoutSeconds.
        ///
        /// It runs on its OWN thread rather than out of the game loop, because the
        /// cases it exists for are exactly the ones where the game loop is not
        /// running.
        /// </summary>
        private void HeartbeatLoop()
        {
            while (_running)
            {
                Thread.Sleep(HeartbeatCheckIntervalMs);

                if (!_inGame)
                    continue;

                double quietMs = (DateTime.UtcNow - LastUdpSendUtc).TotalMilliseconds;
                if (quietMs < HeartbeatIntervalMs)
                    continue; // inputs are flowing; nothing to do

                UdpSend(new HeartbeatPacket { PlayerId = PlayerId }.ToDatagram());
                Interlocked.Increment(ref HeartbeatsSent);
            }
        }

        /// <summary>Send a heartbeat after this long without sending anything. Well
        /// inside the server's 5 s timeout, so a few lost ones are survivable.</summary>
        private const int HeartbeatIntervalMs = 1000;

        private const int HeartbeatCheckIntervalMs = 200;

        /// <summary>When we last put a packet on the wire — inputs, joins and
        /// heartbeats all count, because the server's timeout counts them all.</summary>
        public DateTime LastUdpSendUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>Shown in the dev HUD: stays at 0 while you are playing and starts
        /// climbing the moment the game goes quiet. That contrast is the point.</summary>
        public int HeartbeatsSent;

        private void UdpSend(byte[] datagram)
        {
            UdpClient? udp = _udp;
            IPEndPoint? server = _serverEndPoint;
            if (udp == null || server == null)
                return;

            // Stamped at the SEND CALL, not after the simulated-latency delay: the
            // heartbeat asks "have I got anything in flight", and a packet sitting in
            // the latency simulator is in flight.
            LastUdpSendUtc = DateTime.UtcNow;

            int delayMs = SimulatedLatencyMs;
            if (delayMs <= 0)
            {
                try { udp.Send(datagram, datagram.Length, server); }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException) { }
                return;
            }

            Task.Delay(delayMs).ContinueWith(_ =>
            {
                try { udp.Send(datagram, datagram.Length, server); }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException) { }
            });
        }

        public void Dispose()
        {
            _running = false;
            try { _tcp?.Close(); } catch { }
            try { _udp?.Close(); } catch { }
        }
    }
}

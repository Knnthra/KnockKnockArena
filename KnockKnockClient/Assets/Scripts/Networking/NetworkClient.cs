using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;

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

        public long TcpMessagesSkipped;

        private TcpClient? tcp;
        private NetworkStream? tcpStream;
        private IPAddress serverAddress = IPAddress.None;
        private volatile bool running = true;

        private void RecieveTcpLoop()
        {
            try
            {
                while (running && TcpFraming.TryReadMessage(tcpStream, out MessageType type, out byte[] payload))
                {
                    switch (type)
                    {
                        case MessageType.LoginResponse:
                            LoginResponse login = LoginResponse.FromPayload(payload);
                            if (login.Success)
                                PlayerId = login.PlayerId;
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
                if (running)
                    TcpMessages.Enqueue(new TcpDisconnectedEvent { Reason = ex.Message });
            }
        }

        public void ConnectAndLogin(string host, int port, string username, string password)
        {
            Thread tcpThread = new Thread(() =>
            {
                try
                {
                    serverAddress = AddressResolver.Resolve(host);
                    tcp = new TcpClient(AddressFamily.InterNetwork);
                    tcp.Connect(serverAddress, port);
                    tcpStream = tcp.GetStream();
                    TcpFraming.WriteMessage(tcpStream, MessageType.LoginRequest, new LoginRequest
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
            NetworkStream? stream = tcpStream;

            if (stream == null)
                return;

            TcpFraming.WriteMessage(stream, MessageType.ChatSend, new ChatSend { Text = text }.ToPayload());


        }

        public void Dispose()
        {
            running = false;
            try { tcp?.Close(); }
            catch { }
        }
    }
}

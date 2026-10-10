using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Net
{
    public sealed class TcpGameServer
    {
        public const string ServerVersion = "0.6.0";

        private readonly ServerConfig config;
        private readonly SessionManager sessions;
        private readonly List<ClientConnection> clients = new();
        private readonly object clientGate = new();
        private readonly DateTime startUtc = DateTime.UtcNow;
        private TcpListener? listner;

        public TimeSpan Uptime => DateTime.UtcNow - startUtc;
        public int PlayersOnline => sessions.Count;

        public TcpGameServer(ServerConfig config, SessionManager sessions)
        {
            this.config = config;
            this.sessions = sessions;
        }

        private void Broadcast(MessageType type, byte[] payload, ClientConnection? except = null)
        {
            ClientConnection[] snapshot;
            lock (clientGate)
                snapshot = clients.ToArray();

            foreach (ClientConnection client in snapshot)
            {
                if (client == except || client.Session == null)
                    continue;
                try
                {
                    client.Send(type, payload);
                }
                catch
                {
                    //Send failed!
                }

            }

        }

        private void Disconnect(ClientConnection connection, string reason)
        {
            lock (clientGate)
            {
                if (!clients.Remove(connection))
                    return;
            }

            connection.Close();

            PlayerSession? session = connection.Session;

            if (session != null)
            {
                sessions.Remove(session.PlayerId);
                Broadcast(MessageType.PlayerLeft, new PlayerLeft
                {
                    PlayerId = session.PlayerId,
                    Username = session.Username,
                }.ToPayload());
                Log.Info($"Player left: {session.Username}#{session.PlayerId} ({reason}) " +
         $"({sessions.Count}/{config.MaxPlayers} online)");
            }
            else
            {
                Log.Info($"Connection closed: {connection.RemoteEndPoint} ({reason})");
            }
        }

        private void HandleLogin(ClientConnection connection, byte[] payload)
        {
            if (payload.Length > 0 && payload[0] != ProtocolConstants.Version)
            {
                string versionError = $"protocol version {payload[0]} not supported, server runs {ProtocolConstants.Version}";
                Log.Warn($"REJECT LoginRequest from {connection.DisplayName}: {versionError}");

                connection.Send(MessageType.LoginResponse, new LoginResponse
                {
                    Success = false,
                    Error = versionError,
                }.ToPayload());
                connection.Close();
                return;

            }

            LoginRequest request = LoginRequest.FromPayload(payload);
            string error;

            if (connection.Session != null)
            {
                error = "aldready logged in";
            }
            else if (request.Username.Length == 0 || request.Password != "test")
            {
                error = "invalid credentials";
            }
            else if (sessions.TryCreate(request.Username, out PlayerSession? session, out error))
            {
                connection.Session = session;
                connection.Send(MessageType.LoginResponse, new LoginResponse
                {
                    Success = true,
                    PlayerId = session!.PlayerId,
                    SessionToken = session.Token,
                }.ToPayload());

                connection.Send(MessageType.ServerInfo, new ServerInfoMessage
                {
                    ServerVersion = ServerVersion,
                    TickRate = GameConstants.TickRate,
                    MaxPlayers = (byte)config.MaxPlayers,
                    PlayersOnline = (byte)sessions.Count,
                    MapName = "",
                    MapHash = 0,
                }.ToPayload());

                Broadcast(MessageType.PlayerJoined, new PlayerJoined
                {
                    PlayerId = session.PlayerId,
                    Username = session.Username,
                }.ToPayload(), except: connection);

                foreach (PlayerSession exsiting in sessions.GetAll())
                {
                    if (exsiting.PlayerId == session.PlayerId)
                        continue;

                    connection.Send(MessageType.PlayerJoined, new PlayerJoined
                    {
                        PlayerId = session.PlayerId,
                        Username = session.Username,
                    }.ToPayload());

                }
                Log.Info($"Login OK: {connection.DisplayName} ({sessions.Count}/{config.MaxPlayers} online)");

                return;

            }

            Log.Warn($"REJECT LoginRequest from {connection.DisplayName}: {error}");
            connection.Send(MessageType.LoginResponse, new LoginResponse
            {
                Success = false,
                Error = error,
            }.ToPayload());

        }

        private void Dispatch(ClientConnection connection, MessageType type, byte[] payload)
        {
            switch (type)
            {
                case MessageType.LoginRequest:
                    HandleLogin(connection, payload);
                    break;
                case MessageType.ChatSend:
                    HandleChat(connection,payload);
                    break;
                default:
                    throw new ProtocolException($"unknown type {(byte)type}");
            }
        }

        private void ClientLoop(ClientConnection connection)
        {
            string disconnectReason = "connection closed";

            try
            {
                while (connection.TryReadMessage(out MessageType type, out byte[] payload))
                {
                    try
                    {
                        Dispatch(connection, type, payload);
                    }
                    catch (ProtocolException ex) when (!ex.IsFatal)
                    {
                        Log.Warn($"REJECT {type} from {connection.DisplayName}: {ex.Reason}");
                    }
                }
            }
            catch (ProtocolException ex)
            {
                Log.Warn($"REJECT from {connection.DisplayName}: {ex.Reason} (dropping connection)");

                disconnectReason = ex.Reason;
            }
            catch (Exception ex)
            {
                disconnectReason = ex.Message;
            }
            finally
            {
                Disconnect(connection, disconnectReason);
            }
        }

        private void AcceptLoop()
        {
            while (true)
            {
                TcpClient tcpClient = listner!.AcceptTcpClient();
                ClientConnection connection = new(tcpClient);

                lock (clientGate)
                    clients.Add(connection);

                Log.Info($"Connection from {connection.RemoteEndPoint}");

                Thread clientThread = new(() => ClientLoop(connection))
                {
                    IsBackground = true,
                    Name = $"tcp-client{connection.RemoteEndPoint}",
                };

                clientThread.Start();
            }
        }

        private void HandleChat(ClientConnection connection, byte[] payload)
        {
            if (connection.Session == null)
                throw new ProtocolException("not authenticated");

            ChatSend chat = ChatSend.FromPayload(payload);
            if (chat.Text.Length == 0)
                throw new ProtocolException("empty chat message");

            Log.Info($"Chat {connection.Session.Username}: {chat.Text}");
            
            Broadcast(MessageType.ChatBroadcast, new ChatBroadcast
            {
                PlayerId = connection.Session.PlayerId,
                Username = connection.Session.Username,
                Text = chat.Text,
            }.ToPayload());
        }

        public void Start()
        {
            listner = new TcpListener(IPAddress.Any, config.Port);
            listner.Start();
            Log.Info($"TCP listening on port {config.Port}");

            Thread acceptThread = new(AcceptLoop) { IsBackground = true, Name = "tcp-accept" };
            acceptThread.Start();

        }
    }
}


using System;
using System.Collections.Generic;
using KnockKnockArena.Shared.Protocol.Messages;
using UnityEngine;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Networking
{
    public sealed class NetworkBootstrap : MonoBehaviour
    {
        [Header("Server")]
        public string Host ="localhost";
        public int Port = 36363;
        public string Username = "test";
        public string Password = "test";

        private NetworkClient _client;

        public event Action<string> ChatReceived;
        public event Action<string> PlayerFeed;

        public string StatusLine { get; private set; } = "";
        public string ServerInfoLine { get; private set; } = "";
        public bool IsConnecting { get; private set; }
        public bool IsLoggedIn => _client != null && !IsConnecting;
        public long TcpMessagesSkipped => _client?.TcpMessagesSkipped ?? 0;
        public bool InGame => _client?.InGame ?? false;
        public long JoinsSent => _client?.JoinsSent ?? 0;
        public int HeartbeatsSent => _client?.HeartbeatsSent ?? 0;

        private readonly Dictionary<byte, string> _usernames = new Dictionary<byte, string>();
        
        private readonly object _lanGate = new object();
        private List<DiscoveredServer> _lanResults = new List<DiscoveredServer>();
        
        private string _lanStatus = "";
        private volatile bool _searchingLan;
        
        public IReadOnlyList<DiscoveredServer> LanServers { get; private set; } = new List<DiscoveredServer>();
        public string LanStatus { get; private set; } = "";
        public bool IsSearchingLan => _searchingLan;
        
        public void FindLanServers()
        {
            int port = Port;
            _searchingLan = true;
            lock (_lanGate)
                _lanStatus = $"Searching the LAN on port {port}...";
            new System.Threading.Thread(() =>
            {
                List<DiscoveredServer> found;
                string status;
                try
                {
                    found = LanDiscovery.Search(port, TimeSpan.FromSeconds(2));
                    status = $"{found.Count} LAN server(s) answered on port {port}";
                }
                catch (Exception ex)
                {
                    found = new List<DiscoveredServer>();
                    status = $"LAN search failed: {ex.Message}";
                }
                lock (_lanGate)
                {
                    _lanResults = found;
                    _lanStatus = status;
                }
                _searchingLan = false;
            })
            {
                IsBackground = true,
                Name = "kka-lan-discovery",
            }.Start();
        }

        public void Connect()
        {
            IsConnecting = true;
            StatusLine = $"Connecting to {Host}:{Port}...";
            _client = new NetworkClient();
            _client.ConnectAndLogin(Host, Port, Username, Password);
        }

        public void Disconnect(string reason)
        {
            _client?.Dispose();
            _client = null;
            IsConnecting = false;
            StatusLine = reason;
            ServerInfoLine = "";
            _usernames.Clear();
        }

        public void SendChat(string text)
        {
            _client?.SendChat(text);
        }

        public void OnDestroy()
        {
            _client?.Dispose();
        }

        private void DrainTcpMessages()
        {
            while (_client != null && _client.TcpMessages.TryDequeue(out object message))
            {
                switch (message)
                {
                    case TcpConnectedEvent _:
                        StatusLine = "Connected, logging in...";
                        break;
                    case LoginResponse login when login.Success:
                        StatusLine = $"Logged in as player #{login.PlayerId}, joining the game over UDP...";
                        IsConnecting = false;
                        _usernames[login.PlayerId] = Username;
                        _client.StartGame();
                        break;

                    case LoginResponse login:
                        Disconnect($"Login failed: {login.Error}");
                        break;

                    case ServerInfoMessage info:
                        ServerInfoLine = $"server v{info.ServerVersion} | tick {info.TickRate} Hz | " +
                                         $"{info.PlayersOnline}/{info.MaxPlayers} online";
                        break;

                    case ChatBroadcast chat:
                        ChatReceived?.Invoke($"[{chat.Username}] {chat.Text}");
                        break;
                    case PlayerJoined joined:
                        _usernames[joined.PlayerId] = joined.Username;
                        PlayerFeed?.Invoke($"{joined.Username} joined");
                        break;

                    case PlayerLeft left:
                        _usernames.Remove(left.PlayerId);
                        PlayerFeed?.Invoke($"{left.Username} left");
                        break;

                    case TcpDisconnectedEvent disconnected:
                        Disconnect($"Disconnected: {disconnected.Reason}");
                        break;
                }
            }
        }
        
        private void Update()
        {
            if(_client != null)
                DrainTcpMessages();
                 
            lock (_lanGate)
            {
                LanServers = _lanResults;
                LanStatus = _lanStatus;
            }
        }
        public void JoinLanServer(DiscoveredServer server)
        {
            Host = server.Address.ToString();
            Port = server.Info.GamePort;
            Connect();
        }

    }
}
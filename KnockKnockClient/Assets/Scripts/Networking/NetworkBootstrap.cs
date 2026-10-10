using System;
using System.Collections.Generic;
using KnockKnockArena.Shared.Protocol.Messages;
using UnityEngine;

namespace KnockKnockArena.Networking
{
    public sealed class NetworkBootstrap : MonoBehaviour
    {
        [Header("Server")]
        public string Host ="localhost";
        public int Port = 36363;
        public string Username = "test";
        public string Password = "test";

        private NetworkClient client;

        public event Action<string> ChatReceived;
        public event Action<string> PlayerFeed;

        public string StatusLine { get; private set; } = "";
        public string ServerInfoLine { get; private set; } = "";
        public bool IsConnecting { get; private set; }
        public bool IsLoggedIn => client != null && !IsConnecting;
        public long TcpMessagesSkipped => client?.TcpMessagesSkipped ?? 0;

        private readonly Dictionary<byte, string> usernames = new Dictionary<byte, string>();

        public void Connect()
        {
            IsConnecting = true;
            StatusLine = $"Connecting to {Host}:{Port}...";
            client = new NetworkClient();
            client.ConnectAndLogin(Host, Port, Username, Password);
        }

        public void Disconnect(string reason)
        {
            client?.Dispose();
            client = null;
            IsConnecting = false;
            StatusLine = reason;
            ServerInfoLine = "";
            usernames.Clear();
        }

        public void SendChat(string text)
        {
            client?.SendChat(text);
        }

        public void OnDestroy()
        {
            client?.Dispose();
        }

        private void DrainTcpMessages()
        {
            while (client != null && client.TcpMessages.TryDequeue(out object message))
            {
                switch (message)
                {
                    case TcpConnectedEvent _:
                        StatusLine = "Connected, logging in...";
                        break;
                    case LoginResponse login when login.Success:
                        StatusLine = $"Logged in as player #{login.PlayerId}";
                        IsConnecting = false;
                        usernames[login.PlayerId] = Username;
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
                        usernames[joined.PlayerId] = joined.Username;
                        PlayerFeed?.Invoke($"{joined.Username} joined");
                        break;

                    case PlayerLeft left:
                        usernames.Remove(left.PlayerId);
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
            if(client != null)
                DrainTcpMessages();
        }

    }
}
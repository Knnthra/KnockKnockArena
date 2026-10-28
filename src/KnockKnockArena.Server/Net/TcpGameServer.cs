using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Net;

/// <summary>
/// The TCP side of the game server: login, chat and important events.
/// One accept loop, one thread per client (the pattern taught in T04).
/// </summary>
public sealed class TcpGameServer
{
    public const string ServerVersion = "0.6.0";

    private readonly ServerConfig _config;
    private readonly SessionManager _sessions;
    private readonly List<ClientConnection> _clients = new();
    private readonly object _clientsGate = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private TcpListener? _listener;

    /// <summary>Raised after a player's session is gone (TCP disconnect or kick);
    /// wired up to despawn the player from the game world.</summary>
    public event Action<PlayerSession>? PlayerDisconnected;

    public TcpGameServer(ServerConfig config, SessionManager sessions)
    {
        _config = config;
        _sessions = sessions;
    }

    public TimeSpan Uptime => DateTime.UtcNow - _startedUtc;
    public int PlayersOnline => _sessions.Count;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _config.Port);
        _listener.Start();
        Log.Info($"TCP listening on port {_config.Port}");

        Thread acceptThread = new(AcceptLoop) { IsBackground = true, Name = "tcp-accept" };
        acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            TcpClient tcpClient = _listener!.AcceptTcpClient();
            ClientConnection connection = new(tcpClient);

            lock (_clientsGate)
                _clients.Add(connection);

            Log.Info($"Connection from {connection.RemoteEndPoint}");

            Thread clientThread = new(() => ClientLoop(connection))
            {
                IsBackground = true,
                Name = $"tcp-client {connection.RemoteEndPoint}",
            };
            clientThread.Start();
        }
    }

    private void ClientLoop(ClientConnection connection)
    {
        string disconnectReason = "connection closed";
        try
        {
            while (connection.TryReadMessage(out MessageType type, out byte[] payload))
            {
                Log.Packet($"RECV {type} ({payload.Length} B) from {connection.DisplayName}");
                try
                {
                    Dispatch(connection, type, payload);
                }
                catch (ProtocolException ex) when (!ex.IsFatal)
                {
                    // Payload-level problem; the framing is intact, so we can keep reading.
                    Log.Warn($"REJECT {type} from {connection.DisplayName}: {ex.Reason}");
                }
            }
        }
        catch (ProtocolException ex)
        {
            // Fatal framing error: the stream is desynced and must be dropped.
            Log.Warn($"REJECT from {connection.DisplayName}: {ex.Reason} (dropping connection)");
            disconnectReason = ex.Reason;
        }
        catch (Exception ex)
        {
            // Socket errors (client vanished, connection reset...) end up here.
            disconnectReason = ex.Message;
        }
        finally
        {
            Disconnect(connection, disconnectReason);
        }
    }

    private void Dispatch(ClientConnection connection, MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.LoginRequest:
                HandleLogin(connection, payload);
                break;
            case MessageType.ChatSend:
                HandleChat(connection, payload);
                break;
            default:
                throw new ProtocolException($"unknown type {(byte)type}");
        }
    }

    private void HandleLogin(ClientConnection connection, byte[] payload)
    {
        // A client on another protocol version must be TOLD, not silently dropped —
        // otherwise it hangs on "connecting" with no clue why, which is exactly what
        // the version byte exists to prevent. Answer in our format, then close.
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
            error = "already logged in";
        }
        else if (request.Username.Length == 0 || request.Password != "test")
        {
            // Module 4: any username, one hardcoded password. Real accounts come in M11.
            error = "invalid credentials";
        }
        else if (_sessions.TryCreate(request.Username, out PlayerSession? session, out error))
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
                MaxPlayers = (byte)_config.MaxPlayers,
                PlayersOnline = (byte)_sessions.Count,
                MapName = ArenaMap.Name,
                MapHash = ArenaMap.Hash,
            }.ToPayload());

            Broadcast(MessageType.PlayerJoined, new PlayerJoined
            {
                PlayerId = session.PlayerId,
                Username = session.Username,
            }.ToPayload(), except: connection);

            // Roster replay: PlayerJoined broadcasts only cover FUTURE joins, but the
            // newcomer needs names for everyone already online (scoreboard, kill
            // feed). Same message type, sent only to the new connection.
            foreach (PlayerSession existing in _sessions.GetAll())
            {
                if (existing.PlayerId == session.PlayerId)
                    continue;
                connection.Send(MessageType.PlayerJoined, new PlayerJoined
                {
                    PlayerId = existing.PlayerId,
                    Username = existing.Username,
                }.ToPayload());
            }

            Log.Info($"Login OK: {connection.DisplayName} ({_sessions.Count}/{_config.MaxPlayers} online)");
            return;
        }

        Log.Warn($"REJECT LoginRequest from {connection.DisplayName}: {error}");
        connection.Send(MessageType.LoginResponse, new LoginResponse
        {
            Success = false,
            Error = error,
        }.ToPayload());
    }

    private void HandleChat(ClientConnection connection, byte[] payload)
    {
        if (connection.Session == null)
            throw new ProtocolException("not authenticated");

        ChatSend chat = ChatSend.FromPayload(payload);
        if (chat.Text.Length == 0)
            throw new ProtocolException("empty chat message");

        Log.Info($"Chat {connection.Session.Username}: {chat.Text}");

        // Echoed to everyone, including the sender — the sender's own line comes
        // from the server, so all clients render chat identically.
        Broadcast(MessageType.ChatBroadcast, new ChatBroadcast
        {
            PlayerId = connection.Session.PlayerId,
            Username = connection.Session.Username,
            Text = chat.Text,
        }.ToPayload());
    }

    /// <summary>Sends a message to every authenticated client (optionally excluding one).</summary>
    private void Broadcast(MessageType type, byte[] payload, ClientConnection? except = null)
    {
        ClientConnection[] snapshot;
        lock (_clientsGate)
            snapshot = _clients.ToArray();

        int sent = 0;
        foreach (ClientConnection client in snapshot)
        {
            if (client == except || client.Session == null)
                continue;
            try
            {
                client.Send(type, payload);
                sent++;
            }
            catch
            {
                // Send failed: the client is gone. Its own read loop will notice
                // and run the disconnect path; nothing more to do here.
            }
        }
        Log.Info($"SEND {type} ({payload.Length} B) to {sent} client{(sent == 1 ? "" : "s")}");
    }

    private void Disconnect(ClientConnection connection, string reason)
    {
        lock (_clientsGate)
        {
            if (!_clients.Remove(connection))
                return; // already disconnected
        }

        connection.Close();

        PlayerSession? session = connection.Session;
        if (session != null)
        {
            _sessions.Remove(session.PlayerId);
            PlayerDisconnected?.Invoke(session);
            Broadcast(MessageType.PlayerLeft, new PlayerLeft
            {
                PlayerId = session.PlayerId,
                Username = session.Username,
            }.ToPayload());
            Log.Info($"Player left: {session.Username}#{session.PlayerId} ({reason}) " +
                     $"({_sessions.Count}/{_config.MaxPlayers} online)");
        }
        else
        {
            Log.Info($"Connection closed: {connection.RemoteEndPoint} ({reason})");
        }
    }

    /// <summary>
    /// Disconnects a player server-side (e.g. on UDP timeout): the normal disconnect
    /// path (session removal, PlayerLeft broadcast, despawn), with the kick's reason.
    /// </summary>
    public void KickByPlayerId(byte playerId, string reason)
    {
        ClientConnection? target = null;
        lock (_clientsGate)
        {
            foreach (ClientConnection client in _clients)
            {
                if (client.Session?.PlayerId == playerId)
                {
                    target = client;
                    break;
                }
            }
        }

        if (target != null)
        {
            Log.Info($"Kicking {target.DisplayName}: {reason}");
            // Disconnect here, with the real reason: the client's read loop fails when the
            // socket closes and runs Disconnect too, but by then the connection is gone
            // from the list and nothing happens twice - the log says "udp timeout", not
            // the socket exception the read loop sees.
            Disconnect(target, reason);
        }
    }

    /// <summary>Kill feed: broadcast a PlayerDied event to every logged-in client.</summary>
    public void BroadcastPlayerDied(byte killerId, string killerName, byte victimId, string victimName, byte weaponId)
    {
        Broadcast(MessageType.PlayerDied, new PlayerDied
        {
            KillerId = killerId,
            KillerUsername = killerName,
            VictimId = victimId,
            VictimUsername = victimName,
            WeaponId = weaponId,
        }.ToPayload());
    }

    /// <summary>Broadcast a PickupTaken event to every logged-in client.</summary>
    public void BroadcastPickupTaken(byte playerId, string username, byte pickupId, byte pickupType)
    {
        Broadcast(MessageType.PickupTaken, new PickupTaken
        {
            PlayerId = playerId,
            Username = username,
            PickupId = pickupId,
            Type = (Shared.Protocol.Udp.PickupType)pickupType,
        }.ToPayload());
    }

    /// <summary>Broadcast a RocketExploded event to every logged-in client.</summary>
    public void BroadcastRocketExploded(ushort rocketEntityId, float x, float z, byte ownerId)
    {
        Broadcast(MessageType.RocketExploded, new RocketExploded
        {
            RocketEntityId = rocketEntityId,
            X = x,
            Z = z,
            OwnerId = ownerId,
        }.ToPayload());
    }

    /// <summary>
    /// Announces damage and WHERE it landed. Broadcast, not sent to the victim
    /// alone: everyone watching a fight should see the blood, not just the person
    /// bleeding.
    /// </summary>
    public void BroadcastPlayerDamaged(byte victimId, float x, float z,
        byte attackerId, byte weaponId, byte damage)
    {
        Broadcast(MessageType.PlayerDamaged, new PlayerDamaged
        {
            VictimId = victimId,
            X = x,
            Z = z,
            AttackerId = attackerId,
            WeaponId = weaponId,
            Damage = damage,
        }.ToPayload());
    }
}

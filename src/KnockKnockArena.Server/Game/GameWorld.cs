using System.Collections.Concurrent;
using System.Net;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Shared.Simulation;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Server.Game
{
    public sealed class GameWorld
    {
        private readonly object _gate = new();
        private readonly Dictionary<byte, PlayerState> _playersById = new();
        private readonly ConcurrentDictionary<IPEndPoint, PlayerState> _playersByEndPoint = new();
        private readonly List<PlayerState> _orphaned = new();

        private const int MaxInputsPerTick = 10;
        private const int MaxQueuedInputs = 64;

        public uint Sequence { get; private set; }

        public uint ServerTick { get; private set; }

        public int PlayerCount
        {
            get { lock (_gate) return _playersById.Count; }
        }

        public bool TryGetByEndPoint(IPEndPoint endPoint, out PlayerState player) => _playersByEndPoint.TryGetValue(endPoint, out player!);

        public bool SpawnOrRebind(PlayerSession session, IPEndPoint endPoint, out PlayerState player)
        {
            lock (_gate)
            {
                if (_playersById.TryGetValue(session.PlayerId, out PlayerState? existing))
                {
                    _playersByEndPoint.TryRemove(existing.EndPoint, out _);
                    existing.EndPoint = endPoint;
                    existing.LastUdpPacketUtc = DateTime.UtcNow;
                    existing.TimedOut = false;
                    _playersByEndPoint[endPoint] = existing;
                    player = existing;
                    return false;
                }

                (float spawnX, float spawnZ) = ArenaMap.SpawnPoints[(session.PlayerId - 1) % ArenaMap.SpawnPoints.Length];
                player = new PlayerState(session, endPoint)
                {
                    X = spawnX,
                    Z = spawnZ,
                };
                _playersById[session.PlayerId] = player;
                _playersByEndPoint[endPoint] = player;
                return true;
            }
        }

        public bool TryDespawn(byte playerId)
        {
            lock (_gate)
            {
                if (!_playersById.Remove(playerId, out PlayerState? player))
                    return false;

                _playersByEndPoint.TryRemove(player.EndPoint, out _);
                return true;
            }
        }

        public bool TryEnqueueInput(PlayerState player, InputPacket input)
        {
            if (player.PendingInputs.Count >= MaxQueuedInputs)
                return false;
            player.PendingInputs.Enqueue(input);
            return true;
        }

        public void Tick(List<(IPEndPoint EndPoint, byte[] Datagram)> snapshotSends, List<PlayerState> timedOut)
        {
            lock (_gate)
            {
                ServerTick++;
                Sequence++;
                DateTime now = DateTime.UtcNow;

                foreach (PlayerState player in _playersById.Values)
                {   
                    for (int i = 0; i < MaxInputsPerTick && player.PendingInputs.TryDequeue(out InputPacket? input); i++)
                    {
                        player.LastProcessedInputIndex = input.InputIndex;
                        float x = player.X;
                        float z = player.Z;
                        MovementSimulation.DashState dash = player.Dash;
                        MovementSimulation.Step(ref x, ref z, ref dash, input.Movement,
                            input.AimX, input.AimZ, GameConstants.TickDeltaTime);
                        player.X = x;
                        player.Z = z;
                        player.Dash = dash;
                        player.AimX = input.AimX;
                        player.AimZ = input.AimZ;
                        
                    }
                    
                    double silentSeconds = (now - player.LastUdpPacketUtc).TotalSeconds;

                    if (!player.TimedOut && silentSeconds > GameConstants.UdpTimeoutSeconds)
                    {
                        player.TimedOut = true;
                        timedOut.Add(player);
                    }
                    else if (player.TimedOut && silentSeconds > GameConstants.UdpTimeoutSeconds * 3)
                    {
                        _orphaned.Add(player);
                    }
                }


                foreach (PlayerState player in _orphaned)
                {
                    Log.Warn($"Removing {player.Session.Username}#{player.Session.PlayerId}: timed out and no TCP disconnect followed");

                    _playersById.Remove(player.Session.PlayerId);
                    _playersByEndPoint.TryRemove(player.EndPoint, out _);
                }
                _orphaned.Clear();
                
                if (_playersById.Count == 0)
                    return;
                
                List<PlayerEntityState> entities = new(_playersById.Count);
                
                foreach (PlayerState player in _playersById.Values)
                {
                    entities.Add(new PlayerEntityState
                    {
                        EntityId = player.Session.PlayerId,
                        X = player.X,
                        Z = player.Z,
                        AimX = player.AimX,
                        AimZ = player.AimZ,
                        Health = GameConstants.MaxHealth,
                        State = PlayerLifeState.Alive,
                        DashTicksLeft = player.Dash.TicksLeft,
                        DashCooldownTicks = player.Dash.CooldownTicks,
                        DashDirectionX = player.Dash.DirectionX,
                        DashDirectionZ = player.Dash.DirectionZ,
                    });
                }
                foreach (PlayerState recipient in _playersById.Values)
                {
                    SnapshotPacket snapshot = new()
                    {
                        Sequence = Sequence,
                        ServerTick = ServerTick,
                        LastProcessedInputIndex = recipient.LastProcessedInputIndex,
                        Players = entities,
                    };
                    snapshotSends.Add((recipient.EndPoint, snapshot.ToDatagram()));
                }
                
            }
        }



    }
}
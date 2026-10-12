using System.Collections.Concurrent;
using System.Net;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Game
{
    public sealed class GameWorld
    {
        private readonly object _gate = new();
        private readonly Dictionary<byte, PlayerState> _playersById = new();
        private readonly ConcurrentDictionary<IPEndPoint, PlayerState> _playersByEndPoint = new();
        private readonly List<PlayerState> _orphaned = new();

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

                player = new PlayerState(session, endPoint);
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
        
        public void Tick(List<PlayerState> timedOut)
        {
            lock(_gate)
            {
                ServerTick++;
                DateTime now = DateTime.UtcNow;
                
                foreach(PlayerState player in _playersById.Values)
                {
                    double silentSeconds = (now - player.LastUdpPacketUtc).TotalSeconds;
                    
                    if(!player.TimedOut && silentSeconds > GameConstants.UdpTimeoutSeconds)
                    {
                        player.TimedOut = true;
                        timedOut.Add(player);
                    }
                    else if(player.TimedOut && silentSeconds > GameConstants.UdpTimeoutSeconds * 3)
                    {
                        _orphaned.Add(player);
                    }
                }
                
                
                foreach(PlayerState player in _orphaned)
                {
                     Log.Warn($"Removing {player.Session.Username}#{player.Session.PlayerId}: timed out and no TCP disconnect followed");
                     
                     _playersById.Remove(player.Session.PlayerId);
                     _playersByEndPoint.TryRemove(player.EndPoint, out _);
                }
                _orphaned.Clear();
            }
        }



    }
}
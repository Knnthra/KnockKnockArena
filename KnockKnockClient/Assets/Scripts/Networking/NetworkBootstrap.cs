using System;
using System.Collections.Generic;
using KnockKnockArena.Gameplay;
using KnockKnockArena.Shared.Protocol.Messages;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;
using UnityEngine;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// THE coupling point (HANDOVER 6.1): the single scene object that owns the
    /// network connection. Everything network-related funnels through here — the
    /// student base project is produced by deleting the Networking folder and this
    /// object from the scene.
    ///
    /// MODULE 6, LOCKSTEP: inputs are sampled at the tick rate and sent, and every
    /// player — you included — is drawn where the newest snapshot says. Nothing moves
    /// until the server has answered: you feel the whole round trip. The dev UI is
    /// NetworkHud (handed out): it only reads the public properties below, listens to
    /// the two events and calls the public methods. Nothing here draws anything.
    /// </summary>
    public sealed class NetworkBootstrap : MonoBehaviour
    {
        [Header("Server")]
        public string Host = "localhost";
        public int Port = 36363;
        public string Username = "test";
        public string Password = "test";

        [Header("Debug")]
        [Tooltip("Delay on all UDP traffic, each way, in ms. 2x this = simulated round trip.")]
        public int SimulatedLatencyMs = 0;

        [Header("Gameplay couplings")]
        public CursorAim AimSource;

        [Tooltip("Prefab used for every player view. Its pivot must be at the FEET. " +
                 "Empty = primitive cube placeholders.")]
        public GameObject PlayerViewPrefab;

        [Tooltip("Invoked with the local player's transform when it spawns, and with " +
                 "null on disconnect. Wire your camera's SetTarget(Transform) here.")]
        public UnityEngine.Events.UnityEvent<Transform> LocalPlayerSpawned =
            new UnityEngine.Events.UnityEvent<Transform>();

        // The view LocalPlayerSpawned last fired for, so it fires once per spawn
        // (snapshots arrive 30 times a second; listeners must not).
        private Transform _announcedLocalView;

        /// <summary>Entity render height: cube center sits half the player height
        /// above the playable floor. The server knows no Y (pure XZ simulation).</summary>
        private static float EntityY => ArenaMap.GroundY + 1f;

        private float PlayerY => PlayerViewPrefab != null ? ArenaMap.GroundY : EntityY;

        // What the HUD shows. Only this class sets them.
        public string StatusLine { get; private set; } = "";
        public string ServerInfoLine { get; private set; } = "";
        public bool IsConnecting { get; private set; }
        public bool IsLoggedIn => _client != null && !IsConnecting;
        public long TcpMessagesSkipped => _client?.TcpMessagesSkipped ?? 0;
        public bool InGame => _client?.InGame ?? false;
        public long JoinsSent => _client?.JoinsSent ?? 0;
        public long SnapshotsAccepted => _client?.SnapshotsAccepted ?? 0;
        public uint InputsSent => _client?.InputIndex ?? 0;
        public int HeartbeatsSent => _client?.HeartbeatsSent ?? 0;

        // A chat line and a joined/left line, ready to show.
        public event Action<string> ChatReceived;
        public event Action<string> PlayerFeed;

        // Set by the HUD while its chat line is open: the keys then type text and must
        // not move the player.
        public bool IsTyping { get; set; }

        private NetworkClient _client;

        // Player id -> username, built from PlayerJoined events. The server replays
        // the roster on login, so players who were already online are covered too.
        private readonly Dictionary<byte, string> _usernames = new Dictionary<byte, string>();

        // Entity views, keyed by entity id (= player id), and their CharacterVisual
        // (null for the cube placeholder).
        private readonly Dictionary<ushort, GameObject> _playerViews = new Dictionary<ushort, GameObject>();
        private readonly Dictionary<ushort, CharacterVisual> _characterVisuals = new Dictionary<ushort, CharacterVisual>();

        // Input sampling runs at the tick rate, not the frame rate.
        private float _inputAccumulator;

        // Space is edge-triggered (dash): captured per frame, consumed by ONE input tick.
        private bool _dashQueued;

        private void Update()
        {
            // The search thread's result, copied on the main thread for the HUD.
            lock (_lanGate)
            {
                LanServers = _lanResults;
                LanStatus = _lanStatus;
            }

            if (_client == null)
                return;

            DrainTcpMessages();
            if (_client == null)
                return; // the queue held a disconnect

            // The latency simulator is switchable WHILE PLAYING (the HUD has a slider).
            // It is pushed into the client every frame because the client reads it per
            // packet — no reconnect needed.
            _client.SimulatedLatencyMs = SimulatedLatencyMs;

            if (Input.GetKeyDown(KeyCode.Space) && !IsTyping)
                _dashQueued = true;

            if (_client.TryTakeSnapshot(out SnapshotPacket snapshot))
                ApplySnapshot(snapshot);

            SampleAndSendInputs();
        }

        // ------------------------------------------------------------- connection

        public void Connect()
        {
            IsConnecting = true;
            StatusLine = $"Connecting to {Host}:{Port}...";
            _client = new NetworkClient { SimulatedLatencyMs = SimulatedLatencyMs };
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

            _inputAccumulator = 0f;

            foreach (GameObject view in _playerViews.Values)
                Destroy(view);
            _playerViews.Clear();
            _characterVisuals.Clear();
            if (_announcedLocalView != null)
            {
                _announcedLocalView = null;
                LocalPlayerSpawned.Invoke(null);
            }
        }

        public void SendChat(string text)
        {
            _client?.SendChat(text);
        }

        private void OnDestroy()
        {
            _client?.Dispose();
        }

        // ------------------------------------------------------------- finding servers

        private readonly object _lanGate = new object();
        private List<DiscoveredServer> _lanResults = new List<DiscoveredServer>();
        private string _lanStatus = "";
        private volatile bool _searchingLan;

        // What the HUD shows of the search. Only this class sets them.
        public IReadOnlyList<DiscoveredServer> LanServers { get; private set; } = new List<DiscoveredServer>();
        public string LanStatus { get; private set; } = "";
        public bool IsSearchingLan => _searchingLan;

        /// <summary>
        /// LAN discovery (M05): broadcast a DiscoveryRequest on the game port and collect
        /// answers for 2 s. The search blocks, so it runs on a background thread that only
        /// fills a list — the same rule as the rest of the netcode: that thread never
        /// touches Unity, the main thread never touches the socket.
        /// </summary>
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

        public void JoinLanServer(DiscoveredServer server)
        {
            Host = server.Address.ToString();
            Port = server.Info.GamePort;
            Connect();
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
                                         $"{info.PlayersOnline}/{info.MaxPlayers} online | " +
                                         $"map {info.MapName} {ArenaMapData.FormatHash(info.MapHash)}";
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

        // ------------------------------------------------------------- snapshots

        private void ApplySnapshot(SnapshotPacket snapshot)
        {
            List<ushort> liveIds = new List<ushort>();
            foreach (PlayerEntityState player in snapshot.Players)
            {
                liveIds.Add(player.EntityId);
                if (!_playerViews.TryGetValue(player.EntityId, out GameObject view))
                {
                    if (PlayerViewPrefab != null)
                    {
                        view = Instantiate(PlayerViewPrefab);
                    }
                    else
                    {
                        view = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        view.transform.localScale = new Vector3(1f, 2f, 1f);
                        Destroy(view.GetComponent<Collider>()); // visuals only; the server owns physics
                    }
                    view.name = $"Player {player.EntityId}";
                    _playerViews[player.EntityId] = view;
                    _characterVisuals[player.EntityId] = view.GetComponent<CharacterVisual>();
                }

                // Lockstep: everyone, you included, stands where the server says and
                // faces the aim point the snapshot carries.
                view.transform.position = new Vector3(player.X, PlayerY, player.Z);
                SetAim(player.EntityId, player.AimX, player.AimZ);

                if (player.EntityId == _client.PlayerId && _announcedLocalView != view.transform)
                {
                    _announcedLocalView = view.transform;
                    LocalPlayerSpawned.Invoke(view.transform);
                }
            }

            List<ushort> stale = new List<ushort>();
            foreach (ushort id in _playerViews.Keys)
                if (!liveIds.Contains(id))
                    stale.Add(id);
            foreach (ushort id in stale)
            {
                Destroy(_playerViews[id]);
                _playerViews.Remove(id);
                _characterVisuals.Remove(id);
            }
        }

        /// <summary>Turns a player's figure toward an aim point. W moves toward the aim,
        /// so the figure's facing is the movement rule made visible.</summary>
        private void SetAim(ushort playerId, float aimX, float aimZ)
        {
            if (_characterVisuals.TryGetValue(playerId, out CharacterVisual visual) && visual != null)
                visual.SetAimPoint(new Vector3(aimX, PlayerY, aimZ));
        }

        // ------------------------------------------------------------- input

        // SampleAndSendInputs skrives her i videoen (Modul 6 kort version, implementering 4)
        private void SampleAndSendInputs()
        {
            if (_client == null || !_client.InGame)
                return;
            _inputAccumulator += Time.deltaTime;
            while (_inputAccumulator >= GameConstants.TickDeltaTime)
            {
                _inputAccumulator -= GameConstants.TickDeltaTime;
                if (IsTyping)
                {
                    _dashQueued = false;
                    continue;
                }
                MovementBits movement = MovementBits.None;
                if (Input.GetKey(KeyCode.W)) movement |= MovementBits.W;
                if (Input.GetKey(KeyCode.A)) movement |= MovementBits.A;
                if (Input.GetKey(KeyCode.S)) movement |= MovementBits.S;
                if (Input.GetKey(KeyCode.D)) movement |= MovementBits.D;
                if (Input.GetKey(KeyCode.LeftControl)) movement |= MovementBits.Walk;
                if (Input.GetKey(KeyCode.LeftShift)) movement |= MovementBits.Sprint;
                if (_dashQueued) movement |= MovementBits.Dash;
                _dashQueued = false;
                Vector3 aim = AimSource != null && AimSource.HasAim
                    ? AimSource.AimPoint
                    : Vector3.zero;
                    _client.SendInput(movement, aim.x, aim.z, ButtonBits.None, 0);
            }
        }
    }
}

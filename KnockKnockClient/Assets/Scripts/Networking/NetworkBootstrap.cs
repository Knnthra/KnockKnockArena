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
    /// MODULE 7: entities and interpolation. The local player is predicted and
    /// reconciled as in M06. Other players render a few ticks in the past, blended
    /// between buffered snapshots in position AND aim ("you see yourself in the
    /// present — everyone else in the past"). Pickups are entities: the scene's own
    /// pickup objects are shown and hidden by the snapshot's Active flag. The dev UI is
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

        [Tooltip("Random ± variation on the simulated latency, per datagram, in ms.")]
        public int SimulatedJitterMs = 0;

        [Tooltip("Simulate your own movement immediately instead of waiting for the server.")]
        public bool EnablePrediction = true;

        [Tooltip("Replay unacknowledged inputs on top of each snapshot. Off = snap " +
                 "straight to the server's position: you trail a round trip behind " +
                 "and hop back about 0.2 m - it feels like lockstep.")]
        public bool EnableReconciliation = true;

        [Tooltip("Render other players a few ticks in the past, blended between " +
                 "snapshots. Off = jump to the newest snapshot (visible teleporting).")]
        public bool EnableInterpolation = true;

        [Header("Gameplay couplings")]
        public CursorAim AimSource;

        [Tooltip("Prefab used for every player view. Its pivot must be at the FEET. " +
                 "Empty = primitive cube placeholders.")]
        public GameObject PlayerViewPrefab;

        [Tooltip("Invoked with the local player's transform when it spawns, and with " +
                 "null on disconnect. Wire your camera's SetTarget(Transform) here.")]
        public UnityEngine.Events.UnityEvent<Transform> LocalPlayerSpawned =
            new UnityEngine.Events.UnityEvent<Transform>();

        [Tooltip("Dash ability icon with the radial cooldown sweep. Optional.")]
        public DashCooldownIcon DashCooldownIcon;

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
        public long SnapshotsDropped => _client?.SnapshotsDropped ?? 0;
        public uint InputsSent => _client?.InputIndex ?? 0;
        public int HeartbeatsSent => _client?.HeartbeatsSent ?? 0;
        public int UnackedInputs => _pendingInputs.Count;
        public long Reconciliations { get; private set; }
        public long BitMismatches { get; private set; }
        public string FirstMismatch { get; private set; } = "";

        // A chat line, and a feed line (joined, left, took a pickup), ready to show.
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
        private readonly Dictionary<ushort, byte> _remoteDashTicks = new Dictionary<ushort, byte>();

        // Entity interpolation: remote players render on the server-tick timeline,
        // InterpolationDelayTicks behind the newest snapshot, blended between
        // buffered samples. "You see everyone else in the past."
        private const float InterpolationDelayTicks = 3f; // 100 ms of jitter headroom
        private readonly Dictionary<ushort, InterpolationBuffer> _remoteBuffers = new Dictionary<ushort, InterpolationBuffer>();
        private double _renderTick;
        private bool _renderTickInitialized;
        private uint _latestSnapshotTick;

        // Pickups: the scene's own pickup objects, in the map's pickup order.
        private readonly List<GameObject> _pickupViews = new List<GameObject>();
        private bool _pickupViewsCreated;

        // Input sampling runs at the tick rate, not the frame rate.
        private float _inputAccumulator;

        // Space is edge-triggered (dash): captured per frame, consumed by ONE input tick.
        private bool _dashQueued;
        private MovementBits _lastSampledMovement;

        // Client-side prediction: the local player's own simulation.
        private bool _predictionInitialized;
        private float _predictedX;
        private float _predictedZ;
        private MovementSimulation.DashState _predictedDash;

        // Render interpolation: the simulation steps in fixed 1/30 s ticks, but the
        // screen runs at frame rate — so the view blends between the previous and
        // current predicted position by the accumulator fraction.
        private float _prevPredictedX;
        private float _prevPredictedZ;

        // Reconciliation: every input the server has not acknowledged yet. They carry
        // the aim too: movement is character-relative, so replay needs the exact aim.
        private readonly List<(uint Index, MovementBits Movement, float AimX, float AimZ)> _pendingInputs =
            new List<(uint, MovementBits, float, float)>();

        private static MovementSimulation.DashState DashOf(PlayerEntityState player) =>
            new MovementSimulation.DashState
            {
                TicksLeft = player.DashTicksLeft,
                CooldownTicks = player.DashCooldownTicks,
                DirectionX = player.DashDirectionX,
                DirectionZ = player.DashDirectionZ,
            };

        private void Start()
        {
            // The scene's ArenaMapStamp loaded the arena in Awake: bind its pickups now,
            // so they are there before anyone connects.
            if (ArenaMap.IsLoaded && !_pickupViewsCreated)
                CreatePickupViews();
        }

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

            // The latency simulator is switchable WHILE PLAYING (the HUD has a slider
            // for latency and one for jitter). Both are pushed into the client every
            // frame because the client reads them per packet — no reconnect needed.
            _client.SimulatedLatencyMs = SimulatedLatencyMs;
            _client.SimulatedJitterMs = SimulatedJitterMs;

            if (Input.GetKeyDown(KeyCode.Space) && !IsTyping)
                _dashQueued = true;

            if (_client.TryTakeSnapshot(out SnapshotPacket snapshot))
                ApplySnapshot(snapshot);

            UpdateRemoteViews();
            SampleAndSendInputs();
            UpdateDashIcon();
        }

        // ------------------------------------------------------------- connection

        public void Connect()
        {
            IsConnecting = true;
            StatusLine = $"Connecting to {Host}:{Port}...";
            _client = new NetworkClient { SimulatedLatencyMs = SimulatedLatencyMs, SimulatedJitterMs = SimulatedJitterMs };
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
            _predictionInitialized = false;
            _pendingInputs.Clear();
            Reconciliations = 0;
            BitMismatches = 0;
            FirstMismatch = "";

            foreach (GameObject view in _playerViews.Values)
                Destroy(view);
            _playerViews.Clear();
            _characterVisuals.Clear();
            _remoteDashTicks.Clear();
            _remoteBuffers.Clear();
            _renderTickInitialized = false;

            // The scene's pickups stay in the scene; offline, they are all there.
            foreach (GameObject pickup in _pickupViews)
                pickup.SetActive(true);
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

                    case PickupTaken taken:
                        PlayerFeed?.Invoke($"{taken.Username} took {taken.Type}");
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

                if (player.EntityId == _client.PlayerId)
                {
                    Reconcile(player, snapshot.LastProcessedInputIndex);
                    // The own view renders at the PREDICTED position (per frame, in
                    // SampleAndSendInputs).
                    view.transform.position = new Vector3(_predictedX, PlayerY, _predictedZ);

                    if (_announcedLocalView != view.transform)
                    {
                        _announcedLocalView = view.transform;
                        LocalPlayerSpawned.Invoke(view.transform);
                    }
                }
                else
                {
                    // Other players: position AND aim go into the buffer; both are
                    // rendered in the past, blended between snapshots (UpdateRemoteViews).
                    if (!_remoteBuffers.TryGetValue(player.EntityId, out InterpolationBuffer buffer))
                        _remoteBuffers[player.EntityId] = buffer = new InterpolationBuffer();
                    buffer.Add(snapshot.ServerTick, player.X, player.Z, player.AimX, player.AimZ);
                    
                    _remoteDashTicks[player.EntityId] = player.DashTicksLeft;
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
                _remoteBuffers.Remove(id);
                _remoteDashTicks.Remove(id);
            }

            // Steer the render clock: it should sit InterpolationDelayTicks behind
            // the newest snapshot.
            _latestSnapshotTick = snapshot.ServerTick;
            
            if (!_renderTickInitialized)
            {
                _renderTick = snapshot.ServerTick - InterpolationDelayTicks;
                _renderTickInitialized = true;
            }

            // Pickups: the server says which are there to take.
            for (int i = 0; i < snapshot.Pickups.Count && i < _pickupViews.Count; i++)
                _pickupViews[i].SetActive(snapshot.Pickups[i].Active);
        }

        /// <summary>
        /// Advances the render clock at tick rate and positions every remote view by
        /// sampling its buffer at that (past) moment. The clock is pulled gently
        /// toward its target so clock drift and jitter never cause visible jumps;
        /// only a grossly wrong clock (join, long stall) snaps.
        /// </summary>
        private void UpdateRemoteViews()
        {
            if (!_renderTickInitialized)
                return;

            _renderTick += GameConstants.TickRate * (double)Time.deltaTime;

            double target = _latestSnapshotTick - InterpolationDelayTicks;
            double error = target - _renderTick;
            if (error > 10 || error < -10)
                _renderTick = target;
            else
                _renderTick += error * 0.05;

            foreach (KeyValuePair<ushort, GameObject> entry in _playerViews)
            {
                if (entry.Key == _client.PlayerId)
                    continue;
                if (!_remoteBuffers.TryGetValue(entry.Key, out InterpolationBuffer buffer))
                    continue;

                // Interpolation OFF renders the newest snapshot raw: 30 Hz teleporting
                // (and snapping to each new facing).
                float x, z, aimX, aimZ;
                bool has = EnableInterpolation
                    ? buffer.TrySample(_renderTick, out x, out z, out aimX, out aimZ)
                    : buffer.TryLatest(out x, out z, out aimX, out aimZ);
                if (!has)
                    continue;

                Vector3 previousPosition = entry.Value.transform.position;
                entry.Value.transform.position = new Vector3(x, PlayerY, z);

                // Remote cosmetics: motion inferred from how fast the view is actually
                // gliding (sprint reads as >7.5 m/s — between run 6 and sprint 9); dash
                // comes from the snapshot's authoritative state; the aim is blended.
                if (_characterVisuals.TryGetValue(entry.Key, out CharacterVisual visual) && visual != null)
                {
                    float speed = Time.deltaTime > 0f
                        ? (entry.Value.transform.position - previousPosition).magnitude / Time.deltaTime
                        : 0f;
                    bool dashing = _remoteDashTicks.TryGetValue(entry.Key, out byte dashTicks) && dashTicks > 0;
                    visual.SetMotionState(speed > 0.5f, speed > 7.5f, dashing,
                        GameConstants.DashDurationTicks * GameConstants.TickDeltaTime);
                    visual.SetAimPoint(new Vector3(aimX, PlayerY, aimZ));
                }
            }
        }

        /// <summary>
        /// Binds each pickup in the map data to the object already standing in the
        /// scene at that spot — the one the level author placed (its PickupMarker).
        /// Nothing is spawned: the netcode's job is to show and hide what is already
        /// there, following the server's Active flag. A pickup with no object in the
        /// scene falls back to a coloured cube so the spot is not invisible.
        /// </summary>
        private void CreatePickupViews()
        {
            _pickupViewsCreated = true;
            PickupMarker[] markers = FindObjectsByType<PickupMarker>(FindObjectsInactive.Include);

            for (int i = 0; i < ArenaMap.Pickups.Length; i++)
            {
                (PickupType type, float x, float z) = ArenaMap.Pickups[i];

                PickupMarker match = null;
                float bestDistance = float.MaxValue;
                foreach (PickupMarker marker in markers)
                {
                    if (marker.Type != type)
                        continue;
                    float dx = marker.transform.position.x - x;
                    float dz = marker.transform.position.z - z;
                    float distance = dx * dx + dz * dz;
                    if (distance < bestDistance) { bestDistance = distance; match = marker; }
                }

                GameObject view;
                if (match != null && bestDistance < 1f)
                {
                    view = match.gameObject;
                }
                else
                {
                    view = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    view.transform.position = new Vector3(x, ArenaMap.GroundY + 0.35f, z);
                    view.transform.localScale = Vector3.one * 0.7f;
                    Destroy(view.GetComponent<Collider>());
                    view.GetComponent<Renderer>().material.color = new Color(1f, 0f, 1f);
                    view.name = $"Pickup {i} ({type}) — MISSING in scene";
                    Debug.LogWarning($"[Arena] Pickup {i} ({type}) is in the map data but has no " +
                                     $"marker in the scene at ({x}, {z}) — re-export after moving it.");
                }
                _pickupViews.Add(view);
            }
        }

        /// <summary>
        /// Drives the dash icon off the PREDICTED dash state, the same state the movement
        /// prediction runs on, so the sweep starts on the keypress. The cooldown counts from
        /// dash START (the rule in MovementSimulation), so the sweep includes the dash itself.
        /// </summary>
        private void UpdateDashIcon()
        {
            if (DashCooldownIcon == null)
                return;

            bool show = _client != null && _client.InGame && _predictionInitialized;
            DashCooldownIcon.gameObject.SetActive(show);
            if (show)
                DashCooldownIcon.Set(_predictedDash.CooldownTicks, GameConstants.DashCooldownTicks,
                    GameConstants.TickRate);
        }

        /// <summary>Turns a player's figure toward an aim point. W moves toward the aim,
        /// so the figure's facing is the movement rule made visible.</summary>
        private void SetAim(ushort playerId, float aimX, float aimZ)
        {
            if (_characterVisuals.TryGetValue(playerId, out CharacterVisual visual) && visual != null)
                visual.SetAimPoint(new Vector3(aimX, PlayerY, aimZ));
        }

        /// <summary>
        /// Server reconciliation: start from the authoritative position, drop
        /// acknowledged inputs, replay the rest with the shared simulation, and
        /// compare the result bit for bit with the running prediction.
        /// </summary>
        private void Reconcile(PlayerEntityState me, uint lastProcessedInputIndex)
        {
            if (!_predictionInitialized)
            {
                _predictedX = me.X;
                _predictedZ = me.Z;
                _prevPredictedX = me.X;
                _prevPredictedZ = me.Z;
                _predictedDash = DashOf(me);
                _predictionInitialized = true;
                return;
            }

            // Prediction or reconciliation switched off in the HUD: take the server's
            // position as-is and throw the unacknowledged inputs away. With prediction
            // off that is simply "no local simulation" — you watch yourself move a
            // round trip late. With prediction still ON you run one tick ahead, and
            // every snapshot pulls you back to where the server had you a round trip
            // ago: it feels like lockstep, with small hops, because nothing replays
            // the inputs the server has not seen yet.
            if (!EnablePrediction || !EnableReconciliation)
            {
                _pendingInputs.Clear();
                _predictedX = me.X;
                _predictedZ = me.Z;
                _prevPredictedX = me.X;
                _prevPredictedZ = me.Z;
                _predictedDash = DashOf(me);
                return;
            }

            _pendingInputs.RemoveAll(input => input.Index <= lastProcessedInputIndex);

            float replayedX = me.X;
            float replayedZ = me.Z;
            MovementSimulation.DashState replayedDash = DashOf(me);
            foreach ((uint _, MovementBits movement, float aimX, float aimZ) in _pendingInputs)
                MovementSimulation.Step(ref replayedX, ref replayedZ, ref replayedDash,
                    movement, aimX, aimZ, GameConstants.TickDeltaTime);

            Reconciliations++;
            bool identical =
                BitConverter.SingleToInt32Bits(replayedX) == BitConverter.SingleToInt32Bits(_predictedX) &&
                BitConverter.SingleToInt32Bits(replayedZ) == BitConverter.SingleToInt32Bits(_predictedZ);
            if (!identical)
            {
                BitMismatches++;
                if (FirstMismatch.Length == 0)
                    FirstMismatch = $"predicted=({_predictedX:R}, {_predictedZ:R}) reconciled=({replayedX:R}, {replayedZ:R})";
                // A genuine correction is a snap, not something to smear over frames.
                _prevPredictedX = replayedX;
                _prevPredictedZ = replayedZ;
            }

            _predictedX = replayedX;
            _predictedZ = replayedZ;
            _predictedDash = replayedDash;
        }

        // ------------------------------------------------------------- input

        private void SampleAndSendInputs()
        {
            if (_client == null || !_client.InGame)
                return;

            // One input per server tick (30 Hz), regardless of frame rate — sampled
            // with an accumulator. Every input also resets the heartbeat timer, so
            // while you are playing the heartbeat sends nothing at all.
            _inputAccumulator += Time.deltaTime;
            while (_inputAccumulator >= GameConstants.TickDeltaTime)
            {
                _inputAccumulator -= GameConstants.TickDeltaTime;

                // While the chat field has keyboard focus the player stands still and
                // sends nothing: the heartbeat keeps the session alive.
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
                _dashQueued = false; // consumed by exactly one input tick
                _lastSampledMovement = movement;

                Vector3 aim = AimSource != null && AimSource.HasAim
                    ? AimSource.AimPoint
                    : Vector3.zero;

                uint inputIndex = _client.SendInput(movement, aim.x, aim.z, ButtonBits.None, 0);

                // Client-side prediction: apply the input locally NOW — zero perceived
                // latency — and remember it for reconciliation replay.
                if (_predictionInitialized && EnablePrediction)
                {
                    _prevPredictedX = _predictedX;
                    _prevPredictedZ = _predictedZ;
                    MovementSimulation.Step(ref _predictedX, ref _predictedZ, ref _predictedDash,
                        movement, aim.x, aim.z, GameConstants.TickDeltaTime);
                    _pendingInputs.Add((inputIndex, movement, aim.x, aim.z));
                }
            }

            // The own view follows the prediction every frame, blended between the
            // last two fixed steps by the accumulator fraction (render interpolation).
            if (_predictionInitialized && _playerViews.TryGetValue(_client.PlayerId, out GameObject ownView))
            {
                float alpha = Mathf.Clamp01(_inputAccumulator / GameConstants.TickDeltaTime);
                ownView.transform.position = new Vector3(
                    Mathf.Lerp(_prevPredictedX, _predictedX, alpha),
                    PlayerY,
                    Mathf.Lerp(_prevPredictedZ, _predictedZ, alpha));

                // The own figure's cosmetics come from the PREDICTION too: it turns
                // toward the cursor and runs, sprints or dashes with zero latency.
                if (_characterVisuals.TryGetValue(_client.PlayerId, out CharacterVisual ownVisual) && ownVisual != null)
                {
                    MovementBits m = _lastSampledMovement;
                    bool moving = (m & (MovementBits.W | MovementBits.A | MovementBits.S | MovementBits.D)) != 0;
                    bool sprinting = (m & MovementBits.Sprint) != 0 && (m & MovementBits.W) != 0
                                  && (m & MovementBits.S) == 0 && (m & MovementBits.Walk) == 0;
                    ownVisual.SetMotionState(moving, sprinting, _predictedDash.TicksLeft > 0,
                        GameConstants.DashDurationTicks * GameConstants.TickDeltaTime);
                    if (AimSource != null && AimSource.HasAim)
                        SetAim(_client.PlayerId, AimSource.AimPoint.x, AimSource.AimPoint.z);
                }
            }
        }
    }
}

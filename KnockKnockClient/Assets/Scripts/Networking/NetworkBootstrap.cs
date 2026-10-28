using System;
using System.Collections;
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
    /// MODULE 8: shooting. The fire button and weapon switches ride in the inputs;
    /// the server decides every hit. The client draws its own shot on the click
    /// ("favor the shooter"), mirrors the spread in the crosshair, predicts a weapon
    /// switch, and draws other players' shots from ShotsFired, delayed like their
    /// figures. Every input carries the tick on screen (NetworkClient.ViewTick), so
    /// lag compensation rewinds to what the shooter saw.
    ///
    /// MODULE 9: the consequences. Damage arrives as PlayerDamaged and plays blood
    /// where the hit landed; death shatters the figure, fills the kill feed and the
    /// Tab scoreboard, and Fire respawns after the delay — with the prediction
    /// restarted, because a respawn is a teleport, not a misprediction. The punch
    /// swings on the click. The rocket is predicted locally on the click and drawn
    /// from the snapshot for everyone else; RocketExploded ends it.
    ///
    /// The dev UI is NetworkHud (handed out): it only reads the public properties
    /// below, listens to the two events and calls the public methods. Nothing here
    /// draws IMGUI; the crosshair, the weapon bar and the status HUD are the base
    /// project's components, fed below.
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

        [Header("Game data")]
        [Tooltip("weapons.json: a LOCAL copy of the server's data/weapons.json (M08). " +
                 "Both sides read it with the same shared parser; M10 fetches it from the server.")]
        public TextAsset WeaponTable;

        [Header("Gameplay couplings")]
        public CursorAim AimSource;

        [Tooltip("The crosshair. Its look and feel live on the component itself; this " +
                 "only feeds it the weapon's current spread.")]
        public DynamicCrosshair Crosshair;

        [Tooltip("The weapon bar. Fed the owned weapons and the active one every " +
                 "frame; how it draws them is its own business.")]
        public ActionBar ActionBar;

        [Tooltip("Dash ability icon with the radial cooldown sweep. Optional.")]
        public DashCooldownIcon DashCooldownIcon;

        [Tooltip("The CS corners: health bottom-left, ammo bottom-right. Fed health " +
                 "and the displayed weapon's pool every frame.")]
        public StatusHud StatusHud;

        [Tooltip("Blood burst played where a hit lands (the SplatterEffect prefab). " +
                 "Leave empty for no blood — it is cosmetic and nothing depends on it.")]
        public GameObject BloodEffect;

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
        public long SnapshotsDropped => _client?.SnapshotsDropped ?? 0;
        public uint InputsSent => _client?.InputIndex ?? 0;
        public int HeartbeatsSent => _client?.HeartbeatsSent ?? 0;
        public int UnackedInputs => _pendingInputs.Count;
        public long Reconciliations { get; private set; }
        public long BitMismatches { get; private set; }
        public string FirstMismatch { get; private set; } = "";

        // The own weapon line (M08): known once the first snapshot with us in it has
        // arrived. Ammo is the server's, for the active weapon; so is Health (M09).
        public bool HasOwnState => _me != null;
        public int Ammo => _me?.Ammo ?? 0;
        public int Health => _me?.Health ?? 0;

        // Death and score (M09): the session's frags and deaths, and while dead the
        // time left before Fire respawns you. The Tab scoreboard is every player in
        // the newest snapshot, named from the roster.
        public int Frags => _me?.Frags ?? 0;
        public int Deaths => _me?.Deaths ?? 0;
        public bool IsDead => _me != null && _me.State == PlayerLifeState.Dead;
        public float RespawnSecondsLeft => GameConstants.RespawnDelaySeconds - (Time.time - _diedAtTime);
        public IReadOnlyList<PlayerEntityState> ScoreboardPlayers => _scoreboardPlayers;
        public IReadOnlyDictionary<byte, string> Usernames => _usernames;
        public byte LocalPlayerId => _client?.PlayerId ?? 0;

        // A chat line, and a feed line (joined, left, took a pickup, a kill, a rocket
        // exploding), ready to show.
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

        // Our own player entity from the newest snapshot (weapon, ammo, health).
        private PlayerEntityState _me;

        // Death and respawn (M09). Every player in the newest snapshot, for the Tab
        // scoreboard; the last seen life state per player, for spotting the moment of
        // death (voxel shatter) and of respawn (model back) — state lives in every
        // snapshot, the TRANSITION is what the effect hangs on.
        private List<PlayerEntityState> _scoreboardPlayers = new List<PlayerEntityState>();
        private readonly Dictionary<ushort, PlayerLifeState> _lastLifeStates =
            new Dictionary<ushort, PlayerLifeState>();
        private bool _aliveInLastSnapshot = true;
        private float _diedAtTime;

        // Weapon switching is PREDICTED, like movement: the weapon you asked for is
        // shown at once, and the request keeps riding along in every input until the
        // server confirms it. Sending it in a single packet made a switch feel slow
        // (a full round trip) and made a dropped packet lose it silently.
        private byte? _requestedWeapon;
        private int _weaponRequestTicks;

        /// <summary>Index of the input that first carried the current request, or 0
        /// before it has gone out. A snapshot may only confirm the request once the
        /// server has PROCESSED that input — same rule as movement reconciliation.</summary>
        private uint _weaponRequestInputIndex;

        /// <summary>Give up re-asking after half a second: the server refuses weapons
        /// you do not own, and without a limit a rejected request would ask forever.</summary>
        private const int WeaponRequestTimeoutTicks = 15;

        /// <summary>The weapon to SHOW: the requested one while a switch is in flight,
        /// otherwise the server's. Everything cosmetic reads this (the HUD too), so the
        /// swap is instant — and the server still decides what actually fires.</summary>
        public WeaponId DisplayedWeapon =>
            _requestedWeapon is byte requested ? (WeaponId)requested
            : _me?.ActiveWeapon ?? WeaponId.Glock;

        // Shot cosmetics ("favor the shooter"): the client MIRRORS the server's
        // cooldown/ammo/bloom gates so tracers and the crosshair behave like the real
        // thing — but hits are decided by the server alone.
        private float _cosmeticBloom;
        private int _cosmeticCooldownTicks;
        private WeaponId _cosmeticWeapon = WeaponId.Glock;

        /// <summary>Other players' shots waiting for their moment: each plays one
        /// interpolation delay after it arrived (see <see cref="UpdateRemoteShots"/>).</summary>
        private readonly List<(float PlayAt, ShotRecord Shot)> _pendingRemoteShots =
            new List<(float, ShotRecord)>();

        /// <summary>How long after arrival a remote shot is drawn. Remote figures are
        /// rendered this far in the past (interpolation), so drawing the shot the
        /// moment it arrives would fire it from where the shooter is GOING to be.</summary>
        private static float RemoteShotDelaySeconds => InterpolationDelayTicks * GameConstants.TickDeltaTime;

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
            // The weapon table: a LOCAL copy of the server's data/weapons.json, read with
            // the same shared parser. Like the arena, the two sides agree only because
            // the two files are the same; M10 fetches both from the server.
            if (WeaponTable != null)
                WeaponStats.Load(WeaponStats.ParseTable(WeaponTable.text));
            else
                Debug.LogError($"{name}: no weapons.json assigned — copy data/weapons.json into the project " +
                               "and drag it onto Weapon Table", this);
            // The login menu needs the mouse: no crosshair and no weapon bar until we are in the game
            if (Crosshair != null)
                Crosshair.SetVisible(false);
            if (ActionBar != null)
                ActionBar.gameObject.SetActive(false);

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
            CollectWeaponSwitchKeys();

            if (_client.TryTakeSnapshot(out SnapshotPacket snapshot))
                ApplySnapshot(snapshot);

            UpdateRemoteViews();
            UpdateRemoteShots();
            UpdatePredictedRockets();
            SampleAndSendInputs();
            UpdateDashIcon();
            UpdateCrosshair();
            UpdateActionBar();
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
            _me = null;
            _requestedWeapon = null;
            _pendingRemoteShots.Clear();
            _cosmeticBloom = 0f;
            _cosmeticCooldownTicks = 0;
            if (Crosshair != null)
                Crosshair.SetVisible(false);
            if (ActionBar != null)
                ActionBar.gameObject.SetActive(false);
            if (StatusHud != null)
                StatusHud.gameObject.SetActive(false);
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

            // Death and respawn (M09).
            _scoreboardPlayers = new List<PlayerEntityState>();
            _lastLifeStates.Clear();
            _aliveInLastSnapshot = true;
            _diedAtTime = 0f;

            // Rockets (M09): the predicted visuals and the snapshot's rocket views.
            foreach (PredictedRocket predicted in _predictedRockets)
                if (predicted.Go != null)
                    Destroy(predicted.Go);
            _predictedRockets.Clear();
            foreach (GameObject view in _rocketViews.Values)
                Destroy(view);
            _rocketViews.Clear();
            _rocketHeights.Clear();
            _selfDrivenRockets.Clear();
            _explodedRockets.Clear();

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

                    case PlayerDamaged hit:
                        // Effects play on EVENTS. The snapshot shows health dropping,
                        // but only this carries WHERE the shot went in — the server
                        // computes it during hit detection and nothing else knows it.
                        SpawnBlood(hit);
                        break;

                    case PlayerDied died:
                        // Names ride along on kill events too — cheap robustness.
                        _usernames[died.KillerId] = died.KillerUsername;
                        _usernames[died.VictimId] = died.VictimUsername;
                        PlayerFeed?.Invoke($"{died.KillerUsername} killed {died.VictimUsername} ({(WeaponId)died.WeaponId})");
                        break;

                    case RocketExploded exploded:
                        HandleRocketExploded(exploded);
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
            _scoreboardPlayers = snapshot.Players;

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

                // The weapon in the hand comes from the snapshot — the server owns who
                // carries what. For YOURSELF the requested weapon is shown instead while
                // a switch is in flight, so the swap is instant.
                WeaponId shownWeapon = player.ActiveWeapon;
                if (player.EntityId == _client.PlayerId && _requestedWeapon is byte requested)
                {
                    // Only a snapshot that has PROCESSED the input carrying the request
                    // can settle it: the server has either switched, or refused.
                    bool serverHasSeenIt = _weaponRequestInputIndex != 0 &&
                                           snapshot.LastProcessedInputIndex >= _weaponRequestInputIndex;
                    if (serverHasSeenIt)
                        _requestedWeapon = null;
                    else
                        shownWeapon = (WeaponId)requested;
                }
                if (_characterVisuals.TryGetValue(player.EntityId, out CharacterVisual weaponVisual) && weaponVisual != null)
                    weaponVisual.SetWeapon(shownWeapon);

                ApplyLifeState(player, view);

                if (player.EntityId == _client.PlayerId)
                {
                    _me = player;
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
                _lastLifeStates.Remove(id);
            }

            ApplyRocketSnapshot(snapshot);

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

            // The tick of the picture on screen: other players are drawn here, so this
            // is what a shot fired now is aimed at. It rides in every input (M08).
            _client.ViewTick = (uint)Math.Round(_renderTick);

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

            UpdateRemoteRocketViews();
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
                _aliveInLastSnapshot = me.State == PlayerLifeState.Alive;
                return;
            }

            if (me.State != PlayerLifeState.Alive)
            {
                if (_aliveInLastSnapshot)
                    _diedAtTime = Time.time; // for the respawn countdown in the HUD

                // Dead: the server ACKs inputs but ignores movement — prediction
                // pauses and just tracks the authority.
                _pendingInputs.Clear();
                _predictedX = me.X;
                _predictedZ = me.Z;
                _prevPredictedX = me.X;
                _prevPredictedZ = me.Z;
                _predictedDash = DashOf(me);
                _aliveInLastSnapshot = false;
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
                _aliveInLastSnapshot = true;
                return;
            }

            _pendingInputs.RemoveAll(input => input.Index <= lastProcessedInputIndex);

            float replayedX = me.X;
            float replayedZ = me.Z;
            MovementSimulation.DashState replayedDash = DashOf(me);
            foreach ((uint _, MovementBits movement, float aimX, float aimZ) in _pendingInputs)
                MovementSimulation.Step(ref replayedX, ref replayedZ, ref replayedDash,
                    movement, aimX, aimZ, GameConstants.TickDeltaTime);

            if (!_aliveInLastSnapshot)
            {
                // Just respawned: re-seed the prediction (a respawn is a legitimate
                // teleport, not a misprediction).
                _predictedX = replayedX;
                _predictedZ = replayedZ;
                _prevPredictedX = replayedX;
                _prevPredictedZ = replayedZ;
                _predictedDash = replayedDash;
                _aliveInLastSnapshot = true;
                return;
            }

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

                ButtonBits buttons = ButtonBits.None;
                if (Input.GetMouseButton(0)) buttons |= ButtonBits.Fire;

                // The request rides in EVERY input until the server confirms it, so a
                // dropped packet costs a tick instead of the whole switch. It stops on
                // its own once the snapshot agrees, or after the timeout if the server
                // refuses (a weapon you do not own).
                byte weaponId = 0;
                if (_requestedWeapon is byte requested)
                {
                    if (++_weaponRequestTicks > WeaponRequestTimeoutTicks)
                    {
                        _requestedWeapon = null; // give up; the snapshot is the truth
                    }
                    else
                    {
                        buttons |= ButtonBits.SwitchWeapon;
                        weaponId = requested;
                    }
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

                // Dead: the server ignores a dead player's movement, so say nothing
                // and let the heartbeat keep the session alive. A button press breaks
                // the silence at once — Fire respawns you after the delay.
                bool dead = _me != null && _me.State != PlayerLifeState.Alive;
                if (dead && buttons == ButtonBits.None)
                {
                    TickShotCosmetics(buttons, aim); // bloom still recovers while quiet
                    continue;
                }

                uint inputIndex = _client.SendInput(movement, aim.x, aim.z, buttons, weaponId);

                // Remember which input first carried this request, so only a snapshot
                // that has actually processed it may confirm it.
                if ((buttons & ButtonBits.SwitchWeapon) != 0 && _weaponRequestInputIndex == 0)
                    _weaponRequestInputIndex = inputIndex;

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

                TickShotCosmetics(buttons, aim);
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

        // ------------------------------------------------------------- shooting

        private void CollectWeaponSwitchKeys()
        {
            if (IsTyping)
                return; // typing "1v1?" in chat must not switch weapons

            // GetKeyDown must be read every frame (not inside the fixed-rate loop).
            if (Input.GetKeyDown(KeyCode.Alpha1)) RequestWeapon(WeaponId.Punch);
            if (Input.GetKeyDown(KeyCode.Alpha2)) RequestWeapon(WeaponId.Glock);
            if (Input.GetKeyDown(KeyCode.Alpha3)) RequestWeapon(WeaponId.Ak47);
            if (Input.GetKeyDown(KeyCode.Alpha4)) RequestWeapon(WeaponId.RocketLauncher);
        }

        /// <summary>Asks for a weapon and shows it straight away. The request is always
        /// recorded, even for the weapon already held: an earlier request may still be
        /// travelling, and dropping this one would let the older one win.</summary>
        private void RequestWeapon(WeaponId weapon)
        {
            // Never predict a switch the server will refuse: the client knows which
            // weapons it owns (the bitfield from M01, in every snapshot).
            if (_me != null && (_me.OwnedWeapons & 1 << (int)weapon) == 0)
                return;

            _requestedWeapon = (byte)weapon;
            _weaponRequestTicks = 0;
            _weaponRequestInputIndex = 0; // stamped when it first goes out

            if (_client != null &&
                _characterVisuals.TryGetValue(_client.PlayerId, out CharacterVisual ownVisual) &&
                ownVisual != null)
            {
                ownVisual.SetWeapon(weapon);
            }
        }

        /// <summary>
        /// Runs once per sampled input tick: mirrors the server's fire gates (cooldown,
        /// dead zone, ammo) and bloom, and draws our own shot on the click — favor the
        /// shooter. The hit is decided by the server alone; this is the picture of it.
        /// </summary>
        private void TickShotCosmetics(ButtonBits buttons, Vector3 aim)
        {
            if (_me == null)
                return;

            // Follows the DISPLAYED weapon, so the crosshair resizes with the switch
            // instead of a round trip behind it.
            if (DisplayedWeapon != _cosmeticWeapon)
            {
                _cosmeticWeapon = DisplayedWeapon;
                _cosmeticBloom = 0f; // bloom does not carry across weapons (server rule)
                _cosmeticCooldownTicks = 0;
            }

            _cosmeticBloom = SpreadModel.Decay(_cosmeticWeapon, _cosmeticBloom, GameConstants.TickDeltaTime);
            if (_cosmeticCooldownTicks > 0)
                _cosmeticCooldownTicks--;

            if ((buttons & ButtonBits.Fire) == 0 || _me.State != PlayerLifeState.Alive || _cosmeticCooldownTicks > 0)
                return;

            WeaponStats stats = WeaponStats.For(_cosmeticWeapon);

            // Melee is exempt from the 2 m dead zone — a punch is thrown at point-blank
            // range by definition; only an aim point on your own position is refused.
            float directionX = aim.x - _predictedX;
            float directionZ = aim.z - _predictedZ;
            float aimDistanceSquared = directionX * directionX + directionZ * directionZ;
            if (stats.IsMelee ? aimDistanceSquared < GameConstants.FacingDeadZoneSquared
                              : Mathf.Sqrt(aimDistanceSquared) < GameConstants.DeadZoneMeters)
                return; // the server would reject this shot (dead zone; melee only on itself)
            if (!stats.IsMelee && _me.Ammo == 0)
                return; // the server would reject this shot (no ammo)

            _cosmeticCooldownTicks = Mathf.CeilToInt(stats.CooldownSeconds * GameConstants.TickRate);

            if (stats.IsMelee)
            {
                PlayOwnPunch(stats, directionX, directionZ, Mathf.Sqrt(aimDistanceSquared));
                return;
            }

            _cosmeticBloom = SpreadModel.OnShot(_cosmeticWeapon, _cosmeticBloom);

            // Kick the weapon hand. Recovery takes exactly one fire interval.
            Vector3 from = new Vector3(_predictedX, EntityY, _predictedZ);
            WeaponGrip grip = null;
            if (_characterVisuals.TryGetValue(_client.PlayerId, out CharacterVisual shooterVisual) &&
                shooterVisual != null)
            {
                shooterVisual.ApplyRecoil(stats.CooldownSeconds);
                if (shooterVisual.TryGetMuzzle(out Vector3 muzzle))
                    from = muzzle;
                grip = shooterVisual.HeldWeaponGrip;
            }
            if (grip != null)
                SpatialAudio.Play(grip.FireSounds, from, grip.FireVolume);

            if (_cosmeticWeapon == WeaponId.RocketLauncher)
            {
                // Favor the shooter, projectile edition. The authoritative rocket only
                // exists a round trip from now, so the click spawns the rocket VISUAL
                // immediately, from the actual barrel; when the server's entity arrives
                // it ADOPTS this object (ApplyRocketSnapshot).
                SpawnPredictedRocket(aim);
                return;
            }

            // The cosmetic shot runs FROM THE BARREL TO WHAT THE CURSOR IS ON and stops
            // at the first thing the physics scene says is in the way. Deliberately
            // WITHOUT a spread roll: the server owns the randomness.
            Vector3 shot = aim - from;
            if (shot.sqrMagnitude < 0.0001f)
                return;
            Vector3 to = aim;
            if (Physics.Raycast(from, shot.normalized, out RaycastHit cosmeticHit,
                    shot.magnitude, ~0, QueryTriggerInteraction.Ignore))
                to = cosmeticHit.point;

            if (grip != null && grip.ProjectilePrefab != null)
                BulletVisual.Spawn(grip.ProjectilePrefab, from, to, grip.ProjectileSpeed, grip.HitEffectPrefab);
            else
                TracerEffect.Spawn(from, to);
        }

        /// <summary>
        /// Draws what other players fire, from the server's ShotsFired: the hand kicks,
        /// and the round flies from the shooter's barrel to where the server says the
        /// shot ended. Our own shots are skipped — they were drawn on the click.
        /// </summary>
        private void UpdateRemoteShots()
        {
            while (_client.ShotEvents.TryDequeue(out ShotsFiredPacket packet))
            {
                foreach (ShotRecord shot in packet.Shots)
                    if (shot.ShooterId != _client.PlayerId)
                        _pendingRemoteShots.Add((Time.time + RemoteShotDelaySeconds, shot));
            }

            for (int i = _pendingRemoteShots.Count - 1; i >= 0; i--)
            {
                if (Time.time < _pendingRemoteShots[i].PlayAt)
                    continue;
                PlayRemoteShot(_pendingRemoteShots[i].Shot);
                _pendingRemoteShots.RemoveAt(i);
            }
        }

        private void PlayRemoteShot(ShotRecord shot)
        {
            if (!_characterVisuals.TryGetValue(shot.ShooterId, out CharacterVisual visual) || visual == null)
                return; // shooter not (or no longer) in view

            WeaponStats stats = WeaponStats.For(shot.Weapon);
            if (stats.IsMelee)
            {
                visual.Punch(); // a punch has no round to draw — the fist is the shot
                return;
            }
            visual.ApplyRecoil(stats.CooldownSeconds);

            Vector3 from = new Vector3(visual.transform.position.x, EntityY, visual.transform.position.z);
            if (visual.TryGetMuzzle(out Vector3 muzzle))
                from = muzzle;
            WeaponGrip grip = visual.HeldWeaponGrip;
            if (grip != null)
                SpatialAudio.Play(grip.FireSounds, from, grip.FireVolume);

            // The rocket is its own entity in the snapshot — only the kick is drawn here.
            if (shot.Weapon == WeaponId.RocketLauncher)
                return;

            // The server is XZ-only: the round flies level from the barrel to the end
            // point, which already stops where the server decided — on the player it
            // hit, else at the wall.
            Vector3 to = new Vector3(shot.EndX, from.y, shot.EndZ);
            if ((to - from).sqrMagnitude < 0.0001f)
                return;
            if (grip != null && grip.ProjectilePrefab != null)
                BulletVisual.Spawn(grip.ProjectilePrefab, from, to, grip.ProjectileSpeed,
                    shot.HitPlayer ? null : grip.HitEffectPrefab);
            else
                TracerEffect.Spawn(from, to);
        }

        /// <summary>The dash icon: shown while in the game, its sweep from the predicted dash state, so it starts
        /// at the dash START (the rule in MovementSimulation), so the sweep includes the dash itself.
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

        /// <summary>Feeds the crosshair the weapon's accumulated bloom — the same number
        /// the server rolls its spread inside.</summary>
        private void UpdateCrosshair()
        {
            if (Crosshair == null)
                return;
            bool visible = _client != null && _client.InGame && _me != null;
            Crosshair.SetVisible(visible);
            if (visible)
                Crosshair.SetSpread(_cosmeticBloom);
        }

        /// <summary>Hands the weapon bar the server's picture of your arsenal; the
        /// PREDICTED weapon decides which slot is lit.</summary>
        private void UpdateActionBar()
        {
            if (ActionBar == null)
                return;
            bool show = _client != null && _client.InGame && _me != null;
            ActionBar.gameObject.SetActive(show);
            if (show)
                ActionBar.Refresh(_me.OwnedWeapons, DisplayedWeapon);

            // The CS corners: health bottom-left, the active weapon's pool bottom-
            // right. Driven off the DISPLAYED weapon, so the count follows a
            // predicted switch instead of trailing it by a round trip.
            if (StatusHud != null)
            {
                StatusHud.gameObject.SetActive(show);
                if (show)
                    StatusHud.Set(_me.Health, _me.Ammo, !WeaponStats.For(DisplayedWeapon).IsMelee);
            }
        }

        // ------------------------------------------------------------- the punch (M09)

        /// <summary>
        /// Our own punch, on the click: the arc the server actually tests (reach plus
        /// the target's half extent, the weapon's arc angle) and the thrown fist. Both
        /// cosmetic — the server alone decides whether the swing lands.
        /// </summary>
        private void PlayOwnPunch(WeaponStats stats, float directionX, float directionZ, float aimDistance)
        {
            directionX /= aimDistance;
            directionZ /= aimDistance;

            _characterVisuals.TryGetValue(_client.PlayerId, out CharacterVisual puncher);
            if (puncher != null)
                puncher.ApplyRecoil(stats.CooldownSeconds);

            // Arc matching the server's actual check: reach + target half extent.
            MeleeSwingEffect.Spawn(new Vector3(_predictedX, EntityY, _predictedZ),
                directionX, directionZ,
                stats.MeleeRangeMeters + ArenaMap.PlayerHalfExtent, stats.MeleeArcDeg);

            if (puncher != null)
                puncher.Punch();
        }

        // ------------------------------------------------------------- damage (M09)

        /// <summary>
        /// Blood at the point a hit landed, from the PlayerDamaged event.
        ///
        /// The height is the client's to choose — the server has no Y — so the burst
        /// plays at chest height on the victim rather than at their feet, which is
        /// where the raw XZ would put it.
        /// </summary>
        private void SpawnBlood(PlayerDamaged hit)
        {
            if ((WeaponId)hit.WeaponId == WeaponId.Punch &&
                _characterVisuals.TryGetValue(hit.AttackerId, out CharacterVisual puncher) && puncher != null)
            {
                float y = ArenaMap.IsLoaded ? ArenaMap.GroundY + BloodHeight : BloodHeight;
                puncher.PlayPunchHit(new Vector3(hit.X, y, hit.Z));
            }

            if (BloodEffect == null)
                return;

            // The server's hitscan verdict is instantaneous, but the bullet the
            // shooter watches is theatre with a finite speed — so on a short round
            // trip the blood would beat the bullet to the target. For OUR OWN firearm
            // hits, hold the burst until the bullet visually gets there, the same way
            // the wall sparks already do (they are spawned by the bullet on arrival).
            //
            // Other players' firearm hits wait for THEIR round as well — it is drawn
            // from ShotsFired one interpolation delay after arrival. Melee is
            // genuinely instant, and a rocket's blood rides the explosion.
            float delay = 0f;
            WeaponId weapon = (WeaponId)hit.WeaponId;
            if (_client != null && hit.AttackerId != _client.PlayerId &&
                (weapon == WeaponId.Glock || weapon == WeaponId.Ak47) &&
                _characterVisuals.TryGetValue(hit.AttackerId, out CharacterVisual shooter) && shooter != null)
            {
                Vector3 from = shooter.transform.position;
                if (shooter.TryGetMuzzle(out Vector3 remoteMuzzle))
                    from = remoteMuzzle;
                WeaponGrip remoteGrip = shooter.HeldWeaponGrip;
                float remoteSpeed = remoteGrip != null && remoteGrip.ProjectileSpeed > 0f ? remoteGrip.ProjectileSpeed : 80f;
                float remoteDistance = Vector3.Distance(new Vector3(from.x, 0f, from.z), new Vector3(hit.X, 0f, hit.Z));
                delay = RemoteShotDelaySeconds + remoteDistance / remoteSpeed;
            }
            if (_client != null && hit.AttackerId == _client.PlayerId &&
                (weapon == WeaponId.Glock || weapon == WeaponId.Ak47))
            {
                Vector3 from = new Vector3(_predictedX, 0f, _predictedZ);
                WeaponGrip grip = null;
                if (_characterVisuals.TryGetValue(_client.PlayerId, out CharacterVisual visual) &&
                    visual != null)
                {
                    if (visual.TryGetMuzzle(out Vector3 muzzle))
                        from = new Vector3(muzzle.x, 0f, muzzle.z);
                    grip = visual.HeldWeaponGrip;
                }

                float speed = grip != null && grip.ProjectileSpeed > 0f ? grip.ProjectileSpeed : 80f;
                float distance = Vector3.Distance(from, new Vector3(hit.X, 0f, hit.Z));

                // The event spent a round trip coming back, and the bullet has been
                // flying that whole time — only wait out the remainder.
                float alreadyFlying = 2f * SimulatedLatencyMs / 1000f;
                delay = Mathf.Max(0f, distance / speed - alreadyFlying);
            }

            if (delay > 0.01f)
                StartCoroutine(SpawnBloodAfter(delay, hit));
            else
                SpawnBloodNow(hit);
        }

        private IEnumerator SpawnBloodAfter(float delay, PlayerDamaged hit)
        {
            yield return new WaitForSeconds(delay);
            SpawnBloodNow(hit);
        }

        private void SpawnBloodNow(PlayerDamaged hit)
        {
            float y = ArenaMap.IsLoaded ? ArenaMap.GroundY + BloodHeight : BloodHeight;
            GameObject burst = Instantiate(BloodEffect, new Vector3(hit.X, y, hit.Z),
                Quaternion.identity);

            foreach (Gameplay.SplatterEffect splatter in
                     burst.GetComponentsInChildren<Gameplay.SplatterEffect>())
            {
                // A punch and a rocket should not leave the same mess. Damage runs
                // 5 (punch) to 100 (rocket), so scale around 20.
                splatter.SetIntensity(Mathf.Clamp(hit.Damage / 20f, 0.4f, 3f));
                splatter.Play();
            }
        }

        /// <summary>Chest height above the floor. The burst reads as coming out of a
        /// body rather than off the ground.</summary>
        private const float BloodHeight = 1.4f;

        // ------------------------------------------------------------- death and respawn (M09)

        /// <summary>
        /// Death and respawn, seen as TRANSITIONS: dying shatters the model into voxels
        /// and hides it; respawning brings it back. A player who is already dead when
        /// we first see them (we just joined) is hidden without the show — their death
        /// was not news.
        /// </summary>
        private void ApplyLifeState(PlayerEntityState player, GameObject view)
        {
            if (!_lastLifeStates.TryGetValue(player.EntityId, out PlayerLifeState previousState))
            {
                _lastLifeStates[player.EntityId] = player.State;
                if (player.State == PlayerLifeState.Dead)
                    SetViewVisible(view, false);
            }
            else if (previousState != player.State)
            {
                _lastLifeStates[player.EntityId] = player.State;
                if (player.State == PlayerLifeState.Dead)
                {
                    // Sample the model into voxels BEFORE hiding it — the effect
                    // reads the renderers' positions and colours as they stand.
                    DeathVoxelEffect.Spawn(view.transform);
                    SetViewVisible(view, false);
                }
                else
                {
                    SetViewVisible(view, true);
                }
            }
        }

        /// <summary>Hides or shows a player view without deactivating it — the
        /// GameObject stays live, so hand coroutines, muzzle lookups and position
        /// updates keep working while the model is invisible between death and
        /// respawn. The aim collider goes with it: an invisible corpse must not
        /// catch the cursor and skew someone's aim point.</summary>
        private static void SetViewVisible(GameObject view, bool visible)
        {
            foreach (Renderer renderer in view.GetComponentsInChildren<Renderer>())
                renderer.enabled = visible;
            foreach (Collider collider in view.GetComponentsInChildren<Collider>())
                collider.enabled = visible;
        }

        // ------------------------------------------------------------- rocket (M09)

        // Rocket entities from the snapshot, drawn like remote players: interpolated
        // in the past (their buffers live in _remoteBuffers, keyed by entity id).
        private readonly Dictionary<ushort, GameObject> _rocketViews = new Dictionary<ushort, GameObject>();

        /// <summary>Render height per live rocket, taken from the shooter's muzzle when
        /// it spawned. Cleared with the view.</summary>
        private readonly Dictionary<ushort, float> _rocketHeights = new Dictionary<ushort, float>();

        /// <summary>Rocket entities whose view is a predicted visual driving itself —
        /// the interpolation loop must leave those alone.</summary>
        private readonly HashSet<ushort> _selfDrivenRockets = new HashSet<ushort>();

        /// <summary>
        /// Rockets the explosion event already ended. The event rides TCP, the
        /// snapshots ride UDP, and snapshots SENT before the detonation can still be
        /// in flight when the event lands — each of them still lists the rocket, and
        /// without this set the client would rebuild a view for it (a ghost rocket
        /// popping up after its own explosion) and tear it down again a beat later.
        /// An id leaves the set at the first snapshot that no longer carries it;
        /// from there the sequence check keeps every older snapshot out.
        /// </summary>
        private readonly HashSet<ushort> _explodedRockets = new HashSet<ushort>();

        // The player's OWN rocket, spawned the moment they click (favor the shooter).
        // Flight is deterministic — constant speed, straight line, same constants as
        // the server — so the client can fly the visual itself and be right to within
        // a tick. The server still owns everything that MATTERS: whether the shot
        // happened at all, and when and where it explodes.
        private sealed class PredictedRocket
        {
            public GameObject Go;
            public Vector3 Position;

            /// <summary>The server entity that adopted this visual (valid once Matched).</summary>
            public ushort EntityId;

            /// <summary>The visual reached its known end (aim point or wall) and already
            /// played its explosion. The server's RocketExploded that follows a round
            /// trip later must not play a second one.</summary>
            public bool Detonated;

            /// <summary>Normalized, y = 0 — the same flat direction the server flies.</summary>
            public Vector3 DirectionXZ;

            // The climb: Y goes StartY -> TargetY over the first ClimbXZ metres of XZ
            // travel, then stays level. The server is XZ-only, so height is ours to
            // draw — and what it must do is CROSS the exact 3D point under the
            // crosshair.
            public float TraveledXZ;
            public float ClimbXZ;
            public float StartY;
            public float TargetY;

            /// <summary>Where the flight ENDS: the aim point or the first geometry on
            /// the path, whichever comes first — the same places the server's rocket
            /// dies. The visual runs ahead of the truth by half a round trip, so
            /// without this it overshoots the detonation and flies on past its own
            /// explosion. It holds here until the server's event settles things.</summary>
            public float StopXZ = float.PositiveInfinity;

            public float Age;
            public bool Matched;
        }

        private readonly List<PredictedRocket> _predictedRockets = new List<PredictedRocket>();

        /// <summary>A predicted rocket the server never acknowledged (rejected shot,
        /// lost packets) dies quietly after this long.</summary>
        private const float PredictedRocketTimeoutSeconds = 1f;

        /// <summary>How far the server's detonation may sit from the one we already
        /// showed and still count as the same bang (float noise, the 5 cm pull-back
        /// from walls). Beyond this the server's rocket really died elsewhere.</summary>
        private const float LocalDetonationToleranceMeters = 1.5f;

        private void SpawnPredictedRocket(Vector3 aim)
        {
            // From the ACTUAL barrel — the visual muzzle, wherever the hand and the
            // body's turn have put it this frame. Without a weapon model, mirror the
            // server's formula so the two sides at least agree.
            Vector3 origin;
            if (_client != null &&
                _characterVisuals.TryGetValue(_client.PlayerId, out CharacterVisual visual) &&
                visual != null && visual.TryGetMuzzle(out Vector3 muzzle))
            {
                origin = muzzle;
            }
            else
            {
                WeaponStats stats = WeaponStats.For(WeaponId.RocketLauncher);
                Vector3 fwd = new Vector3(aim.x - _predictedX, 0f, aim.z - _predictedZ).normalized;
                Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
                origin = new Vector3(_predictedX, EntityY, _predictedZ)
                       + fwd * stats.MuzzleForwardMeters + right * stats.MuzzleRightMeters;
            }

            // Same rule as the server in XZ: muzzle -> aim point at the rocket speed.
            // Height is OURS — the server has no Y at all — and the rocket must pass
            // through the exact 3D point under the crosshair, like the bullets do. So
            // the visual climbs or dips linearly until it crosses the aim point, then
            // flies level. The XZ speed never changes, so the explosion the server
            // sends still lands where the visual has got to.
            Vector3 flat = new Vector3(aim.x - origin.x, 0f, aim.z - origin.z);
            Vector3 direction = flat.sqrMagnitude > 0.0001f
                ? flat.normalized
                : new Vector3(aim.x - _predictedX, 0f, aim.z - _predictedZ).normalized;
            float climb = flat.magnitude;
            bool hasClimb = climb > 0.05f;

            // The flight has a known end: the server detonates at the aim point
            // ("shooting the ground") or on the first wall, whichever is nearer, and
            // walls do not move. Both are computable HERE, from the shared geometry —
            // so the visual can stop exactly where its rocket is going to die instead
            // of sailing past its own explosion while the event is still in transit.
            // (A player stepping into the path still detonates earlier than this; the
            // event cleans that up.)
            float geometry = Raycast.ArenaHitDistance(origin.x, origin.z, direction.x, direction.z);
            float stop = hasClimb ? Mathf.Min(climb, geometry) : geometry;

            GameObject go = BuildRocketModel(aim - origin, "Rocket (predicted)");
            go.transform.position = origin;
            _predictedRockets.Add(new PredictedRocket
            {
                Go = go,
                Position = origin,
                DirectionXZ = direction,
                ClimbXZ = hasClimb ? climb : 0f,
                StartY = origin.y,
                TargetY = hasClimb ? aim.y : origin.y,
                StopXZ = stop,
            });
        }

        /// <summary>
        /// Flies the predicted rockets — including adopted ones, which keep driving
        /// their own view for the whole flight so the hand-off from prediction to
        /// authority never shows as a backwards jump. An unmatched one that the
        /// server never confirms times out; one whose entity despawned finds its
        /// GameObject already destroyed and drops the record.
        /// </summary>
        private void UpdatePredictedRockets()
        {
            for (int i = _predictedRockets.Count - 1; i >= 0; i--)
            {
                PredictedRocket rocket = _predictedRockets[i];
                if (rocket.Go == null)
                {
                    _predictedRockets.RemoveAt(i); // entity despawned; view went with it
                    continue;
                }

                if (rocket.Detonated)
                {
                    // Already exploded on screen; only waiting for the server's event
                    // to retire the (hidden) view. An unconfirmed one still times out.
                    if (!rocket.Matched)
                    {
                        rocket.Age += Time.deltaTime;
                        if (rocket.Age > PredictedRocketTimeoutSeconds)
                        {
                            Destroy(rocket.Go);
                            _predictedRockets.RemoveAt(i);
                        }
                    }
                    continue;
                }

                // Never overshoot the flight's known end (aim point or first wall).
                float step = GameConstants.ResolveRocketSpeed() * Time.deltaTime;
                if (rocket.TraveledXZ + step > rocket.StopXZ)
                    step = Mathf.Max(0f, rocket.StopXZ - rocket.TraveledXZ);
                rocket.TraveledXZ += step;
                rocket.Position += rocket.DirectionXZ * step;

                // Climb (or dip) toward the aimed height until the aim point is
                // crossed, then level off. Height is pure rendering — XZ is what the
                // server simulates, and it moved the full step above.
                bool climbing = rocket.ClimbXZ > 0f && rocket.TraveledXZ < rocket.ClimbXZ;
                float y = climbing
                    ? Mathf.Lerp(rocket.StartY, rocket.TargetY, rocket.TraveledXZ / rocket.ClimbXZ)
                    : rocket.TargetY;
                rocket.Position = new Vector3(rocket.Position.x, y, rocket.Position.z);
                rocket.Go.transform.position = rocket.Position;
                rocket.Go.transform.rotation = Quaternion.LookRotation(climbing
                    ? rocket.DirectionXZ * rocket.ClimbXZ + Vector3.up * (rocket.TargetY - rocket.StartY)
                    : rocket.DirectionXZ);

                // Reached the end: explode NOW, on our own clock. The end is
                // predictable — the aim point and the walls are the same on both
                // sides — so the effect is too; the server still decides the damage,
                // and its event only retires the view (a player stepping into the path
                // is the one case it differs, handled when the event arrives).
                if (rocket.TraveledXZ >= rocket.StopXZ - 0.001f)
                {
                    SpawnRocketExplosion(rocket.Position);
                    rocket.Detonated = true;
                    rocket.Go.SetActive(false);
                    continue;
                }

                if (rocket.Matched)
                    continue;

                rocket.Age += Time.deltaTime;
                if (rocket.Age > PredictedRocketTimeoutSeconds)
                {
                    Destroy(rocket.Go);
                    _predictedRockets.RemoveAt(i);
                }
            }
        }

        private PredictedRocket PredictedRocketFor(ushort entityId)
        {
            foreach (PredictedRocket rocket in _predictedRockets)
                if (rocket.Matched && rocket.EntityId == entityId)
                    return rocket;
            return null;
        }

        private PredictedRocket OldestUnmatchedPredictedRocket()
        {
            foreach (PredictedRocket rocket in _predictedRockets)
                if (!rocket.Matched && rocket.Go != null)
                    return rocket;
            return null;
        }

        /// <summary>
        /// The rockets in a snapshot. Our own rocket ADOPTS the visual we predicted on
        /// the click; anyone else's gets a view at the height of the barrel it came
        /// out of, fed into an interpolation buffer like a remote player.
        /// </summary>
        private void ApplyRocketSnapshot(SnapshotPacket snapshot)
        {
            List<ushort> liveRocketIds = new List<ushort>();
            foreach (RocketEntityState rocket in snapshot.Rockets)
            {
                liveRocketIds.Add(rocket.EntityId);

                // Already ended by its explosion event — this snapshot was sent
                // before the detonation and only arrived after it.
                if (_explodedRockets.Contains(rocket.EntityId))
                    continue;

                if (!_rocketViews.TryGetValue(rocket.EntityId, out GameObject view))
                {
                    // Our own rocket? The player already watched it leave the barrel
                    // the moment they clicked — the entity arriving a round trip
                    // later ADOPTS that visual instead of spawning a second one a few
                    // metres back. The predicted flight keeps driving it (flagged
                    // self-driven, skipped by interpolation); the server still owns
                    // when and where it explodes.
                    PredictedRocket predicted = rocket.OwnerId == _client.PlayerId
                        ? OldestUnmatchedPredictedRocket()
                        : null;
                    if (predicted != null)
                    {
                        view = predicted.Go;
                        predicted.Matched = true;
                        predicted.EntityId = rocket.EntityId;
                        _selfDrivenRockets.Add(rocket.EntityId);
                        _rocketHeights[rocket.EntityId] = predicted.Position.y;
                    }
                    else
                    {
                        view = CreateRocketView(rocket);

                        // Fly at the height of the barrel it came out of. The server
                        // has no Y at all — it placed the rocket at the muzzle in XZ,
                        // and this is the rendering half of the same answer. Read
                        // once, at spawn: the shooter's hand keeps moving, the rocket
                        // does not follow it.
                        _rocketHeights[rocket.EntityId] = MuzzleHeightOf(rocket.OwnerId);

                        // Seed the view at the first known position so it does not
                        // flash at the origin before the interpolator has two samples.
                        view.transform.position = new Vector3(
                            rocket.X, _rocketHeights[rocket.EntityId], rocket.Z);
                    }
                    _rocketViews[rocket.EntityId] = view;
                }
                if (!_remoteBuffers.TryGetValue(rocket.EntityId, out InterpolationBuffer rocketBuffer))
                    _remoteBuffers[rocket.EntityId] = rocketBuffer = new InterpolationBuffer();
                rocketBuffer.Add(snapshot.ServerTick, rocket.X, rocket.Z);
            }

            List<ushort> stale = new List<ushort>();
            foreach (ushort id in _rocketViews.Keys)
                if (!liveRocketIds.Contains(id))
                    stale.Add(id);
            foreach (ushort id in stale)
            {
                Destroy(_rocketViews[id]);
                _rocketViews.Remove(id);
            }

            // Rocket buffers and heights go with their entity.
            List<ushort> deadBuffers = new List<ushort>();
            foreach (ushort id in _remoteBuffers.Keys)
                if (!_playerViews.ContainsKey(id) && !_rocketViews.ContainsKey(id))
                    deadBuffers.Add(id);
            foreach (ushort id in deadBuffers)
                _remoteBuffers.Remove(id);
            foreach (ushort id in new List<ushort>(_rocketHeights.Keys))
                if (!liveRocketIds.Contains(id))
                    _rocketHeights.Remove(id);
            _selfDrivenRockets.RemoveWhere(id => !liveRocketIds.Contains(id));

            // First snapshot without the rocket: the ghost window is over, and the
            // sequence check keeps every older snapshot out from here on.
            _explodedRockets.RemoveWhere(id => !liveRocketIds.Contains(id));
        }

        /// <summary>Positions the other players' rockets from their buffers, on the
        /// same past render clock as the remote players.</summary>
        private void UpdateRemoteRocketViews()
        {
            foreach (KeyValuePair<ushort, GameObject> entry in _rocketViews)
            {
                // A rocket we predicted flies itself (UpdatePredictedRockets); letting
                // the interpolator touch it would snap it a round trip backwards.
                if (_selfDrivenRockets.Contains(entry.Key))
                    continue;
                if (!_remoteBuffers.TryGetValue(entry.Key, out InterpolationBuffer rocketBuffer))
                    continue;

                float x, z;
                bool has = EnableInterpolation
                    ? rocketBuffer.TrySample(_renderTick, out x, out z)
                    : rocketBuffer.TryLatest(out x, out z);
                if (has)
                {
                    float y = _rocketHeights.TryGetValue(entry.Key, out float muzzleY) ? muzzleY : EntityY;
                    entry.Value.transform.position = new Vector3(x, y, z);
                }
            }
        }

        /// <summary>
        /// The server says a rocket blew up. Explode at the height the rocket was
        /// DRAWN at — the event carries XZ, because that is all the server has — and
        /// take its view down with it. Our own rocket usually exploded on screen
        /// already, the moment it reached its known end; the server's bang only plays
        /// if that did not happen, or if the rocket died somewhere else (a player
        /// stepped into the path, the one end the client could not foresee).
        /// </summary>
        private void HandleRocketExploded(RocketExploded exploded)
        {
            float boomY;
            if (_rocketViews.TryGetValue(exploded.RocketEntityId, out GameObject exploding) &&
                exploding != null)
                boomY = exploding.transform.position.y;
            else if (!_rocketHeights.TryGetValue(exploded.RocketEntityId, out boomY))
                boomY = EntityY;

            // A rocket fired point-blank can detonate on the very tick it spawned and
            // never appear in any snapshot. Our predicted visual is then the only
            // trace of it — this event is its end.
            PredictedRocket own = null;
            if (exploded.OwnerId == _client.PlayerId &&
                !_rocketViews.ContainsKey(exploded.RocketEntityId))
            {
                PredictedRocket orphan = OldestUnmatchedPredictedRocket();
                if (orphan != null)
                {
                    own = orphan;
                    boomY = orphan.Position.y;
                    Destroy(orphan.Go);
                    _predictedRockets.Remove(orphan);
                }
            }
            else if (exploded.OwnerId == _client.PlayerId)
            {
                own = PredictedRocketFor(exploded.RocketEntityId);
            }

            bool alreadyShown = own != null && own.Detonated &&
                new Vector2(own.Position.x - exploded.X, own.Position.z - exploded.Z).sqrMagnitude
                    < LocalDetonationToleranceMeters * LocalDetonationToleranceMeters;
            if (!alreadyShown)
                SpawnRocketExplosion(new Vector3(exploded.X, boomY, exploded.Z));

            // The event IS the end of this rocket — take the view down with it.
            // Waiting for the despawn snapshot left the visual flying on for a beat
            // after its own explosion.
            if (exploding != null)
            {
                Destroy(exploding);
                _rocketViews.Remove(exploded.RocketEntityId);
            }
            _rocketHeights.Remove(exploded.RocketEntityId);
            _selfDrivenRockets.Remove(exploded.RocketEntityId);
            _explodedRockets.Add(exploded.RocketEntityId);
            PlayerFeed?.Invoke($"boom! rocket exploded at ({exploded.X:F0}, {exploded.Z:F0})");
        }

        private GameObject CreateRocketView(RocketEntityState rocket)
            => BuildRocketModel(new Vector3(rocket.DirectionX, 0f, rocket.DirectionZ),
                $"Rocket {rocket.EntityId}");

        /// <summary>
        /// A rocket visual: the model the launcher itself says it fires
        /// (<see cref="WeaponGrip.ProjectilePrefab"/>), pointed along its flight
        /// direction. The weapon owns this the same way it owns how it sits in a
        /// hand — no projectile is configured anywhere in the netcode. With no model
        /// assigned we fall back to a plain sphere, which is unmistakably a
        /// placeholder rather than something that might pass for art.
        ///
        /// The prefab's own scale is kept. Overriding it is how a correctly-sized
        /// model ends up invisible.
        /// </summary>
        private GameObject BuildRocketModel(Vector3 direction, string name)
        {
            Quaternion facing = direction.sqrMagnitude > 0.000001f
                ? Quaternion.LookRotation(direction)
                : Quaternion.identity;

            GameObject prefab = RocketProjectilePrefab();
            if (prefab != null)
            {
                GameObject view = Instantiate(prefab, Vector3.zero, facing);
                view.name = name;
                // It is a picture of simulation state, not a thing in the scene: it
                // must not collide, and it must not drift on its own.
                foreach (Collider collider in view.GetComponentsInChildren<Collider>())
                    Destroy(collider);
                foreach (HoverAndRotate hover in view.GetComponentsInChildren<HoverAndRotate>())
                    Destroy(hover);
                return view;
            }

            GameObject placeholder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            placeholder.name = name + " (no projectile model)";
            placeholder.transform.localScale = Vector3.one * 0.35f;
            placeholder.transform.rotation = facing;
            Destroy(placeholder.GetComponent<Collider>());
            placeholder.GetComponent<Renderer>().material.color = Color.red;
            return placeholder;
        }

        /// <summary>The rocket launcher's projectile model. A Rocket entity is by
        /// definition what the rocket launcher fires, so this does not depend on what
        /// the owner happens to be holding by the time we see it.</summary>
        private static GameObject RocketProjectilePrefab()
        {
            GameObject held = PickupMarker.HeldPrefabFor(WeaponId.RocketLauncher);
            WeaponGrip grip = held != null ? held.GetComponent<WeaponGrip>() : null;
            return grip != null ? grip.ProjectilePrefab : null;
        }

        /// <summary>
        /// What a rocket leaves behind: the launcher's own hit effect, carried on the
        /// weapon's <see cref="WeaponGrip.HitEffectPrefab"/> exactly like the bullet
        /// weapons carry their hit sparks there. With nothing assigned we fall back to
        /// the code-built placeholder sphere (the OTHER ExplosionEffect, in this
        /// assembly — the ported art lives in Gameplay under the same name).
        /// </summary>
        private void SpawnRocketExplosion(Vector3 position)
        {
            GameObject held = PickupMarker.HeldPrefabFor(WeaponId.RocketLauncher);
            WeaponGrip grip = held != null ? held.GetComponent<WeaponGrip>() : null;
            if (grip != null)
                SpatialAudio.Play(grip.ImpactSounds, position, grip.ImpactVolume);
            GameObject prefab = grip != null ? grip.HitEffectPrefab : null;
            if (prefab == null)
            {
                ExplosionEffect.Spawn(position);
                return;
            }

            GameObject boom = Instantiate(prefab, position, Quaternion.identity);

            // The ported effect builds its particle systems stopped and waits for
            // Play() — an effect that fires in Awake would flash at the prefab's
            // authoring position for one frame first.
            foreach (Gameplay.ExplosionEffect effect in
                     boom.GetComponentsInChildren<Gameplay.ExplosionEffect>())
                effect.Play();
        }

        /// <summary>World Y of a player's muzzle, or the default entity height when
        /// they have no weapon model (or we have no view of them yet).</summary>
        private float MuzzleHeightOf(byte playerId)
        {
            if (_characterVisuals.TryGetValue(playerId, out CharacterVisual visual) && visual != null &&
                visual.TryGetMuzzle(out Vector3 muzzle))
                return muzzle.y;
            return EntityY;
        }
    }
}

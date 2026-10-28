using System.Collections.Generic;
using KnockKnockArena.Gameplay;
using KnockKnockArena.Shared.Protocol.Udp;
using UnityEngine;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// Handed out (Moodle), module 9 after implementation 8: the dev UI — connect panel
    /// with the latency field, "Find LAN servers" and the list of servers that answered,
    /// HUD with your HP, the shown weapon and its ammo, frags and deaths, the UDP
    /// counters (joins, snapshots, snapshots dropped out of order, inputs, heartbeats),
    /// the prediction line (unacked inputs, reconciliations, bitMismatches, the first
    /// mismatch), the key line (the prediction, reconciliation and interpolation toggles and the
    /// latency and jitter sliders are in the settings menu, F1), chat and the feed (joined, left, took a
    /// pickup, the kills, the rocket explosions), the YOU DIED overlay with the
    /// respawn countdown, and the scoreboard while Tab is held. The crosshair, the weapon bar and the status HUD
    /// (health and ammo in the corners) are not drawn here: they are the base
    /// project's components, fed by NetworkBootstrap. The UI is unchanged since
    /// implementation 4; it replaces the NetworkHud.cs from implementation 5.
    /// It only reads NetworkBootstrap's public properties, sets its
    /// connect fields, SimulatedLatencyMs, SimulatedJitterMs, EnablePrediction,
    /// EnableReconciliation, EnableInterpolation and IsTyping, listens
    /// to its two events and calls its public methods; it never touches the network. Put it in Assets/Scripts/Networking next to NetworkBootstrap.
    /// It adds itself to the NetworkBootstrap object when the scene starts, so nothing in
    /// the scene changes.
    /// </summary>
    public sealed class NetworkHud : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AttachToBootstrap()
        {
            NetworkBootstrap bootstrap = FindAnyObjectByType<NetworkBootstrap>();
            if (bootstrap != null && bootstrap.GetComponent<NetworkHud>() == null)
                bootstrap.gameObject.AddComponent<NetworkHud>();
        }

        private NetworkBootstrap _net;

        // Chat + event feed. Game-style chat: the input line is hidden until Enter
        // opens it; Enter again sends and closes; Escape cancels.
        private readonly List<string> _chatLog = new List<string>();
        private string _chatInput = "";
        private bool _chatOpen;
        private bool _chatFocusRequested;
        private readonly List<(string Text, float Time)> _feed = new List<(string, float)>();

        // How far the list of LAN servers is scrolled.
        private Vector2 _serverListScroll;

        private void Awake()
        {
            _net = GetComponent<NetworkBootstrap>();
            _net.ChatReceived += AddChatLine;
            _net.PlayerFeed += AddFeedLine;
            GameMenu.Sections += DrawSettingsSections;
            _showStats = PlayerPrefs.GetInt("kka.ui.stats", 1) == 1;
        }

        private void OnDestroy()
        {
            if (_net == null)
                return;
            _net.ChatReceived -= AddChatLine;
            _net.PlayerFeed -= AddFeedLine;
            GameMenu.Sections -= DrawSettingsSections;
        }

        private void Update()
        {
            _feed.RemoveAll(entry => Time.time - entry.Time > 6f);

            // The open chat line or the settings menu (F1) takes the keys and the clicks, so NetworkBootstrap stops
            // moving the player, and a click on a slider in the menu is not a shot.
            _net.IsTyping = _chatOpen || GameMenu.Open;

            // A dropped connection closes the chat line.
            if (!_net.IsLoggedIn && _chatOpen)
            {
                SetChatOpen(false);
                _chatFocusRequested = false;
                _chatInput = "";
            }
        }

        // The open chat line takes the keys, so NetworkBootstrap stops moving the player.
        private void SetChatOpen(bool open)
        {
            _chatOpen = open;
            _net.IsTyping = open;
        }

        private void AddChatLine(string line)
        {
            _chatLog.Add(line);
            if (_chatLog.Count > 10)
                _chatLog.RemoveAt(0);
        }

        private void AddFeedLine(string line)
        {
            _feed.Add((line, Time.time));
            if (_feed.Count > 6)
                _feed.RemoveAt(0);
        }

        private void OnGUI()
        {
            // White text throughout the dev UI, and every panel on a dark backdrop
            // (HudPanel), since this map's ground is bright: white on dark reads in-game and
            // survives video compression on a recording.
            GUI.contentColor = Color.white;

            if (!_net.IsLoggedIn)
            {
                DrawConnectPanel();
                return;
            }

            HandleChatKeys();

            if (_showStats)
                DrawHud();
            else
                DrawKeyHint();
            DrawDeathOverlay();
            DrawChat();
            DrawFeed();
            if (Input.GetKey(KeyCode.Tab) && _net.InGame)
                DrawScoreboard();
        }

        private void DrawConnectPanel()
        {
            // Solid black panel with white text: the default box is see-through.
            Rect panel = new Rect(20, 20, 400, 390);
            Color previousColor = GUI.color;
            Color previousContent = GUI.contentColor;
            GUI.color = Color.black;
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = previousColor;
            GUI.contentColor = Color.white;

            GUILayout.BeginArea(new Rect(panel.x + 8, panel.y + 6, panel.width - 16, panel.height - 12));
            GUILayout.Label("KnockKnock Arena — connect");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host", GUILayout.Width(70));
            _net.Host = GUILayout.TextField(_net.Host);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(70));
            int.TryParse(GUILayout.TextField(_net.Port.ToString()), out _net.Port);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("User", GUILayout.Width(70));
            _net.Username = GUILayout.TextField(_net.Username);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Password", GUILayout.Width(70));
            _net.Password = GUILayout.PasswordField(_net.Password, '*');
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Latency ms", GUILayout.Width(70));
            int.TryParse(GUILayout.TextField(_net.SimulatedLatencyMs.ToString()), out _net.SimulatedLatencyMs);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !_net.IsConnecting;
            if (GUILayout.Button("Connect"))
                _net.Connect();
            GUI.enabled = !_net.IsConnecting && !_net.IsSearchingLan;
            if (GUILayout.Button("Find LAN servers"))
                _net.FindLanServers();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            DrawServerList();

            GUILayout.Label(_net.StatusLine);
            GUILayout.EndArea();
            GUI.contentColor = previousContent;
        }

        private void DrawServerList()
        {
            if (_net.LanStatus.Length == 0)
                return;

            _serverListScroll = GUILayout.BeginScrollView(_serverListScroll, GUILayout.Height(120));
            GUILayout.Label(_net.LanStatus);
            foreach (DiscoveredServer server in _net.LanServers)
            {
                GUILayout.BeginHorizontal();
                if (server.Info == null)
                {
                    GUILayout.Label($"{server.Address}:{server.SourcePort}  protocol v{server.ProtocolVersion} (incompatible)");
                }
                else
                {
                    GUILayout.Label($"{server.Info.ServerName}  {server.Info.PlayersOnline}/{server.Info.MaxPlayers}");
                    GUI.enabled = !_net.IsConnecting;
                    if (GUILayout.Button("Join", GUILayout.Width(50)))
                        _net.JoinLanServer(server);
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        // The counters change every frame. In a font where every character is equally wide, and with a fixed width
        // per number, the text around them stays still, so the HUD can be read while playing.
        private GUIStyle _counters;
        private GUIStyle Counters => _counters ??= new GUIStyle(GUI.skin.label)
        {
            font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New" }, 14),
        };

        private void DrawHud()
        {
            GUILayout.BeginArea(new Rect(20, 20, 780, 160));
            GUILayout.BeginVertical(HudPanel);
            GUILayout.Label(!_net.InGame ? _net.StatusLine
                : !_net.HasOwnState ? "In the game (UDP)"
                : $"In the game (UDP)  |  HP {_net.Health}  |  {_net.DisplayedWeapon} ({_net.Ammo} ammo)  |  " +
                  $"frags {_net.Frags}  deaths {_net.Deaths}");
            GUILayout.Label($"UDP: joins sent={_net.JoinsSent,3}  snapshots={_net.SnapshotsAccepted,7}  " +
                            $"dropped (out of order)={_net.SnapshotsDropped,5}  " +
                            $"inputs sent={_net.InputsSent,7}  heartbeats={_net.HeartbeatsSent,4}", Counters);
            GUILayout.Label($"{_net.ServerInfoLine}  |  unknown TCP messages skipped: {_net.TcpMessagesSkipped}");
            GUILayout.Label($"prediction: unacked={_net.UnackedInputs,3}  reconciliations={_net.Reconciliations,7}  " +
                            $"bitMismatches={_net.BitMismatches,4}", Counters);
            GUILayout.Label("F1 settings  |  WASD move  |  Shift sprint  |  Ctrl walk  |  Space dash  |  mouse fire  |  1-4 weapons  |  Enter chat  |  Tab scoreboard");
            if (_net.FirstMismatch.Length > 0)
                GUILayout.Label($"FIRST MISMATCH: {_net.FirstMismatch}");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        /// <summary>
        /// The netcode techniques plus the latency simulator, switchable WHILE PLAYING
        /// so one unbroken take can show what each of them is doing. The controls only
        /// set NetworkBootstrap's fields; NetworkBootstrap pushes the latency and the
        /// jitter into the client every frame — no reconnect needed.
        /// </summary>
        private void DrawTechniqueToggles()
        {
            GUILayout.Label("Netcode");
            GUILayout.BeginHorizontal();
            _net.EnablePrediction = GUILayout.Toggle(_net.EnablePrediction, " prediction", GUILayout.Width(110));
            _net.EnableReconciliation = GUILayout.Toggle(_net.EnableReconciliation, " reconciliation", GUILayout.Width(125));
            _net.EnableInterpolation = GUILayout.Toggle(_net.EnableInterpolation, " interpolation", GUILayout.Width(120));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"   latency {_net.SimulatedLatencyMs} ms/way", GUILayout.Width(160));
            _net.SimulatedLatencyMs = Mathf.RoundToInt(
                GameMenu.MenuSlider(_net.SimulatedLatencyMs, 0f, 300f, GUILayout.Width(200)));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"   jitter ±{_net.SimulatedJitterMs} ms", GUILayout.Width(160));
            _net.SimulatedJitterMs = Mathf.RoundToInt(
                GameMenu.MenuSlider(_net.SimulatedJitterMs, 0f, 100f, GUILayout.Width(200)));
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------- settings menu (F1)

        // The stats panel (top left) can be hidden from the menu; kept between runs (read in Awake:
        // Unity does not allow PlayerPrefs in a MonoBehaviour's field initializers).
        private bool _showStats = true;

        // with the stats hidden: only the keys, so F1 can still be found
        private void DrawKeyHint()
        {
            GUILayout.BeginArea(new Rect(20, 20, 780, 60));
            GUILayout.BeginHorizontal();
            GUILayout.Label("F1 settings  |  Tab scoreboard  |  Enter chat", HudPanel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>
        /// This HUD's sections in the settings menu (GameMenu, F1), above its sound section: the
        /// stats on or off and the netcode techniques with the latency simulator. While the menu is
        /// open the game takes no input (IsTyping, set in Update).
        /// </summary>
        private void DrawSettingsSections()
        {
            bool showStats = GUILayout.Toggle(_showStats, " Show stats (top left: connection, inputs, prediction)");
            if (showStats != _showStats)
            {
                _showStats = showStats;
                PlayerPrefs.SetInt("kka.ui.stats", showStats ? 1 : 0);
                PlayerPrefs.Save();
            }
            GUILayout.Space(10);
            DrawTechniqueToggles();
        }

        // A dark, half see-through backdrop that fits its content: the default box is too light
        // behind white text on this map.
        private GUIStyle _hudPanel;
        private GUIStyle HudPanel
        {
            get
            {
                if (_hudPanel != null)
                    return _hudPanel;
                Texture2D dark = new Texture2D(1, 1);
                dark.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.65f));
                dark.Apply();
                _hudPanel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 6, 6) };
                _hudPanel.normal.background = dark;
                return _hudPanel;
            }
        }

        private GUIStyle _deathStyle;

        /// <summary>Big centered death overlay: the respawn countdown and prompt need
        /// to be unmissable, not buried in the debug block. Respawn is "press fire"
        /// (Quake style) — a held or fresh click after the delay respawns.</summary>
        private void DrawDeathOverlay()
        {
            if (!_net.IsDead)
                return;

            if (_deathStyle == null)
            {
                _deathStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 34,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
            }

            float wait = _net.RespawnSecondsLeft;
            string line2 = wait > 0f
                ? $"respawn in {wait:F1} s"
                : "CLICK (fire) TO RESPAWN";
            GUI.Box(new Rect(Screen.width / 2f - 220f, Screen.height * 0.35f - 6f, 440f, 88f), GUIContent.none, HudPanel);
            GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, 50), "YOU DIED", _deathStyle);
            GUI.Label(new Rect(0, Screen.height * 0.35f + 46, Screen.width, 30), line2,
                new GUIStyle(_deathStyle) { fontSize = 20, fontStyle = FontStyle.Normal });
        }

        /// <summary>Hold Tab: this session's frags and deaths, straight from the newest
        /// snapshot — every player entity carries its own score.</summary>
        private void DrawScoreboard()
        {
            IReadOnlyList<PlayerEntityState> players = _net.ScoreboardPlayers;
            const float width = 420f;
            float height = 60f + players.Count * 24f;
            GUILayout.BeginArea(new Rect((Screen.width - width) / 2f, 80f, width, height), HudPanel);
            GUILayout.Label("SCOREBOARD (session)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Player", GUILayout.Width(180));
            GUILayout.Label("Frags", GUILayout.Width(60));
            GUILayout.Label("Deaths", GUILayout.Width(60));
            GUILayout.Label("HP", GUILayout.Width(80));
            GUILayout.EndHorizontal();

            List<PlayerEntityState> sorted = new List<PlayerEntityState>(players);
            sorted.Sort((a, b) => b.Frags != a.Frags ? b.Frags.CompareTo(a.Frags) : a.Deaths.CompareTo(b.Deaths));

            foreach (PlayerEntityState player in sorted)
            {
                string name = _net.Usernames.TryGetValue((byte)player.EntityId, out string known)
                    ? known
                    : $"player {player.EntityId}";
                if (player.EntityId == _net.LocalPlayerId)
                    name += " (you)";
                GUILayout.BeginHorizontal();
                GUILayout.Label(name, GUILayout.Width(180));
                GUILayout.Label(player.Frags.ToString(), GUILayout.Width(60));
                GUILayout.Label(player.Deaths.ToString(), GUILayout.Width(60));
                GUILayout.Label(player.State == PlayerLifeState.Dead ? "DEAD" : player.Health.ToString(),
                    GUILayout.Width(80));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }

        private void HandleChatKeys()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown)
                return;

            if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
            {
                if (!_chatOpen)
                {
                    SetChatOpen(true);
                    _chatFocusRequested = true;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(_chatInput))
                        _net.SendChat(_chatInput.Trim());
                    _chatInput = "";
                    SetChatOpen(false);
                    GUIUtility.keyboardControl = 0;
                }
                current.Use();
            }
            else if (current.keyCode == KeyCode.Escape && _chatOpen)
            {
                _chatInput = "";
                SetChatOpen(false);
                GUIUtility.keyboardControl = 0;
                current.Use();
            }
        }

        private void DrawChat()
        {
            GUILayout.BeginArea(new Rect(20, Screen.height - 240, 420, 220), HudPanel);
            foreach (string line in _chatLog)
                GUILayout.Label(line);
            GUILayout.FlexibleSpace();

            if (_chatOpen)
            {
                GUI.SetNextControlName("ChatInput");
                _chatInput = GUILayout.TextField(_chatInput);
                if (_chatFocusRequested && Event.current.type == EventType.Repaint)
                {
                    GUI.FocusControl("ChatInput");
                    _chatFocusRequested = false;
                }
            }
            else
            {
                GUILayout.Label("<[Enter] chat>");
            }
            GUILayout.EndArea();
        }

        private void DrawFeed()
        {
            if (_feed.Count == 0)
                return;
            GUILayout.BeginArea(new Rect(Screen.width - 380, 20, 360, 160));
            GUILayout.BeginVertical(HudPanel);
            foreach ((string text, float _) in _feed)
                GUILayout.Label(text);
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }
}

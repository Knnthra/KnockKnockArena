using System.Collections.Generic;
using KnockKnockArena.Shared.Protocol.Udp;
using UnityEngine;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// Handed out (Moodle), module 8 after implementation 6: the dev UI — connect panel
    /// with the latency field, "Find LAN servers" and the list of servers that answered,
    /// HUD with the shown weapon and its ammo, the UDP counters (joins, snapshots,
    /// snapshots dropped out of order, inputs, heartbeats), the prediction line (unacked
    /// inputs, reconciliations, bitMismatches, the first mismatch), the prediction,
    /// reconciliation and interpolation toggles, the latency and jitter sliders and the
    /// key line (now with mouse fire and 1-4 weapons), chat and the feed (joined, left,
    /// took a pickup). The crosshair and the weapon bar are not drawn here: they are the
    /// base project's components, fed by NetworkBootstrap. The UI is unchanged since
    /// implementation 4; it replaces the NetworkHud.cs
    /// from implementation 4. It only reads NetworkBootstrap's public properties, sets its
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
        }

        private void OnDestroy()
        {
            if (_net == null)
                return;
            _net.ChatReceived -= AddChatLine;
            _net.PlayerFeed -= AddFeedLine;
        }

        private void Update()
        {
            _feed.RemoveAll(entry => Time.time - entry.Time > 6f);

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
            // Black text throughout the dev UI. The default skin draws light text,
            // which disappears against this map's bright ground — and this HUD gets
            // read off a recording, where it has to survive video compression too.
            GUI.contentColor = Color.black;

            if (!_net.IsLoggedIn)
            {
                DrawConnectPanel();
                return;
            }

            HandleChatKeys();

            DrawHud();
            DrawChat();
            DrawFeed();
        }

        private void DrawConnectPanel()
        {
            // Solid black panel with white text: the default box is see-through, and
            // the rest of the dev UI draws black text for the in-game overlay.
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
            GUILayout.Label(!_net.InGame ? _net.StatusLine
                : !_net.HasOwnState ? "In the game (UDP)"
                : $"In the game (UDP)  |  {_net.DisplayedWeapon} ({_net.Ammo} ammo)");
            GUILayout.Label($"UDP: joins sent={_net.JoinsSent,3}  snapshots={_net.SnapshotsAccepted,7}  " +
                            $"dropped (out of order)={_net.SnapshotsDropped,5}  " +
                            $"inputs sent={_net.InputsSent,7}  heartbeats={_net.HeartbeatsSent,4}", Counters);
            GUILayout.Label($"{_net.ServerInfoLine}  |  unknown TCP messages skipped: {_net.TcpMessagesSkipped}");
            GUILayout.Label($"prediction: unacked={_net.UnackedInputs,3}  reconciliations={_net.Reconciliations,7}  " +
                            $"bitMismatches={_net.BitMismatches,4}", Counters);
            DrawTechniqueToggles();
            GUILayout.Label("WASD move  |  Shift sprint  |  Ctrl walk  |  Space dash  |  mouse fire  |  1-4 weapons  |  Enter chat");
            if (_net.FirstMismatch.Length > 0)
                GUILayout.Label($"FIRST MISMATCH: {_net.FirstMismatch}");
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
            GUILayout.BeginHorizontal();
            _net.EnablePrediction = GUILayout.Toggle(_net.EnablePrediction, "prediction", GUILayout.Width(95));
            _net.EnableReconciliation = GUILayout.Toggle(_net.EnableReconciliation, "reconciliation", GUILayout.Width(115));
            _net.EnableInterpolation = GUILayout.Toggle(_net.EnableInterpolation, "interpolation", GUILayout.Width(110));
            GUILayout.Label($"latency {_net.SimulatedLatencyMs} ms/way", GUILayout.Width(120));
            _net.SimulatedLatencyMs = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(_net.SimulatedLatencyMs, 0f, 300f, GUILayout.Width(100)));
            GUILayout.Label($"jitter ±{_net.SimulatedJitterMs} ms", GUILayout.Width(95));
            _net.SimulatedJitterMs = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(_net.SimulatedJitterMs, 0f, 100f, GUILayout.Width(80)));
            GUILayout.EndHorizontal();
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
            GUILayout.BeginArea(new Rect(20, Screen.height - 240, 420, 220), GUI.skin.box);
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
            GUILayout.BeginArea(new Rect(Screen.width - 380, 20, 360, 160));
            foreach ((string text, float _) in _feed)
                GUILayout.Label(text);
            GUILayout.EndArea();
        }
    }
}

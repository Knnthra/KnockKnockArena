using System.Collections.Generic;
using UnityEngine;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// Handed out (Moodle), module 4: the dev UI — connect panel, HUD, chat and the
    /// joined/left feed. It only reads NetworkBootstrap's public properties, listens to
    /// its two events and calls its public methods; it never touches the network.
    /// Put it in Assets/Scripts/Networking next to NetworkBootstrap. It adds itself to
    /// the NetworkBootstrap object when the scene starts, so nothing in the scene changes.
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
                _chatOpen = false;
                _chatFocusRequested = false;
                _chatInput = "";
            }
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
            Rect panel = new Rect(20, 20, 400, 220);
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

            GUI.enabled = !_net.IsConnecting;
            if (GUILayout.Button("Connect"))
                _net.Connect();
            GUI.enabled = true;

            GUILayout.Label(_net.StatusLine);
            GUILayout.EndArea();
            GUI.contentColor = previousContent;
        }

        private void DrawHud()
        {
            GUILayout.BeginArea(new Rect(20, 20, 780, 80));
            GUILayout.Label(_net.StatusLine);
            GUILayout.Label($"{_net.ServerInfoLine}  |  unknown TCP messages skipped: {_net.TcpMessagesSkipped}");
            GUILayout.Label("Enter chat");
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
                    _chatOpen = true;
                    _chatFocusRequested = true;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(_chatInput))
                        _net.SendChat(_chatInput.Trim());
                    _chatInput = "";
                    _chatOpen = false;
                    GUIUtility.keyboardControl = 0;
                }
                current.Use();
            }
            else if (current.keyCode == KeyCode.Escape && _chatOpen)
            {
                _chatInput = "";
                _chatOpen = false;
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

using System;
using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// The settings menu: F1 opens it, F1, Esc or Close shuts it. It makes itself when the game
    /// starts (no scene holds it) and draws a solid black window with the sound settings. The
    /// network code can add its own sections above the sound (<see cref="Sections"/>), e.g. the
    /// netcode toggles and the latency simulator.
    ///
    /// While it is open the mouse cursor shows instead of the crosshair (CrosshairFollowMouse). The
    /// game's input is read by the network code, which should take no keys or clicks while
    /// <see cref="Open"/> is true, so a click on a slider is not a shot.
    /// </summary>
    public static class GameMenu
    {
        /// <summary>True while the settings menu is open.</summary>
        public static bool Open;

        /// <summary>Extra sections, drawn (GUILayout) at the top of the menu in the order added.</summary>
        public static event Action Sections;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            GameObject go = new GameObject("Settings menu (F1)");
            go.AddComponent<Host>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        private sealed class Host : MonoBehaviour
        {
            private void Update()
            {
                if (Input.GetKeyDown(KeyCode.F1))
                    Open = !Open;
                else if (Open && Input.GetKeyDown(KeyCode.Escape))
                    Open = false;
            }

            private void OnGUI()
            {
                if (!Open)
                    return;
                GUI.depth = -100;   // over every other panel
                GUI.contentColor = Color.white;
                const float width = 480f, height = 420f;
                GUILayout.BeginArea(new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height), Panel);
                GUILayout.Label("Settings   (F1 or Esc closes)");
                GUILayout.Space(6);
                if (Sections != null)
                {
                    Sections();
                    GUILayout.Space(10);
                }
                DrawSound();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close"))
                    Open = false;
                GUILayout.EndArea();
            }
        }

        private static GUIStyle _panel;

        // solid black, so nothing of the game shows through the text
        private static GUIStyle Panel
        {
            get
            {
                if (_panel != null)
                    return _panel;
                _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(12, 12, 10, 10) };
                _panel.normal.background = Solid(Color.black);
                return _panel;
            }
        }

        /// <summary>The sound section: footsteps and weapon sounds (shots, impacts, punches) on or
        /// off, each with a volume from 0 to 200 %. The settings live in <see cref="SpatialAudio"/>
        /// and are kept in PlayerPrefs.</summary>
        public static void DrawSound()
        {
            GUILayout.Label("Sound");
            bool footsteps = GUILayout.Toggle(SpatialAudio.FootstepsEnabled, " Footsteps");
            GUI.enabled = footsteps;
            float footstepsVolume = Slider(SpatialAudio.FootstepsVolume);
            GUI.enabled = true;

            bool weapons = GUILayout.Toggle(SpatialAudio.WeaponsEnabled, " Weapons (shots, impacts, punches)");
            GUI.enabled = weapons;
            float weaponsVolume = Slider(SpatialAudio.WeaponsVolume);
            GUI.enabled = true;

            if (footsteps != SpatialAudio.FootstepsEnabled || weapons != SpatialAudio.WeaponsEnabled ||
                !Mathf.Approximately(footstepsVolume, SpatialAudio.FootstepsVolume) ||
                !Mathf.Approximately(weaponsVolume, SpatialAudio.WeaponsVolume))
            {
                SpatialAudio.FootstepsEnabled = footsteps;
                SpatialAudio.FootstepsVolume = footstepsVolume;
                SpatialAudio.WeaponsEnabled = weapons;
                SpatialAudio.WeaponsVolume = weaponsVolume;
                SpatialAudio.SaveSettings();
            }
        }

        private static GUIStyle _track, _thumb;

        /// <summary>A slider that shows on the menu's black background: the default skin's track and
        /// thumb are dark grey and vanish there, so these are a grey track and a white thumb.</summary>
        public static float MenuSlider(float value, float min, float max, params GUILayoutOption[] options)
        {
            if (_track == null)
            {
                _track = new GUIStyle(GUI.skin.horizontalSlider) { fixedHeight = 10f, margin = new RectOffset(4, 4, 7, 7) };
                _track.normal.background = Solid(new Color(0.55f, 0.55f, 0.55f));
                _thumb = new GUIStyle(GUI.skin.horizontalSliderThumb) { fixedWidth = 14f, fixedHeight = 10f };
                _thumb.normal.background = Solid(Color.white);
                _thumb.hover.background = _thumb.active.background = Solid(new Color(1f, 0.8f, 0.3f));
            }
            return GUILayout.HorizontalSlider(value, min, max, _track, _thumb, options);
        }

        private static Texture2D Solid(Color color)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, color);
            t.Apply();
            return t;
        }

        // 0 to 200 %: 100 % is the sound as the prefabs set it
        private static float Slider(float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("   Volume", GUILayout.Width(80));
            value = MenuSlider(value, 0f, 2f, GUILayout.Width(200));
            GUILayout.Label($"{Mathf.RoundToInt(value * 100)} %", GUILayout.Width(50));
            GUILayout.EndHorizontal();
            return value;
        }
    }
}

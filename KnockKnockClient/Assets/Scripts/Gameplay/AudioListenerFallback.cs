using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// The base project's ears. Main.unity has no AudioListener (the camera hangs high above the
    /// map, and distance from there would make every sound equally far away), so without one the
    /// game is silent. This listener stands where the camera looks: at the player it follows
    /// (FixedFollowCamera's target), or at the camera when it follows no one yet.
    ///
    /// It steps aside as soon as anything else provides a listener: the full client's network code
    /// places its own at the local player and switches the others off. It makes itself when the
    /// game starts, so no scene needs to hold it.
    /// </summary>
    public sealed class AudioListenerFallback : MonoBehaviour
    {
        private AudioListener _listener;
        private FixedFollowCamera _camera;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            GameObject go = new GameObject("Audio Listener (fallback)");
            go.AddComponent<AudioListenerFallback>();
            DontDestroyOnLoad(go);
        }

        private void Awake() => _listener = gameObject.AddComponent<AudioListener>();

        private void LateUpdate()
        {
            // another enabled listener (the network code's own) wins: exactly one, or Unity warns
            bool other = false;
            foreach (AudioListener l in FindObjectsByType<AudioListener>())
                if (l != _listener && l.enabled && l.gameObject.activeInHierarchy) { other = true; break; }
            _listener.enabled = !other;
            if (other)
                return;

            if (_camera == null)
                _camera = FindAnyObjectByType<FixedFollowCamera>();
            Camera cam = Camera.main;
            Transform target = _camera != null ? _camera.target : null;
            transform.position = target != null ? target.position : cam != null ? cam.transform.position : Vector3.zero;
            // turned with the camera, so left and right in the speakers match left and right on screen
            transform.rotation = Quaternion.Euler(0f, cam != null ? cam.transform.eulerAngles.y : 0f, 0f);
        }
    }
}

using KnockKnockArena.Shared.Simulation;
using UnityEngine;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// Module 8.2: shows the raycast a shot would take right now. A line goes from your player's
    /// centre towards the aim point and stops at the nearest other player, a wall or the map's
    /// edge; a cross marks the hitpoint (red on a player, white on a wall or the edge).
    ///
    /// It uses the shared Raycast, the same code the server judges shots with from 8.3. The
    /// server does not shoot yet, so this is only the client's own calculation, without spread.
    /// F3 turns it on and off. It needs no setup: it creates itself when the scene loads.
    /// </summary>
    public sealed class RaycastPreview : MonoBehaviour
    {
        private const float LineY = 1f;          // above the floor, at the players' middle
        private const float CrossSize = 0.6f;

        private NetworkBootstrap _bootstrap;
        private Transform _localPlayer;
        private LineRenderer _line;
        private LineRenderer _cross;
        private bool _visible = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateInScene()
        {
            new GameObject(nameof(RaycastPreview)).AddComponent<RaycastPreview>();
        }

        private void Start()
        {
            _line = NewLine("line", 0.12f, 2);
            _cross = NewLine("hitpoint", 0.12f, 5);
            _bootstrap = FindAnyObjectByType<NetworkBootstrap>();
            if (_bootstrap != null)
                _bootstrap.LocalPlayerSpawned.AddListener(view => _localPlayer = view);
        }

        private LineRenderer NewLine(string name, float width, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.widthMultiplier = width;
            line.positionCount = points;
            line.useWorldSpace = true;
            line.enabled = false;
            return line;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3))
                _visible = !_visible;

            bool show = _visible && _localPlayer != null && _bootstrap.AimSource != null &&
                        _bootstrap.AimSource.HasAim && ArenaMap.IsLoaded;
            _line.enabled = _cross.enabled = false;
            if (!show)
                return;

            // The direction is the aim point minus the player's centre, normalized to length 1,
            // so the raycast's distance t is in metres (the same as the server will do).
            float fromX = _localPlayer.position.x, fromZ = _localPlayer.position.z;
            Vector3 aim = _bootstrap.AimSource.AimPoint;
            float dirX = aim.x - fromX, dirZ = aim.z - fromZ;
            float length = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
            if (length < GameConstants.DeadZoneMeters)
                return;
            dirX /= length;
            dirZ /= length;

            // The smallest t wins: the map's edge first, then every wall and every other player.
            float t = Raycast.ArenaHitDistance(fromX, fromZ, dirX, dirZ);
            bool hitPlayer = false;
            foreach (Aabb wall in ArenaMap.Walls)
            {
                if (Raycast.RayIntersectsAabb(fromX, fromZ, dirX, dirZ,
                        wall.MinX, wall.MinZ, wall.MaxX, wall.MaxZ, out float d) && d < t)
                {
                    t = d;
                    hitPlayer = false;
                }
            }
            const float half = ArenaMap.PlayerHalfExtent;
            foreach (Vector3 p in OtherPlayers())
            {
                if (Raycast.RayIntersectsAabb(fromX, fromZ, dirX, dirZ,
                        p.x - half, p.z - half, p.x + half, p.z + half, out float d) && d < t)
                {
                    t = d;
                    hitPlayer = true;
                }
            }

            float y = ArenaMap.GroundY + LineY;
            var from = new Vector3(fromX, y, fromZ);
            var hit = new Vector3(fromX + dirX * t, y, fromZ + dirZ * t);
            Color color = hitPlayer ? new Color(1f, 0.25f, 0.25f) : Color.white;

            _line.startColor = _line.endColor = new Color(1f, 0.85f, 0.2f, 0.8f);
            _line.SetPosition(0, from);
            _line.SetPosition(1, hit);
            _line.enabled = true;

            float s = CrossSize;
            _cross.startColor = _cross.endColor = color;
            _cross.SetPosition(0, hit + new Vector3(-s, 0f, -s));
            _cross.SetPosition(1, hit + new Vector3(s, 0f, s));
            _cross.SetPosition(2, hit);
            _cross.SetPosition(3, hit + new Vector3(-s, 0f, s));
            _cross.SetPosition(4, hit + new Vector3(s, 0f, -s));
            _cross.enabled = true;
        }

        // The other players: every character in the scene except your own.
        private System.Collections.Generic.IEnumerable<Vector3> OtherPlayers()
        {
            foreach (Gameplay.CharacterVisual visual in FindObjectsByType<Gameplay.CharacterVisual>())
            {
                if (visual.transform != _localPlayer)
                    yield return visual.transform.position;
            }
        }
    }
}

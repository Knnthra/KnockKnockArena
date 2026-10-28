using UnityEngine;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// Placeholder melee swing: a fading arc in front of the player matching the
    /// server's actual hit check (same reach incl. the target half extent, same
    /// arc angle from the shared weapon table) — the cosmetic shows honestly what
    /// the server tests. Whether anything was HIT is the server's call alone.
    /// </summary>
    public sealed class MeleeSwingEffect : MonoBehaviour
    {
        private const float LifetimeSeconds = 0.15f;
        private const int Segments = 12;

        private LineRenderer _line;
        private float _age;

        public static void Spawn(Vector3 origin, float directionX, float directionZ, float reachMeters, float arcDegrees)
        {
            GameObject go = new GameObject("Melee Swing");
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.positionCount = Segments;
            line.startWidth = 0.1f;
            line.endWidth = 0.1f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = new Color(1f, 1f, 1f, 0.9f);
            line.endColor = new Color(1f, 1f, 1f, 0.9f);

            float baseAngle = Mathf.Atan2(directionZ, directionX);
            float halfArc = arcDegrees * 0.5f * Mathf.Deg2Rad;
            for (int i = 0; i < Segments; i++)
            {
                float angle = baseAngle - halfArc + 2f * halfArc * i / (Segments - 1);
                line.SetPosition(i, new Vector3(
                    origin.x + Mathf.Cos(angle) * reachMeters,
                    origin.y,
                    origin.z + Mathf.Sin(angle) * reachMeters));
            }

            go.AddComponent<MeleeSwingEffect>()._line = line;
        }

        private void Update()
        {
            _age += Time.deltaTime;
            float alpha = Mathf.Clamp01(1f - _age / LifetimeSeconds) * 0.9f;
            Color color = _line.startColor;
            color.a = alpha;
            _line.startColor = color;
            _line.endColor = color;
            if (_age >= LifetimeSeconds)
                Destroy(gameObject);
        }
    }
}

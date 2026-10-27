using UnityEngine;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// Placeholder hitscan tracer: a thin line from the muzzle along the aim
    /// direction, clipped against the arena geometry, fading out over a tenth of a
    /// second. Pure "favor the shooter" COSMETICS (Video 6.7): it is drawn the
    /// moment the client fires, along the aim direction WITHOUT the server's spread
    /// roll — whether anything was hit is decided by the server alone.
    /// </summary>
    public sealed class TracerEffect : MonoBehaviour
    {
        private const float LifetimeSeconds = 0.1f;

        private LineRenderer _line;
        private float _age;

        public static void Spawn(Vector3 from, Vector3 to)
        {
            GameObject go = new GameObject("Tracer");
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.startWidth = 0.06f;
            line.endWidth = 0.03f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = new Color(1f, 0.95f, 0.6f);
            line.endColor = new Color(1f, 0.95f, 0.6f, 0.4f);
            go.AddComponent<TracerEffect>()._line = line;
        }

        private void Update()
        {
            _age += Time.deltaTime;
            float alpha = Mathf.Clamp01(1f - _age / LifetimeSeconds);
            Color start = _line.startColor;
            start.a = alpha;
            _line.startColor = start;
            Color end = _line.endColor;
            end.a = alpha * 0.4f;
            _line.endColor = end;
            if (_age >= LifetimeSeconds)
                Destroy(gameObject);
        }
    }
}

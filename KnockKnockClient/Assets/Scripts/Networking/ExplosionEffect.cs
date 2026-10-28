using UnityEngine;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// Placeholder explosion visual: an expanding sphere that grows to the splash
    /// diameter (7 m) and disappears. Spawned from the RocketExploded TCP EVENT —
    /// deliberately not from the entity despawning in the snapshot (Video 6.7:
    /// effects play on events; snapshots only carry state).
    /// </summary>
    public sealed class ExplosionEffect : MonoBehaviour
    {
        private const float LifetimeSeconds = 0.4f;
        private const float FullDiameter = 7f; // 2 x splash radius (3.5 m)

        private float _age;

        public static void Spawn(Vector3 position)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Explosion";
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().material.color = new Color(1f, 0.55f, 0.1f);
            go.transform.position = position;
            go.transform.localScale = Vector3.one * 0.5f;
            go.AddComponent<ExplosionEffect>();
        }

        private void Update()
        {
            _age += Time.deltaTime;
            transform.localScale = Vector3.one * Mathf.Lerp(0.5f, FullDiameter, _age / LifetimeSeconds);
            if (_age >= LifetimeSeconds)
                Destroy(gameObject);
        }
    }
}

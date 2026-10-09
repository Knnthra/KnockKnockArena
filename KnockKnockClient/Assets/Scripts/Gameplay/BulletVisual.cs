using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// A bullet flying from the muzzle to where the shot ends, the way the source
    /// game draws its projectiles.
    ///
    /// IT IS PURELY A PICTURE. The server settles a hitscan shot the instant it is
    /// fired, rewinding to the tick the shooter saw — so by the time this bullet is
    /// halfway there, the damage has already been dealt. Nothing about this object
    /// decides anything; it exists so a shot reads as a travelling round instead of
    /// an instant line. That means you can occasionally see a target fall before the
    /// bullet reaches it, which is the honest price of instant hit detection.
    ///
    /// Part of the student base project: it moves a mesh in a straight line.
    /// </summary>
    public sealed class BulletVisual : MonoBehaviour
    {
        private Vector3 _target;
        private float _speed;
        private float _remainingLifetime;
        private GameObject _hitEffect;

        /// <summary>
        /// Sends a bullet from <paramref name="from"/> to <paramref name="to"/>.
        /// Falls back to doing nothing without a prefab, so a weapon with no bullet
        /// art simply shows no projectile rather than throwing.
        /// </summary>
        public static void Spawn(GameObject prefab, Vector3 from, Vector3 to, float speed,
            GameObject hitEffect = null)
        {
            if (prefab == null || speed <= 0f)
                return;

            Vector3 direction = to - from;
            if (direction.sqrMagnitude < 0.0001f)
                return;

            GameObject go = Instantiate(prefab, from, Quaternion.LookRotation(direction));
            foreach (Collider collider in go.GetComponentsInChildren<Collider>())
                Destroy(collider); // it must never push anything around

            BulletVisual bullet = go.AddComponent<BulletVisual>();
            bullet._target = to;
            bullet._speed = speed;
            bullet._hitEffect = hitEffect;
            // Generous ceiling: the bullet normally dies on arrival, this only stops
            // one lingering forever if it somehow overshoots.
            bullet._remainingLifetime = direction.magnitude / speed + 1f;
        }

        private void Update()
        {
            _remainingLifetime -= Time.deltaTime;
            if (_remainingLifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            float step = _speed * Time.deltaTime;
            Vector3 toTarget = _target - transform.position;
            if (toTarget.sqrMagnitude <= step * step)
            {
                Land();
                return;
            }
            transform.position += toTarget.normalized * step;
        }

        /// <summary>Leaves the hit effect behind and disappears. The effect cleans
        /// itself up once its particles have finished.</summary>
        private void Land()
        {
            if (_hitEffect != null)
            {
                GameObject effect = Instantiate(_hitEffect, transform.position, transform.rotation);
                Destroy(effect, 3f);
            }
            Destroy(gameObject);
        }
    }
}

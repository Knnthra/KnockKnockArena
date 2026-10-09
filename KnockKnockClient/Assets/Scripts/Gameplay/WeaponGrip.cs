using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// How a weapon model sits in a character's hand. Put this on the weapon prefab
    /// and nudge the values until the grip looks right — same idea as the Hand Grip
    /// Offsets on KnockKnock's Item, trimmed to what a held model needs.
    ///
    /// PURELY COSMETIC. The weapon a player carries is decided by the server and
    /// arrives in the snapshot; this only says where the model hangs.
    /// </summary>
    public sealed class WeaponGrip : MonoBehaviour
    {
        [Tooltip("Local position inside the hand transform.")]
        public Vector3 Position;

        [Tooltip("Local euler rotation inside the hand transform.")]
        public Vector3 Rotation;

        [Tooltip("Uniform scale applied to the held model.")]
        public float Scale = 1f;

        [Tooltip("Where the barrel ends. Tracers and muzzle effects start here instead " +
                 "of at the player's feet. Leave empty and the model's forward edge is " +
                 "used. The SERVER still traces from the player's centre — this is the " +
                 "cosmetic origin only, which is why a shot can look like it left the " +
                 "barrel while the hit is judged from the body.")]
        public Transform Muzzle;

        [Tooltip("Projectile drawn flying out of the barrel. Cosmetic only — the shot " +
                 "itself is settled the instant it is fired. Empty = draw a tracer line.")]
        public GameObject ProjectilePrefab;

        [Tooltip("How fast that projectile flies, in metres per second. Low enough to " +
                 "see, high enough not to lag visibly behind the hit.")]
        public float ProjectileSpeed = 80f;

        [Tooltip("Spawned where the projectile lands. Cosmetic, like the projectile " +
                 "itself — it marks where the shot LOOKED like it went, not where the " +
                 "server judged the hit.")]
        public GameObject HitEffectPrefab;

        [Header("Sound")]
        [Tooltip("Played at the muzzle on every shot, for yours and other players' alike " +
                 "(3D — you hear others only when they are near). One is picked at random.")]
        public AudioClip[] FireSounds;
        [Range(0f, 10f)] public float FireVolume = 0.5f;

        [Tooltip("Played where the shot lands — for the rocket launcher, the explosion. " +
                 "One is picked at random.")]
        public AudioClip[] ImpactSounds;
        [Range(0f, 10f)] public float ImpactVolume = 1f;

        /// <summary>World position shots appear to come from.</summary>
        public Vector3 MuzzleWorldPosition
        {
            get
            {
                if (Muzzle != null)
                    return Muzzle.position;

                // No muzzle marked: use the forward edge of whatever is rendered.
                Renderer[] renderers = GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0)
                    return transform.position;

                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    bounds.Encapsulate(renderers[i].bounds);
                return bounds.center + transform.forward * bounds.extents.magnitude * 0.5f;
            }
        }
    }
}

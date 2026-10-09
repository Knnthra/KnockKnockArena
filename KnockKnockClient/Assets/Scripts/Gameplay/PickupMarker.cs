using System.Collections.Generic;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;
using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// Marks a spot in the scene as a pickup, with its type. The Arena exporter reads
    /// every PickupMarker in the scene and writes its world XZ position + type into
    /// ArenaMap. Put it on the pickup's visual GameObject (or an empty marker).
    ///
    /// Authoring content (base project), NOT netcode — only the world position and
    /// the enum matter; the mesh under it is cosmetic.
    /// </summary>
    public sealed class PickupMarker : MonoBehaviour
    {
        [Tooltip("Which pickup this spot spawns. Order in the exported list follows scene hierarchy order.")]
        public PickupType Type = PickupType.Health;

        [Tooltip("For a WEAPON pickup: the model the player ends up holding. The pickup " +
                 "knows what it hands over, so adding a weapon to the game means building " +
                 "its pickup — nothing has to be registered anywhere else.")]
        public GameObject HeldPrefab;

        // Every weapon pickup announces what it grants, so the held model can be
        // resolved from a weapon id alone (a remote player's weapon arrives as an id
        // in the snapshot, not as a prefab). Built from the scene, never maintained
        // by hand: place a pickup and its weapon is known.
        private static readonly Dictionary<WeaponId, GameObject> HeldByWeapon =
            new Dictionary<WeaponId, GameObject>();

        /// <summary>
        /// Statics survive leaving play mode when domain reloading is off, so a
        /// registry built from the scene must be emptied before the scene rebuilds it.
        /// Otherwise a weapon whose pickup you deleted keeps showing its old model.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry() => HeldByWeapon.Clear();

        /// <summary>The model to put in a player's hand for this weapon, or null when
        /// nothing grants it (bare fists, or art not made yet).</summary>
        public static GameObject HeldPrefabFor(WeaponId weapon) =>
            HeldByWeapon.TryGetValue(weapon, out GameObject prefab) ? prefab : null;

        /// <summary>
        /// Registers a weapon model that no pickup hands out — the weapon players
        /// SPAWN with. Everything else registers itself from its pickup; this exists
        /// because the spawn weapon is never lying on the map to be picked up.
        /// </summary>
        public static void RegisterHeldPrefab(WeaponId weapon, GameObject prefab)
        {
            if (weapon != WeaponId.Punch && prefab != null)
                HeldByWeapon[weapon] = prefab;
        }

        /// <summary>The weapon a pickup type hands over; Punch means "not a weapon".</summary>
        public static WeaponId WeaponFor(PickupType type) => type switch
        {
            PickupType.Ak47Weapon => WeaponId.Ak47,
            PickupType.RocketLauncherWeapon => WeaponId.RocketLauncher,
            _ => WeaponId.Punch,
        };

        private void Awake()
        {
            WeaponId weapon = WeaponFor(Type);
            if (weapon != WeaponId.Punch && HeldPrefab != null)
                HeldByWeapon[weapon] = HeldPrefab;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Type == PickupType.Health ? Color.green
                : (Type == PickupType.Ak47Weapon || Type == PickupType.RocketLauncherWeapon)
                    ? new Color(0.9f, 0.8f, 0.1f)
                    : new Color(0.5f, 0.5f, 1f);
            // The real radius, read from the shared constant — a gizmo that lies about
            // the reach is worse than no gizmo.
            Gizmos.DrawWireSphere(transform.position, GameConstants.PickupRadius);
        }
    }
}

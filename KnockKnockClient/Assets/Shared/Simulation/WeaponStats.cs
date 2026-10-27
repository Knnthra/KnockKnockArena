using System;
using System.Collections.Generic;
using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Shared.Simulation
{
    /// <summary>
    /// The weapon table (S01 2.3), loaded from weapons.json so a group can retune a
    /// weapon — or add one — without recompiling anything. The values are published
    /// verbatim on the wiki so clients can mirror the spread model for their crosshair.
    ///
    /// Unlike the arena, this table does not feed client prediction: movement is the
    /// only thing predicted, and no weapon value enters it. The client loads the table
    /// for its crosshair and its action bar; the server's copy is the one that decides
    /// damage.
    /// </summary>
    public readonly struct WeaponStats
    {
        public readonly bool IsMelee;
        public readonly byte Damage;
        public readonly float CooldownSeconds;

        /// <summary>How good the weapon is, for auto-switching on pickup: walking over
        /// a weapon better than the one in your hands draws it immediately, a worse one
        /// is stowed. Higher wins; ties keep what you are holding. Tune it freely — it
        /// only decides what you switch to, never what a weapon does.</summary>
        public readonly byte Priority;

        // Spread bloom model (hitscan only): current spread = BaseSpreadDeg + bloom,
        // bloom grows SpreadPerShotDeg per shot (capped so the total never exceeds
        // MaxSpreadDeg) and recovers SpreadRecoveryDegPerSec.
        public readonly float BaseSpreadDeg;
        public readonly float SpreadPerShotDeg;
        public readonly float MaxSpreadDeg;
        public readonly float SpreadRecoveryDegPerSec;

        // Melee only.
        public readonly float MeleeRangeMeters;
        public readonly float MeleeArcDeg;

        /// <summary>How fast this weapon's PROJECTILE flies, in m/s. Only meaningful
        /// for projectile weapons (the rocket launcher); 0 for everything else.
        /// Tunable in weapons.json like the rest of the balance.</summary>
        public readonly float ProjectileSpeed;

        /// <summary>
        /// Where this weapon's muzzle sits relative to the player's CENTRE, in the
        /// player's own frame: forward along the aim, and right of it.
        ///
        /// The sideways number is the one that matters and the one that is easy to
        /// forget — the weapon is held in a hand, off to one side, not bolted to the
        /// player's midline. Firing a projectile from the centre puts it visibly
        /// beside the barrel.
        ///
        /// Measured against the MODEL, not guessed from metres (FEJLLOG #39): read the
        /// weapon hand's local position off the character, and add the muzzle's local
        /// offset times the grip scale. Only projectile weapons need it; a hitscan
        /// shot is judged from the centre and only drawn from the barrel.
        /// </summary>
        public readonly float MuzzleForwardMeters;
        public readonly float MuzzleRightMeters;

        public WeaponStats(bool isMelee, byte damage, float cooldownSeconds, byte priority,
            float baseSpreadDeg, float spreadPerShotDeg, float maxSpreadDeg, float spreadRecoveryDegPerSec,
            float meleeRangeMeters, float meleeArcDeg,
            float muzzleForwardMeters = 0f, float muzzleRightMeters = 0f,
            float projectileSpeed = 0f)
        {
            MuzzleForwardMeters = muzzleForwardMeters;
            MuzzleRightMeters = muzzleRightMeters;
            ProjectileSpeed = projectileSpeed;
            IsMelee = isMelee;
            Damage = damage;
            CooldownSeconds = cooldownSeconds;
            Priority = priority;
            BaseSpreadDeg = baseSpreadDeg;
            SpreadPerShotDeg = spreadPerShotDeg;
            MaxSpreadDeg = maxSpreadDeg;
            SpreadRecoveryDegPerSec = spreadRecoveryDegPerSec;
            MeleeRangeMeters = meleeRangeMeters;
            MeleeArcDeg = meleeArcDeg;
        }

        public static WeaponStats For(WeaponId weapon) => Table[(int)weapon];

        private static WeaponStats[]? _table;

        public static bool IsLoaded => _table != null;

        /// <summary>Indexed by WeaponId, loaded from weapons.json.</summary>
        public static WeaponStats[] Table =>
            _table ?? throw new InvalidOperationException(
                "No weapon table loaded. Call WeaponStats.Load(WeaponStats.ParseTable(json)) first — " +
                "the server does this at startup from --weapons, the client after fetching /api/config.");

        public static void Load(WeaponStats[] table)
        {
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            if (table.Length != WeaponCount)
                throw new ArgumentException(
                    $"the weapon table must hold exactly {WeaponCount} entries, one per WeaponId", nameof(table));
            _table = table;
        }

        /// <summary>
        /// Reads weapons.json. The entries must appear in WeaponId order and be named,
        /// so a file that has drifted out of step with the enum says so at load instead
        /// of quietly giving the Glock the rocket launcher's damage.
        /// </summary>
        public static WeaponStats[] ParseTable(string json)
        {
            IReadOnlyList<JsonValue> entries = Json.Parse(json)["weapons"].Items;
            if (entries.Count != WeaponCount)
                throw new JsonException(
                    $"\"weapons\" holds {entries.Count} entries but there are {WeaponCount} weapon ids");

            WeaponStats[] table = new WeaponStats[WeaponCount];
            for (int i = 0; i < entries.Count; i++)
            {
                JsonValue entry = entries[i];
                string expected = ((WeaponId)i).ToString();
                string id = entry["id"].AsString();
                if (!string.Equals(id, expected, StringComparison.Ordinal))
                    throw new JsonException(
                        $"weapon {i} is \"{id}\" but weapon id {i} is {expected}; " +
                        "the entries must be in WeaponId order");

                int damage = entry["damage"].AsInt();
                if (damage < 0 || damage > 255)
                    throw new JsonException($"weapon \"{id}\": damage must be 0-255");
                int priority = entry["priority"].AsInt();
                if (priority < 0 || priority > 255)
                    throw new JsonException($"weapon \"{id}\": priority must be 0-255");

                table[i] = new WeaponStats(
                    entry["melee"].AsBool(),
                    (byte)damage,
                    entry["cooldownSeconds"].AsFloat(),
                    (byte)priority,
                    OptionalFloat(entry, "baseSpreadDeg"),
                    OptionalFloat(entry, "spreadPerShotDeg"),
                    OptionalFloat(entry, "maxSpreadDeg"),
                    OptionalFloat(entry, "spreadRecoveryDegPerSec"),
                    OptionalFloat(entry, "meleeRangeMeters"),
                    OptionalFloat(entry, "meleeArcDeg"),
                    OptionalFloat(entry, "muzzleForwardMeters"),
                    OptionalFloat(entry, "muzzleRightMeters"),
                    OptionalFloat(entry, "projectileSpeed"));
            }
            return table;
        }

        /// <summary>Melee entries leave the spread fields out and vice versa; absent
        /// means zero rather than an error.</summary>
        private static float OptionalFloat(JsonValue entry, string name)
        {
            JsonValue? value = entry.Optional(name);
            return value == null || value.Kind == JsonKind.Null ? 0f : value.AsFloat();
        }

        /// <summary>Number of weapon ids — the table must cover every one of them.</summary>
        public static readonly int WeaponCount = Enum.GetValues(typeof(WeaponId)).Length;
    }

    /// <summary>
    /// The spread bloom model. The SERVER owns the bloom state and rolls the random
    /// deviation per shot; the client runs the same functions purely cosmetically to
    /// size its crosshair (no randomness client-side — only the bloom value).
    /// </summary>
    public static class SpreadModel
    {
        /// <summary>Total spread in degrees at a given bloom level.</summary>
        public static float CurrentSpreadDeg(WeaponId weapon, float bloom)
        {
            return WeaponStats.For(weapon).BaseSpreadDeg + bloom;
        }

        /// <summary>Bloom recovery, applied every tick.</summary>
        public static float Decay(WeaponId weapon, float bloom, float deltaTime)
        {
            float recovered = bloom - WeaponStats.For(weapon).SpreadRecoveryDegPerSec * deltaTime;
            return recovered > 0f ? recovered : 0f;
        }

        /// <summary>Bloom growth from firing one shot.</summary>
        public static float OnShot(WeaponId weapon, float bloom)
        {
            WeaponStats stats = WeaponStats.For(weapon);
            float grown = bloom + stats.SpreadPerShotDeg;
            float cap = stats.MaxSpreadDeg - stats.BaseSpreadDeg;
            return grown < cap ? grown : cap;
        }
    }
}

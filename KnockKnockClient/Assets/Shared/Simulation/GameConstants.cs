namespace KnockKnockArena.Shared.Simulation
{
    /// <summary>
    /// Gameplay constants shared by server and client. Values marked [PROPOSAL] in
    /// S01 are start values that may change during playtesting; they are locked and
    /// published on the wiki when protocol v1 is frozen.
    /// </summary>
    public static class GameConstants
    {
        /// <summary>Server ticks (and snapshots) per second.</summary>
        public const int TickRate = 30;

        /// <summary>Fixed simulation time step in seconds.</summary>
        public const float TickDeltaTime = 1f / TickRate;

        /// <summary>Player movement speed in m/s (run — the default). [PROPOSAL]</summary>
        public const float MoveSpeed = 6f;

        /// <summary>Movement speed with the Walk modifier (Ctrl). [PROPOSAL]</summary>
        public const float WalkSpeed = 3f;

        /// <summary>Movement speed with the Sprint modifier (Shift, forward only). [PROPOSAL]</summary>
        public const float SprintSpeed = 9f;

        // Dash (ported from the KnockKnock character: 5 m leap, ~0.55 s, 1 s cooldown).
        // Duration is expressed in whole ticks so the rule is exactly replicable;
        // speed is derived so the dash covers exactly DashDistance meters. [PROPOSAL]
        public const float DashDistance = 10f;
        public const byte DashDurationTicks = 16;
        public const byte DashCooldownTicks = 150;
        public const float DashSpeed = DashDistance / (DashDurationTicks * TickDeltaTime);

        /// <summary>Below this squared aim distance the facing is undefined (cursor on
        /// top of the player) and movement falls back to world-axis WASD.</summary>
        public const float FacingDeadZoneSquared = 0.01f;

        /// <summary>Seconds without any UDP packet before a player is disconnected. [PROPOSAL]</summary>
        public const float UdpTimeoutSeconds = 5f;

        /// <summary>Lag compensation history per player, in ticks (1 second). [PROPOSAL]</summary>
        public const int RewindBufferTicks = 30;

        /// <summary>Minimum time dead before the fire button respawns you. [PROPOSAL]</summary>
        public const float RespawnDelaySeconds = 2f;

        /// <summary>Aim points closer than this are rejected (the shot direction becomes
        /// meaningless when the cursor is on top of the player). [PROPOSAL]</summary>
        public const float DeadZoneMeters = 2f;

        /// <summary>Glock ammo at spawn/respawn. [PROPOSAL]</summary>
        public const ushort GlockStartAmmo = 24;

        public const byte MaxHealth = 100;

        // Rocket (S01 2.4). All [PROPOSAL].

        /// <summary>FALLBACK rocket speed. The real number lives in weapons.json as
        /// the rocket launcher's projectileSpeed — tune it there, like the fire
        /// rates. This constant only catches a table that omits the field.</summary>
        public const float RocketSpeed = 18f;

        public const float RocketSplashRadius = 3.5f;
        public const int RocketSplashMaxDamage = 60;

        // Pickups (S01 2.6). All [PROPOSAL].
        /// <summary>Measured from the player's CENTRE, so it has to clear the player's
        /// own half extent (1.15 m) before anything is picked up — a radius near that
        /// means walking into a pickup does nothing until you are on top of it.</summary>
        public const float PickupRadius = 2.0f;
        public const float WeaponPickupRespawnSeconds = 20f;
        public const float AmmoPickupRespawnSeconds = 15f;
        public const float HealthPickupRespawnSeconds = 25f;
        public const byte HealthPickupAmount = 25;
        public const ushort GlockAmmoPickupAmount = 12;
        public const ushort Ak47AmmoPickupAmount = 30;
        public const ushort RocketAmmoPickupAmount = 2;

        // Entity id ranges in the snapshot: players use their player id (1-99),
        // pickups use PickupEntityIdBase + pickup index, rockets count up from
        // RocketEntityIdBase (and wrap eventually - ids are only unique while live).
        public const ushort PickupEntityIdBase = 100;
        public const ushort RocketEntityIdBase = 1000;
    }
}

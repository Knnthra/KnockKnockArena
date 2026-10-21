using System;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Shared.Simulation
{
    /// <summary>
    /// The movement rule: kinematic, fixed speed, XZ plane, then collision against
    /// the arena geometry. Documented as pseudocode on the wiki page
    /// "Protocol: Map &amp; Collision" — the wiki text and this code MUST stay in sync.
    ///
    /// This EXACT code runs on both the server (authoritative simulation) and the
    /// client (prediction). Client-side prediction only works if both sides compute
    /// bit-identical results for the same input, so: do not reimplement this,
    /// reference it. Every operation here is plain IEEE 754 float math in a fixed
    /// order — that is what makes the result reproducible across both sides.
    /// </summary>
    public static class MovementSimulation
    {
        /// <summary>
        /// Per-player dash state. Part of the AUTHORITATIVE simulation state: the
        /// server owns it, the snapshot carries it, and the client seeds prediction
        /// replay from it — exactly like the position.
        /// </summary>
        public struct DashState
        {
            /// <summary>Remaining dash ticks; the player is dashing while &gt; 0.</summary>
            public byte TicksLeft;

            /// <summary>Ticks until a new dash may start (counted from dash START).</summary>
            public byte CooldownTicks;

            /// <summary>Normalized dash direction, locked at dash start.</summary>
            public float DirectionX;
            public float DirectionZ;
        }

        /// <summary>
        /// Advances a player position by one input worth one fixed time step.
        ///
        /// Movement is CHARACTER-RELATIVE (ported from the KnockKnock character):
        /// the player faces the aim point, W moves toward it, A/D strafe. Speed is
        /// MoveSpeed, or WalkSpeed with Walk, or SprintSpeed with Sprint while the
        /// net movement is forward. Dash (edge-triggered) locks the current move
        /// direction for DashDurationTicks at DashSpeed and ignores other input.
        /// </summary>
        public static void Step(ref float x, ref float z, ref DashState dash,
                                MovementBits movement, float aimX, float aimZ, float deltaTime)
        {
            if (dash.CooldownTicks > 0)
                dash.CooldownTicks--;

            float inputRight = 0f;
            float inputForward = 0f;

            if ((movement & MovementBits.W) != 0) inputForward += 1f;
            if ((movement & MovementBits.S) != 0) inputForward -= 1f;
            if ((movement & MovementBits.A) != 0) inputRight -= 1f;
            if ((movement & MovementBits.D) != 0) inputRight += 1f;

            // EVERY operation below is wrapped in an explicit (float) cast. The CLR
            // spec allows runtimes to keep intermediates in higher precision (Unity's
            // Mono computes float chains in double), which produced rare 1-ulp
            // divergence from the server during wall slides (FEJLLOG #32). An
            // explicit cast forces a round-to-float32 at every step on every
            // conforming runtime — THAT is what makes the rule bit-replicable.
            float directionX = 0f;
            float directionZ = 0f;

            if (inputRight != 0f || inputForward != 0f)
            {
                // Facing = from the player toward the aim point. Within the dead
                // zone (cursor on top of the player) the facing is undefined and
                // movement falls back to world-axis WASD.
                float facingX = (float)(aimX - x);
                float facingZ = (float)(aimZ - z);
                float facingLengthSquared = (float)((float)(facingX * facingX) + (float)(facingZ * facingZ));

                if (facingLengthSquared >= GameConstants.FacingDeadZoneSquared)
                {
                    float facingLength = (float)Math.Sqrt(facingLengthSquared);
                    facingX = (float)(facingX / facingLength);
                    facingZ = (float)(facingZ / facingLength);

                    // right = (facingZ, -facingX); direction = forward*v + right*h.
                    directionX = (float)((float)(facingX * inputForward) + (float)(facingZ * inputRight));
                    directionZ = (float)((float)(facingZ * inputForward) - (float)(facingX * inputRight));
                }
                else
                {
                    directionX = inputRight;
                    directionZ = inputForward;
                }

                // Normalize so diagonal movement is not faster than straight movement.
                float lengthSquared = (float)((float)(directionX * directionX) + (float)(directionZ * directionZ));
                if (lengthSquared > 1f)
                {
                    float length = (float)Math.Sqrt(lengthSquared);
                    directionX = (float)(directionX / length);
                    directionZ = (float)(directionZ / length);
                }
            }

            bool isMoving = directionX != 0f || directionZ != 0f;

            // Dash start: edge-triggered bit, only while moving, not already
            // dashing, cooldown expired. The direction is locked for the whole dash.
            if ((movement & MovementBits.Dash) != 0 && isMoving
                && dash.TicksLeft == 0 && dash.CooldownTicks == 0)
            {
                dash.TicksLeft = GameConstants.DashDurationTicks;
                dash.CooldownTicks = GameConstants.DashCooldownTicks;
                dash.DirectionX = directionX;
                dash.DirectionZ = directionZ;
            }

            if (dash.TicksLeft > 0)
            {
                dash.TicksLeft--;
                x = (float)(x + (float)((float)(dash.DirectionX * GameConstants.DashSpeed) * deltaTime));
                z = (float)(z + (float)((float)(dash.DirectionZ * GameConstants.DashSpeed) * deltaTime));
            }
            else if (isMoving)
            {
                float speed = GameConstants.MoveSpeed;
                bool walking = (movement & MovementBits.Walk) != 0;
                bool forwardOnly = (movement & MovementBits.W) != 0 && (movement & MovementBits.S) == 0;

                if (walking)
                    speed = GameConstants.WalkSpeed;
                else if ((movement & MovementBits.Sprint) != 0 && forwardOnly)
                    speed = GameConstants.SprintSpeed;

                x = (float)(x + (float)((float)(directionX * speed) * deltaTime));
                z = (float)(z + (float)((float)(directionZ * speed) * deltaTime));
            }

            // Collision runs even without movement input: a respawn or a future
            // knockback must also resolve against the geometry.
            ResolveCollisions(ref x, ref z);
        }

        /// <summary>
        /// The collision rule (S01 4.2): clamp to the arena bounds, then push out of
        /// each overlapping wall along the axis with the smallest penetration, walls
        /// visited in the fixed order of <see cref="ArenaMap.Walls"/>.
        ///
        /// The player collides as a 1x1 m axis-aligned square (the bounding square of
        /// the original 0.5 m circle). A true circle cannot be resolved along "the
        /// shortest axis" at corners, so the shape was simplified to keep the rule
        /// exactly replicable — see FEJLLOG entry 12.
        /// </summary>
        public static void ResolveCollisions(ref float x, ref float z)
        {
            const float half = ArenaMap.PlayerHalfExtent;

            // 1) Clamp to the arena bounds, inset by the player's half extent.
            if (x < ArenaMap.BoundsMinX + half) x = ArenaMap.BoundsMinX + half;
            if (x > ArenaMap.BoundsMaxX - half) x = ArenaMap.BoundsMaxX - half;
            if (z < ArenaMap.BoundsMinZ + half) z = ArenaMap.BoundsMinZ + half;
            if (z > ArenaMap.BoundsMaxZ - half) z = ArenaMap.BoundsMaxZ - half;

            // 2) Push out of each wall, in the published wall order.
            for (int i = 0; i < ArenaMap.Walls.Length; i++)
            {
                Aabb wall = ArenaMap.Walls[i];

                // Expand the wall by the player's half extent; the player is then a
                // point against the expanded box (Minkowski sum).
                float minX = wall.MinX - half;
                float maxX = wall.MaxX + half;
                float minZ = wall.MinZ - half;
                float maxZ = wall.MaxZ + half;

                bool inside = x > minX && x < maxX && z > minZ && z < maxZ;
                if (!inside)
                    continue;

                // Penetration depth toward each of the four faces. The smallest push
                // wins; ties break in this exact order: west, east, south, north.
                float pushWest = x - minX;
                float pushEast = maxX - x;
                float pushSouth = z - minZ;
                float pushNorth = maxZ - z;

                float smallest = pushWest;
                int face = 0;
                if (pushEast < smallest) { smallest = pushEast; face = 1; }
                if (pushSouth < smallest) { smallest = pushSouth; face = 2; }
                if (pushNorth < smallest) { smallest = pushNorth; face = 3; }

                switch (face)
                {
                    case 0: x = minX; break;
                    case 1: x = maxX; break;
                    case 2: z = minZ; break;
                    case 3: z = maxZ; break;
                }
            }
        }
    }
}

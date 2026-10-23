using System.Collections.Generic;

namespace KnockKnockArena.Networking
{
    /// <summary>
    /// Position and aim history for ONE remote entity, keyed by SERVER TICK (M07).
    /// Remote entities are rendered a few ticks in the past, interpolated between
    /// the two snapshots that straddle the render tick.
    ///
    /// Keying on the server-tick timeline (not local arrival time) means network
    /// jitter does not leak into the motion: the samples sit at exact 1-tick
    /// spacing no matter how unevenly the packets arrived (FEJLLOG #15).
    ///
    /// The aim point is interpolated exactly like the position, so a remote figure
    /// TURNS smoothly instead of snapping to a new facing 30 times a second.
    ///
    /// Never extrapolates: past the newest sample it clamps, so the entity briefly
    /// stands still instead of guessing (and overshooting).
    /// </summary>
    public sealed class InterpolationBuffer
    {
        /// <summary>Segments longer than this are treated as teleports (respawn) and
        /// snapped instead of interpolated — nobody glides across the map in 33 ms.</summary>
        private const float TeleportDistance = 3f;

        private const int MaxSamples = 64;

        private readonly List<(uint Tick, float X, float Z, float AimX, float AimZ)> _samples =
            new List<(uint, float, float, float, float)>();

        public int Count => _samples.Count;

        public void Add(uint tick, float x, float z, float aimX, float aimZ)
        {
            // Snapshots can repeat a tick after a reconnect hiccup; keep one per tick.
            if (_samples.Count > 0 && _samples[_samples.Count - 1].Tick >= tick)
                return;
            _samples.Add((tick, x, z, aimX, aimZ));
            if (_samples.Count > MaxSamples)
                _samples.RemoveAt(0);
        }

        /// <summary>
        /// The newest sample, unblended — what you render when interpolation is
        /// switched off in the HUD. Entities then jump from snapshot to snapshot at
        /// 30 Hz, which is the point: it shows what the buffer above is hiding.
        /// </summary>
        public bool TryLatest(out float x, out float z, out float aimX, out float aimZ)
        {
            x = z = aimX = aimZ = 0f;
            if (_samples.Count == 0)
                return false;
            (_, x, z, aimX, aimZ) = _samples[_samples.Count - 1];
            return true;
        }

        public bool TrySample(double renderTick, out float x, out float z, out float aimX, out float aimZ)
        {
            x = z = aimX = aimZ = 0f;
            if (_samples.Count == 0)
                return false;

            if (renderTick <= _samples[0].Tick)
            {
                (_, x, z, aimX, aimZ) = _samples[0];
                return true;
            }
            if (renderTick >= _samples[_samples.Count - 1].Tick)
            {
                (_, x, z, aimX, aimZ) = _samples[_samples.Count - 1];
                return true;
            }

            for (int i = 1; i < _samples.Count; i++)
            {
                if (_samples[i].Tick < renderTick)
                    continue;

                (uint tick0, float x0, float z0, float aimX0, float aimZ0) = _samples[i - 1];
                (uint tick1, float x1, float z1, float aimX1, float aimZ1) = _samples[i];

                float dx = x1 - x0;
                float dz = z1 - z0;
                if (dx * dx + dz * dz > TeleportDistance * TeleportDistance)
                {
                    // Teleport (e.g. respawn): snap to the newer side of the gap.
                    x = x1;
                    z = z1;
                    aimX = aimX1;
                    aimZ = aimZ1;
                    return true;
                }

                float alpha = (float)((renderTick - tick0) / (tick1 - tick0));
                x = x0 + dx * alpha;
                z = z0 + dz * alpha;
                aimX = aimX0 + (aimX1 - aimX0) * alpha;
                aimZ = aimZ0 + (aimZ1 - aimZ0) * alpha;
                return true;
            }

            (_, x, z, aimX, aimZ) = _samples[_samples.Count - 1];
            return true;
        }
    }
}

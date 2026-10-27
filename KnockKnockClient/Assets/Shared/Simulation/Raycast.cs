using System;

namespace KnockKnockArena.Shared.Simulation
{
    /// <summary>
    /// Ray geometry against the arena, shared by the server (hit detection, rocket
    /// flight) and the client (cosmetic tracers). Cosmetics and mechanics disagreeing
    /// about where a wall is would look broken — one implementation prevents that.
    /// </summary>
    public static class Raycast
    {
        /// <summary>Ray vs axis-aligned box on the XZ plane (slab method). Distance is
        /// in ray-direction units (= meters when the direction is normalized); an
        /// origin inside the box hits at distance 0.</summary>
        public static bool RayIntersectsAabb(float originX, float originZ, float directionX, float directionZ,
            float minX, float minZ, float maxX, float maxZ, out float distance)
        {
            distance = 0f;
            float tMin = 0f;
            float tMax = float.PositiveInfinity;

            if (Math.Abs(directionX) < 1e-8f)
            {
                if (originX < minX || originX > maxX)
                    return false;
            }
            else
            {
                float t1 = (minX - originX) / directionX;
                float t2 = (maxX - originX) / directionX;
                if (t1 > t2)
                {
                    float swap = t1;
                    t1 = t2;
                    t2 = swap;
                }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax)
                    return false;
            }

            if (Math.Abs(directionZ) < 1e-8f)
            {
                if (originZ < minZ || originZ > maxZ)
                    return false;
            }
            else
            {
                float t1 = (minZ - originZ) / directionZ;
                float t2 = (maxZ - originZ) / directionZ;
                if (t1 > t2)
                {
                    float swap = t1;
                    t1 = t2;
                    t2 = swap;
                }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax)
                    return false;
            }

            distance = tMin;
            return true;
        }

        /// <summary>
        /// Distance along a normalized ray until it hits arena geometry: the nearest
        /// wall, or the outer bounds if no wall is in the way. Always finite.
        /// </summary>
        public static float ArenaHitDistance(float originX, float originZ, float directionX, float directionZ)
        {
            float best = float.PositiveInfinity;
            if (directionX > 0f) best = (ArenaMap.BoundsMaxX - originX) / directionX;
            else if (directionX < 0f) best = (ArenaMap.BoundsMinX - originX) / directionX;
            float boundsZ = float.PositiveInfinity;
            if (directionZ > 0f) boundsZ = (ArenaMap.BoundsMaxZ - originZ) / directionZ;
            else if (directionZ < 0f) boundsZ = (ArenaMap.BoundsMinZ - originZ) / directionZ;
            if (boundsZ < best)
                best = boundsZ;

            foreach (Aabb wall in ArenaMap.Walls)
            {
                if (RayIntersectsAabb(originX, originZ, directionX, directionZ,
                        wall.MinX, wall.MinZ, wall.MaxX, wall.MaxZ, out float t) && t < best)
                    best = t;
            }
            return best;
        }
    }
}

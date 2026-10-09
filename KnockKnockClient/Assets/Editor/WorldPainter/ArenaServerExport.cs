using System.Collections.Generic;
using System.Linq;
using KnockKnockArena.Gameplay;
using KnockKnockArena.Gameplay.Editor;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Exports the World Painter's PAINTED data (no bake required) to the server's
/// collision file, ArenaMap.Generated.cs:
///
///  - Cells of palette types with serverWall (or mergeCollider — invisible boundary
///    walls must block online too) are flattened to the XZ plane and greedy-merged
///    into axis-aligned boxes — the same idea as the painter's own BakedWalls merge.
///  - The PROP LAYER (Props paint mode) is the intended home for markers: prop
///    palette entries whose prefab carries a PickupMarker / SpawnPointMarker
///    become pickups / spawn points. Props sit on top of blocks, so the ground
///    stays intact underneath. (Block-palette marker prefabs still work too.)
///  - Prop types flagged serverObstacle (crates etc.) become walls too, sized from
///    the PREFAB'S measured footprint rather than its grid cell, so a scaled prop
///    blocks exactly what it looks like it blocks.
///  - Hand-placed markers OUTSIDE the painter hierarchy are included as well
///    (painter children are skipped to avoid double-counting baked instances).
///  - Bounds: an "ArenaBounds" BoxCollider if the scene has one, otherwise the
///    outer edge of everything painted.
///
/// Runs automatically after the painter's Create &amp; Bake, and manually via the
/// menu. Rebuild the server after every export.
/// </summary>
public static class ArenaServerExport
{
    [MenuItem("KnockKnock/Export Arena from World Painter")]
    public static void ExportMenu()
    {
        WorldPainter painter = Object.FindFirstObjectByType<WorldPainter>();
        if (painter == null)
        {
            EditorUtility.DisplayDialog("No World Painter",
                "No WorldPainter found in the open scene.", "OK");
            return;
        }
        Export(painter);
    }

    public static void Export(WorldPainter painter)
    {
        List<string> warnings = new List<string>();
        float gs = painter.gridSize;
        Transform root = painter.transform;

        if (root.rotation != Quaternion.identity || root.lossyScale != Vector3.one)
            warnings.Add("The WorldPainter object is rotated/scaled — exported boxes are " +
                         "world-space AABBs and may not match the visuals. Keep the painter " +
                         "at identity rotation and scale.");

        // ---- classify painted cells ----
        HashSet<Vector2Int> wallCells = new HashSet<Vector2Int>();
        List<Aabb> propWalls = new List<Aabb>();

        // Baked obstacle props, if the prop layer has been baked. Their live bounds
        // are the truth; the painted-data path below is only a fallback.
        List<Bounds> bakedObstacles = GatherBakedObstacles(painter);
        bool bakedObstaclesFound = bakedObstacles != null && bakedObstacles.Count > 0;
        List<(PickupType Type, float X, float Z)> pickups = new List<(PickupType, float, float)>();
        List<(float X, float Z)> spawns = new List<(float, float)>();
        List<float> spawnFloorYs = new List<float>();
        bool anyCell = false;
        int minCX = 0, maxCX = 0, minCZ = 0, maxCZ = 0;

        foreach ((Vector3Int pos, int typeIndex) in painter.EnumerateBlocks())
        {
            if (!anyCell) { minCX = maxCX = pos.x; minCZ = maxCZ = pos.z; anyCell = true; }
            else
            {
                if (pos.x < minCX) minCX = pos.x;
                if (pos.x > maxCX) maxCX = pos.x;
                if (pos.z < minCZ) minCZ = pos.z;
                if (pos.z > maxCZ) maxCZ = pos.z;
            }

            WorldPainter.BlockType type =
                (painter.palette != null && typeIndex >= 0 && typeIndex < painter.palette.Length)
                    ? painter.palette[typeIndex] : null;
            if (type == null)
                continue;

            Vector3 world = root.TransformPoint(new Vector3(pos.x * gs, pos.y * gs, pos.z * gs));

            PickupMarker pickupMarker = type.prefab != null
                ? type.prefab.GetComponentInChildren<PickupMarker>(true) : null;
            if (pickupMarker != null)
            {
                pickups.Add((pickupMarker.Type, ArenaExporter.R(world.x), ArenaExporter.R(world.z)));
                continue;
            }
            if (type.prefab != null && type.prefab.GetComponentInChildren<SpawnPointMarker>(true) != null)
            {
                spawns.Add((ArenaExporter.R(world.x), ArenaExporter.R(world.z)));
                continue;
            }

            if (type.serverWall || type.mergeCollider)
                wallCells.Add(new Vector2Int(pos.x, pos.z));
        }

        // ---- prop layer: markers painted ON TOP of blocks (spawns, pickups) ----
        foreach ((Vector3Int pos, int typeIndex) in painter.AllProps)
        {
            if (!anyCell) { minCX = maxCX = pos.x; minCZ = maxCZ = pos.z; anyCell = true; }
            else
            {
                if (pos.x < minCX) minCX = pos.x;
                if (pos.x > maxCX) maxCX = pos.x;
                if (pos.z < minCZ) minCZ = pos.z;
                if (pos.z > maxCZ) maxCZ = pos.z;
            }

            WorldPainter.PropType propType =
                (painter.propPalette != null && typeIndex >= 0 && typeIndex < painter.propPalette.Length)
                    ? painter.propPalette[typeIndex] : null;
            if (propType == null || propType.prefab == null)
                continue;

            Vector3 world = root.TransformPoint(new Vector3(pos.x * gs, pos.y * gs, pos.z * gs));

            PickupMarker pickup = propType.prefab.GetComponentInChildren<PickupMarker>(true);
            if (pickup != null)
            {
                pickups.Add((pickup.Type, ArenaExporter.R(world.x), ArenaExporter.R(world.z)));
            }
            else if (propType.prefab.GetComponentInChildren<SpawnPointMarker>(true) != null)
            {
                spawns.Add((ArenaExporter.R(world.x), ArenaExporter.R(world.z)));
                // Props stand on the FLOOR of their cell — that height IS the
                // playable floor, used client-side to render entities on the ground.
                spawnFloorYs.Add(root.TransformPoint(
                    new Vector3(pos.x * gs, (pos.y - 0.5f) * gs + propType.yOffset, pos.z * gs)).y);
            }
            else if (propType.serverObstacle && !bakedObstaclesFound)
            {
                // FALLBACK ONLY — used when the prop layer has not been baked yet.
                // The wall is derived from the prefab's own footprint (never from the
                // grid cell: a scaled prop sticks out past its cell and a player would
                // walk into the overhang). When baked props exist, their real bounds
                // are used instead — see below.
                // Markers take precedence — a spawn/pickup marker is never solid.
                Vector3 propLocal = new Vector3(pos.x * gs, (pos.y - 0.5f) * gs + propType.yOffset, pos.z * gs);
                Vector3 propWorld = root.TransformPoint(propLocal);
                (Vector3 center, Vector3 size) = MeasureFootprint(propType.prefab, gs);
                propWalls.Add(BoxAround(propWorld + center, size));
            }
            // props without markers or the obstacle flag are purely decorative
        }

        // ---- baked obstacle props: measure what is ACTUALLY standing in the scene ----
        // This is the normal path (Create & Bake runs the export right after baking).
        // Reading the live instances means the exported collision follows whatever you
        // did to them — prefab rescaled, a single crate scaled or rotated by hand, an
        // off-centre pivot — without touching the exporter. What you see is what the
        // server blocks.
        if (bakedObstacles != null)
            foreach (Bounds b in bakedObstacles)
                propWalls.Add(BoxAround(b.center, b.size));

        if (!anyCell)
        {
            EditorUtility.DisplayDialog("Nothing painted",
                "The World Painter has no painted blocks or props to export.", "OK");
            return;
        }

        // ---- greedy 2D merge of wall cells into boxes (XZ footprint) ----
        // Painted wall blocks merge (they tile the grid); obstacle props keep their
        // own measured box each, since they are individual objects.
        List<Aabb> walls = MergeWallCells(wallCells, gs, root);
        walls.AddRange(propWalls);

        // ---- bounds: explicit ArenaBounds wins; otherwise the painted extent ----
        float minX, minZ, maxX, maxZ;
        if (!ArenaExporter.TryGetArenaBoundsOverride(out minX, out minZ, out maxX, out maxZ))
        {
            Vector3 lo = root.TransformPoint(new Vector3((minCX - 0.5f) * gs, 0f, (minCZ - 0.5f) * gs));
            Vector3 hi = root.TransformPoint(new Vector3((maxCX + 0.5f) * gs, 0f, (maxCZ + 0.5f) * gs));
            minX = ArenaExporter.R(Mathf.Min(lo.x, hi.x));
            maxX = ArenaExporter.R(Mathf.Max(lo.x, hi.x));
            minZ = ArenaExporter.R(Mathf.Min(lo.z, hi.z));
            maxZ = ArenaExporter.R(Mathf.Max(lo.z, hi.z));
        }

        // ---- hand-placed markers outside the painter (skip painter children so
        //      baked instances of painted markers are not counted twice) ----
        System.Func<Transform, bool> outsidePainter = t => !t.IsChildOf(root);
        spawns.AddRange(ArenaExporter.GatherSceneSpawns(outsidePainter));
        pickups.AddRange(ArenaExporter.GatherScenePickups(outsidePainter));

        // Deterministic pickup order = stable pickup ids across re-exports.
        pickups = pickups.OrderBy(p => p.X).ThenBy(p => p.Z).ThenBy(p => (int)p.Type).ToList();

        // Hand-placed spawn markers outside the painter contribute to the floor
        // height too (they stand directly on the floor).
        foreach (SpawnPointMarker marker in Object.FindObjectsByType<SpawnPointMarker>(FindObjectsSortMode.None))
        {
            if (!marker.transform.IsChildOf(root))
                spawnFloorYs.Add(marker.transform.position.y);
        }

        float groundY = 0f;
        if (spawnFloorYs.Count > 0)
            groundY = ArenaExporter.R(spawnFloorYs.Average());
        else
            warnings.Add("No spawn markers to derive the playable floor height from — GroundY " +
                         "defaults to 0 and entities may render inside/under the map.");

        ArenaExporter.ExportData(minX, minZ, maxX, maxZ, walls, spawns, pickups, warnings, groundY);
    }

    /// <summary>World-space XZ box around a centre point with the given size.</summary>
    private static Aabb BoxAround(Vector3 center, Vector3 size)
    {
        float halfX = size.x * 0.5f;
        float halfZ = size.z * 0.5f;
        return new Aabb(
            ArenaExporter.R(center.x - halfX), ArenaExporter.R(center.z - halfZ),
            ArenaExporter.R(center.x + halfX), ArenaExporter.R(center.z + halfZ));
    }

    /// <summary>
    /// World bounds of every baked prop whose palette type is flagged serverObstacle —
    /// one box per prop, covering all its child renderers. Returns null when the prop
    /// layer has not been baked, so the caller falls back to the painted data.
    /// </summary>
    private static List<Bounds> GatherBakedObstacles(WorldPainter painter)
    {
        Transform container = painter.transform.Find("BakedProps");
        if (container == null || painter.propPalette == null)
            return null;

        HashSet<GameObject> obstaclePrefabs = new HashSet<GameObject>();
        foreach (WorldPainter.PropType type in painter.propPalette)
            if (type != null && type.serverObstacle && type.prefab != null)
                obstaclePrefabs.Add(type.prefab);
        if (obstaclePrefabs.Count == 0)
            return null;

        List<Bounds> result = new List<Bounds>();
        foreach (Transform child in container)
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);
            if (source == null || !obstaclePrefabs.Contains(source))
                continue;

            // A collider states explicitly what is meant to be SOLID, so it wins over
            // the visual mesh — decorative overhangs (ledges, signs) should not block.
            // Without one, the renderer bounds are the best available description.
            Collider[] colliders = child.GetComponentsInChildren<Collider>();
            if (colliders.Length > 0)
            {
                Bounds fromColliders = colliders[0].bounds;
                for (int i = 1; i < colliders.Length; i++)
                    fromColliders.Encapsulate(colliders[i].bounds);
                result.Add(fromColliders);
                continue;
            }

            Renderer[] renderers = child.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                continue;

            Bounds combined = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                combined.Encapsulate(renderers[i].bounds);
            result.Add(combined);
        }
        return result;
    }

    /// <summary>
    /// Renderer footprint of a prop prefab, in world units at the prefab's own scale
    /// — the size it will actually occupy once baked. Returned as the bounds centre
    /// offset from the prefab's origin plus the size, so a mesh whose pivot is not
    /// centred still gets a correctly placed box. Measured by instantiating the
    /// prefab exactly as the bake does, which is the only way to get this right for
    /// nested children with their own scales.
    /// </summary>
    private static (Vector3 Center, Vector3 Size) MeasureFootprint(GameObject prefab, float gridSize)
    {
        if (_footprints.TryGetValue(prefab, out (Vector3, Vector3) cached))
            return cached;

        // Only the position is zeroed — the prefab's own ROTATION and SCALE are kept,
        // because the bake keeps them too. Forcing identity rotation here would spin
        // an off-centre pivot onto the wrong axis and offset the exported box.
        GameObject temp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        temp.transform.position = Vector3.zero;

        Bounds bounds = default;
        bool any = false;
        foreach (Renderer renderer in temp.GetComponentsInChildren<Renderer>())
        {
            if (!any) { bounds = renderer.bounds; any = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        Object.DestroyImmediate(temp);

        // A prop with no renderer (pure logic object) falls back to one grid cell.
        (Vector3, Vector3) result = any
            ? (bounds.center, bounds.size)
            : (Vector3.zero, new Vector3(gridSize, gridSize, gridSize));
        _footprints[prefab] = result;
        return result;
    }

    private static readonly Dictionary<GameObject, (Vector3, Vector3)> _footprints =
        new Dictionary<GameObject, (Vector3, Vector3)>();

    /// <summary>Greedy 2D rectangle cover of the wall cells (expand along X, then Z),
    /// converted to world-space AABBs. Cell (x,z) spans [(x-0.5)gs, (x+0.5)gs] locally.</summary>
    private static List<Aabb> MergeWallCells(HashSet<Vector2Int> cells, float gs, Transform root)
    {
        List<Aabb> walls = new List<Aabb>();
        List<Vector2Int> sorted = cells.OrderBy(c => c.y).ThenBy(c => c.x).ToList();
        HashSet<Vector2Int> remaining = new HashSet<Vector2Int>(cells);

        foreach (Vector2Int start in sorted)
        {
            if (!remaining.Contains(start))
                continue;

            int xMax = start.x;
            while (remaining.Contains(new Vector2Int(xMax + 1, start.y)))
                xMax++;

            int zMax = start.y;
            while (true)
            {
                bool rowOk = true;
                for (int x = start.x; x <= xMax && rowOk; x++)
                    if (!remaining.Contains(new Vector2Int(x, zMax + 1)))
                        rowOk = false;
                if (!rowOk)
                    break;
                zMax++;
            }

            for (int x = start.x; x <= xMax; x++)
            for (int z = start.y; z <= zMax; z++)
                remaining.Remove(new Vector2Int(x, z));

            Vector3 lo = root.TransformPoint(new Vector3((start.x - 0.5f) * gs, 0f, (start.y - 0.5f) * gs));
            Vector3 hi = root.TransformPoint(new Vector3((xMax + 0.5f) * gs, 0f, (zMax + 0.5f) * gs));
            walls.Add(new Aabb(
                ArenaExporter.R(Mathf.Min(lo.x, hi.x)), ArenaExporter.R(Mathf.Min(lo.z, hi.z)),
                ArenaExporter.R(Mathf.Max(lo.x, hi.x)), ArenaExporter.R(Mathf.Max(lo.z, hi.z))));
        }

        return walls;
    }
}

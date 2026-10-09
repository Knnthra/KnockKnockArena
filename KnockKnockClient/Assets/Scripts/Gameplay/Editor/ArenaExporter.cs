using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KnockKnockArena.Gameplay;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;
using UnityEditor;
using UnityEngine;

namespace KnockKnockArena.Gameplay.Editor
{
    /// <summary>
    /// Turns authored map data into arena.json — the collision data the server
    /// simulates against and the client predicts against. One bake produces both the
    /// visual map and this file, and stamps the file's hash into the scene, so the
    /// picture and the collision data can be proved to be the same map. Visual meshes
    /// are irrelevant here — only collision boxes and markers are exported.
    ///
    /// Two authoring paths feed the same <see cref="ExportData"/> core:
    ///  1. The World Painter (menu "KnockKnock/Export Arena from World Painter",
    ///     also run automatically by the painter's Create &amp; Bake button) —
    ///     the primary path: painted serverWall cells become merged boxes, painted
    ///     marker prefabs become pickups/spawns.
    ///  2. Hand-built scenes (menu "KnockKnock/Export Arena from Scene"): axis-
    ///     aligned BoxColliders under "ArenaCollision", an "ArenaBounds" box,
    ///     SpawnPointMarker components (or children of "SpawnPoints") and
    ///     PickupMarker components.
    ///
    /// After ANY export: restart the server so it re-reads the new collision data.
    /// (No rebuild — the map is data now, not compiled code.)
    /// </summary>
    public static class ArenaExporter
    {
        /// <summary>Inside the Unity project, so the scene can reference it as a
        /// TextAsset and ship with the client.</summary>
        private const string OutputPath = "Assets/Arena/arena.json";

        /// <summary>The server's copy, relative to the repository root. The exporter
        /// writes both from the same bake so the two can never be different maps.</summary>
        private const string ServerRelativePath = "data/arena.json";

        /// <summary>Scene object that carries the stamp tying the visual map to the
        /// collision file. Created on first export.</summary>
        private const string StampObjectName = "ArenaMap";

        // ------------------------------------------------------------------ scene-convention path

        [MenuItem("KnockKnock/Export Arena from Scene")]
        public static void Export()
        {
            List<string> warnings = new List<string>();

            // ---- walls ----
            GameObject collisionRoot = GameObject.Find("ArenaCollision");
            List<Aabb> walls = new List<Aabb>();
            if (collisionRoot == null)
            {
                warnings.Add("No GameObject named 'ArenaCollision' found — exported ZERO walls. " +
                             "Parent your wall BoxColliders under an object named exactly 'ArenaCollision'.");
            }
            else
            {
                foreach (BoxCollider box in collisionRoot.GetComponentsInChildren<BoxCollider>(false))
                {
                    if (!IsAxisAligned(box.transform))
                        warnings.Add($"'{box.name}' is rotated off-axis — the server can only do " +
                                     "axis-aligned boxes, so the exported box is the enclosing AABB " +
                                     "(larger than the mesh). Rotate it to a multiple of 90 degrees, " +
                                     "or split the shape into axis-aligned pieces.");
                    Bounds b = box.bounds; // world-space AABB; exact for axis-aligned colliders
                    walls.Add(new Aabb(R(b.min.x), R(b.min.z), R(b.max.x), R(b.max.z)));
                }
            }

            // ---- bounds ----
            float minX, minZ, maxX, maxZ;
            if (TryGetArenaBoundsOverride(out minX, out minZ, out maxX, out maxZ))
            {
                // explicit ArenaBounds box wins
            }
            else if (walls.Count > 0)
            {
                minX = walls.Min(w => w.MinX) - 2f; maxX = walls.Max(w => w.MaxX) + 2f;
                minZ = walls.Min(w => w.MinZ) - 2f; maxZ = walls.Max(w => w.MaxZ) + 2f;
                warnings.Add("No 'ArenaBounds' BoxCollider found — bounds computed from the walls " +
                             "plus a 2 m margin. Add an 'ArenaBounds' object for an exact play area.");
            }
            else
            {
                Debug.LogError("Arena export aborted: no bounds and no walls to derive them from.");
                return;
            }

            List<(float X, float Z)> spawns = GatherSceneSpawns(null);
            List<(PickupType Type, float X, float Z)> pickups = GatherScenePickups(null);

            // Playable floor height = where the spawn markers stand (client render only).
            SpawnPointMarker[] spawnMarkers = Object.FindObjectsByType<SpawnPointMarker>(FindObjectsSortMode.None);
            float groundY = spawnMarkers.Length > 0
                ? R(spawnMarkers.Average(m => m.transform.position.y))
                : 0f;

            ExportData(minX, minZ, maxX, maxZ, walls, spawns, pickups, warnings, groundY);
        }

        // ------------------------------------------------------------------ shared gatherers

        /// <summary>Reads the world XZ of an "ArenaBounds" BoxCollider, if the scene has one.</summary>
        public static bool TryGetArenaBoundsOverride(out float minX, out float minZ, out float maxX, out float maxZ)
        {
            minX = minZ = maxX = maxZ = 0f;
            GameObject boundsObj = GameObject.Find("ArenaBounds");
            BoxCollider boundsBox = boundsObj != null ? boundsObj.GetComponent<BoxCollider>() : null;
            if (boundsBox == null)
                return false;
            Bounds b = boundsBox.bounds;
            minX = R(b.min.x); minZ = R(b.min.z); maxX = R(b.max.x); maxZ = R(b.max.z);
            return true;
        }

        /// <summary>All spawn positions in the scene: SpawnPointMarker components plus
        /// (legacy convention) direct children of a GameObject named "SpawnPoints".
        /// The filter can exclude markers (e.g. those under the World Painter).</summary>
        public static List<(float X, float Z)> GatherSceneSpawns(System.Func<Transform, bool> include)
        {
            List<(float X, float Z)> spawns = new List<(float, float)>();
            foreach (SpawnPointMarker marker in Object.FindObjectsByType<SpawnPointMarker>(FindObjectsSortMode.None)
                         .OrderBy(m => R(m.transform.position.x)).ThenBy(m => R(m.transform.position.z)))
            {
                if (include == null || include(marker.transform))
                    spawns.Add((R(marker.transform.position.x), R(marker.transform.position.z)));
            }

            GameObject spawnRoot = GameObject.Find("SpawnPoints");
            if (spawnRoot != null)
            {
                foreach (Transform child in spawnRoot.transform)
                {
                    if (child.GetComponent<SpawnPointMarker>() != null)
                        continue; // already collected above
                    if (include == null || include(child))
                        spawns.Add((R(child.position.x), R(child.position.z)));
                }
            }
            return spawns;
        }

        /// <summary>All pickups in the scene, deterministically ordered so pickup ids
        /// stay stable across re-exports.</summary>
        public static List<(PickupType Type, float X, float Z)> GatherScenePickups(System.Func<Transform, bool> include)
        {
            return Object.FindObjectsByType<PickupMarker>(FindObjectsSortMode.None)
                .Where(m => include == null || include(m.transform))
                .OrderBy(m => R(m.transform.position.x))
                .ThenBy(m => R(m.transform.position.z))
                .Select(m => (m.Type, R(m.transform.position.x), R(m.transform.position.z)))
                .ToList();
        }

        // ------------------------------------------------------------------ the export core

        /// <summary>
        /// Writes arena.json from collected data, stamps its hash into the scene, and
        /// logs the wiki table. Both authoring paths (World Painter and scene
        /// conventions) end here.
        /// </summary>
        public static void ExportData(float minX, float minZ, float maxX, float maxZ,
            List<Aabb> walls, List<(float X, float Z)> spawns,
            List<(PickupType Type, float X, float Z)> pickups, List<string> warnings,
            float groundY = 0f)
        {
            if (spawns.Count == 0)
            {
                spawns = new List<(float, float)> { (R((minX + maxX) * 0.5f), R((minZ + maxZ) * 0.5f)) };
                warnings.Add("No spawn points found — added one at the arena center. Paint/place " +
                             "SpawnPointMarker objects for real spawns.");
            }
            if (walls.Count == 0)
                warnings.Add("Exported ZERO walls — the arena is an empty box server-side.");

            string mapName = System.IO.Path.GetFileNameWithoutExtension(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().name);
            if (string.IsNullOrEmpty(mapName))
                mapName = "unnamed";

            // Write the file, then parse it back with the SHARED loader to get the hash
            // — the same code the server and the client will run on it. Taking the hash
            // from the file rather than from the data in memory is what makes the stamp
            // mean something: it is a statement about the bytes both sides will read.
            string json = BuildJson(mapName, minX, minZ, maxX, maxZ, walls, spawns, pickups, groundY, null);

            ArenaMapData parsed;
            try
            {
                parsed = ArenaMapData.Parse(json);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Arena export aborted: the exported map does not load — {ex.Message}");
                return;
            }

            json = BuildJson(mapName, minX, minZ, maxX, maxZ, walls, spawns, pickups, groundY, parsed.Hash);

            string projectPath = System.IO.Path.Combine(Application.dataPath, "Arena/arena.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(projectPath));
            System.IO.File.WriteAllText(projectPath, json);

            // The player build's copy: SimulationDigestRunner (the -dump-bits
            // determinism check, M06) reads the map from StreamingAssets in a built
            // player. Written here too, or it silently keeps the previous map.
            string streamingPath = System.IO.Path.Combine(Application.streamingAssetsPath, "arena.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(streamingPath));
            System.IO.File.WriteAllText(streamingPath, json);

            // The server's copy. dataPath is <repo>/KnockKnockClient/Assets.
            string serverPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "../..", ServerRelativePath));
            bool wroteServerCopy = false;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(serverPath));
                System.IO.File.WriteAllText(serverPath, json);
                wroteServerCopy = true;
            }
            catch (System.Exception ex)
            {
                warnings.Add($"could not write the server's copy at {serverPath} ({ex.Message}) — " +
                             $"copy {OutputPath} there by hand, or the server will run the old map.");
            }

            AssetDatabase.Refresh();
            StampScene(parsed.Hash, warnings);

            Debug.Log($"Arena \"{mapName}\" exported to {OutputPath}, StreamingAssets/arena.json" +
                      (wroteServerCopy ? $" and {ServerRelativePath}" : "") + ": " +
                      $"{walls.Count} walls, {spawns.Count} spawn points, {pickups.Count} pickups, " +
                      $"hash {ArenaMapData.FormatHash(parsed.Hash)}. " +
                      $"bounds x[{minX}..{maxX}] z[{minZ}..{maxZ}].\n" +
                      "RESTART THE SERVER so it re-reads the map (no rebuild needed — it is data now).\n\n" +
                      BuildWikiTable(minX, minZ, maxX, maxZ, walls));

            foreach (string w in warnings)
                Debug.LogWarning("Arena export: " + w);
        }

        /// <summary>
        /// Points the scene's ArenaMapStamp at the file this bake just wrote. That
        /// component is how the running client knows which arena it is showing, and
        /// therefore how it can tell the server it is showing a different one.
        /// </summary>
        private static void StampScene(uint hash, List<string> warnings)
        {
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(OutputPath);
            if (asset == null)
            {
                warnings.Add($"{OutputPath} did not import as a TextAsset, so the scene could not be " +
                             "stamped. Re-run the export.");
                return;
            }

            ArenaMapStamp stamp = Object.FindFirstObjectByType<ArenaMapStamp>();
            if (stamp == null)
            {
                GameObject host = GameObject.Find(StampObjectName) ?? new GameObject(StampObjectName);
                stamp = host.GetComponent<ArenaMapStamp>() ?? host.AddComponent<ArenaMapStamp>();
                Undo.RegisterCreatedObjectUndo(host, "Stamp arena map");
            }

            Undo.RecordObject(stamp, "Stamp arena map");
            stamp.SetBakeOutput(asset, hash);
            EditorUtility.SetDirty(stamp);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(stamp.gameObject.scene);
        }

        private static bool IsAxisAligned(Transform t)
        {
            Vector3 e = t.eulerAngles;
            return NearMultipleOf90(e.x) && NearMultipleOf90(e.y) && NearMultipleOf90(e.z);
        }

        private static bool NearMultipleOf90(float angle)
        {
            float m = Mathf.Repeat(angle, 90f);
            return m < 0.5f || m > 89.5f;
        }

        /// <summary>Round to 3 decimals — server and client compile the same literals,
        /// so any precision is "bit-perfect", but clean numbers keep the wiki readable.</summary>
        public static float R(float v) => (float)System.Math.Round(v, 3);

        /// <summary>A number the shared loader will read back as the same float.
        /// R() has already rounded to 3 decimals, so this is lossless for what it is
        /// given — and short enough to stay readable in a hand-edited map.</summary>
        private static string L(float v) => v.ToString("0.0##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Writes arena.json. Pass <paramref name="hash"/> as null on the first pass:
        /// the exporter builds the file once without it, loads it to find out what it
        /// hashes to, and writes it again with the answer baked in.
        /// </summary>
        private static string BuildJson(string mapName, float minX, float minZ, float maxX, float maxZ,
            List<Aabb> walls, List<(float X, float Z)> spawns,
            List<(PickupType Type, float X, float Z)> pickups, float groundY, uint? hash)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": {Quote(mapName)},");
            sb.AppendLine();
            sb.AppendLine("  \"_comment\": [");
            sb.AppendLine("    \"Written by the Arena exporter from the baked map. Edit the map and re-export\",");
            sb.AppendLine("    \"rather than editing this file: the hash is checked on load, and an edited\",");
            sb.AppendLine("    \"file no longer matches the map you can see.\",");
            sb.AppendLine("    \"Walls are axis-aligned XZ boxes in FIXED iteration order - collision push-out\",");
            sb.AppendLine("    \"visits them in this order, so the order is part of the rule.\",");
            sb.AppendLine("    \"groundY is client rendering only; the server simulates pure XZ.\"");
            sb.AppendLine("  ],");
            if (hash.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine($"  \"hash\": \"{ArenaMapData.FormatHash(hash.Value)}\",");
            }
            sb.AppendLine();
            sb.AppendLine($"  \"bounds\": {{ \"minX\": {L(minX)}, \"maxX\": {L(maxX)}, " +
                          $"\"minZ\": {L(minZ)}, \"maxZ\": {L(maxZ)} }},");
            sb.AppendLine();
            sb.AppendLine($"  \"groundY\": {L(groundY)},");
            sb.AppendLine();

            sb.AppendLine("  \"walls\": [");
            for (int i = 0; i < walls.Count; i++)
            {
                Aabb w = walls[i];
                sb.AppendLine($"    {{ \"minX\": {L(w.MinX)}, \"minZ\": {L(w.MinZ)}, " +
                              $"\"maxX\": {L(w.MaxX)}, \"maxZ\": {L(w.MaxZ)} }}" +
                              (i < walls.Count - 1 ? "," : ""));
            }
            sb.AppendLine("  ],");
            sb.AppendLine();

            sb.AppendLine("  \"spawnPoints\": [");
            for (int i = 0; i < spawns.Count; i++)
            {
                (float x, float z) = spawns[i];
                sb.AppendLine($"    {{ \"x\": {L(x)}, \"z\": {L(z)} }}" + (i < spawns.Count - 1 ? "," : ""));
            }
            sb.AppendLine("  ],");
            sb.AppendLine();

            sb.AppendLine("  \"pickups\": [");
            for (int i = 0; i < pickups.Count; i++)
            {
                (PickupType type, float x, float z) = pickups[i];
                sb.AppendLine($"    {{ \"type\": \"{type}\", \"x\": {L(x)}, \"z\": {L(z)} }}" +
                              (i < pickups.Count - 1 ? "," : ""));
            }
            sb.AppendLine("  ]");

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Quote(string value)
            => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string BuildWikiTable(float minX, float minZ, float maxX, float maxZ, List<Aabb> walls)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Arena bounds: x fra {minX} til {maxX}, z fra {minZ} til {maxZ}.");
            sb.AppendLine();
            sb.AppendLine("| # | minX | minZ | maxX | maxZ |");
            sb.AppendLine("|---|---|---|---|---|");
            for (int i = 0; i < walls.Count; i++)
                sb.AppendLine($"| {i} | {walls[i].MinX} | {walls[i].MinZ} | {walls[i].MaxX} | {walls[i].MaxZ} |");
            return sb.ToString();
        }
    }
}

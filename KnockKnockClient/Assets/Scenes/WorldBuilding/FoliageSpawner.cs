using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class FoliageType
{
    public string name = "Foliage";
    [Tooltip("Color used to represent this type on the World Painter grid.")]
    public Color tileColor = new Color(0.15f, 0.75f, 0.25f);
    public Mesh mesh;
    [Tooltip("One material per submesh. All materials must have GPU Instancing enabled.")]
    public Material[] materials;

    [Header("Spawn Settings")]
    [Tooltip("Instances per square unit (only used when Spawn In Patches is off)")]
    public float density = 0.1f;

    [Header("Color Filter")]
    [Tooltip("Only spawn where the surface color matches the target color")]
    public bool useColorFilter = false;
    public Color targetColor = Color.green;
    [Tooltip("Log the first 10 sampled surface colors to the console — use this to find the right target color")]
    public bool debugColors = false;
    [Range(0f, 1f)]
    [Tooltip("How close the color must be (0 = exact match, 1 = anything)")]
    public float colorTolerance = 0.3f;
    [Tooltip("Check the tile's material color instead of vertex colors (works on most tile setups)")]
    public bool useMaterialColor = true;

    [Header("Scale")]
    [Tooltip("Lock width to match height for uniform scaling")]
    public bool uniformScale = false;
    public Vector2 heightRange = new Vector2(0.8f, 1.2f);
    public Vector2 widthRange = new Vector2(0.8f, 1.2f);

    [Header("Spacing")]
    [Tooltip("Minimum world-space distance between any two instances of this type. " +
             "Set to 0 to disable the check. Prevents grass blades spawning on top of each other.")]
    public float minimumDistance = 0.5f;

    [Header("Patch Spawning")]
    [Tooltip("Spawn in clusters instead of uniformly spread")]
    public bool spawnInPatches = false;
    [Tooltip("Number of clusters to place across the area")]
    public int patchCount = 10;
    [Tooltip("Min and max instances per cluster (randomized each patch)")]
    public Vector2Int instancesPerPatch = new Vector2Int(10, 20);
    [Tooltip("Radius of each cluster in world units")]
    public float patchRadius = 5f;
}

/// <summary>
/// Places GPU-instanced foliage (grass, flowers, rocks, etc.) by raycasting down
/// onto surfaces. Supports multiple foliage types with per-type color filters and
/// patch spawning. Press Generate in the inspector — instances are baked and
/// rendered in edit mode without entering play mode.
/// Placement area is auto-detected from all colliders on the specified surface layers.
/// </summary>
[ExecuteAlways]
public class FoliageSpawner : MonoBehaviour
{
    [Header("Global Settings")]
    public int seed = 12345;
    [Tooltip("Layers to raycast against when finding the ground surface")]
    public LayerMask surfaceLayers = ~0;
    [Tooltip("Y offset applied after surface hit (negative = push into ground)")]
    public float yOffset = 0f;
    [Tooltip("How far away foliage is visible")]
    public float renderDistance = 100f;

    [Header("Foliage Types")]
    public FoliageType[] foliageTypes;

    // ---- Serialized per-type instance data (survives domain reloads / scene saves) ----

    [System.Serializable]
    private class FoliageInstanceData
    {
        public Vector3[] positions = new Vector3[0];
        public float[]   rotations = new float[0];
        public Vector3[] scales    = new Vector3[0];
        public int Count => positions?.Length ?? 0;
    }

    [SerializeField, HideInInspector]
    private FoliageInstanceData[] savedInstances = new FoliageInstanceData[0];

    // Cached bounds for gizmo display
    [SerializeField, HideInInspector] private Vector3 _gizmoCenter;
    [SerializeField, HideInInspector] private Vector3 _gizmoSize;

    // ---- Public accessors for editor ----

    public int TotalInstanceCount
    {
        get
        {
            int total = 0;
            if (savedInstances != null)
                foreach (var d in savedInstances) total += d?.Count ?? 0;
            return total;
        }
    }

    public int GetTypeInstanceCount(int i) =>
        (savedInstances != null && i >= 0 && i < savedInstances.Length) ? savedInstances[i]?.Count ?? 0 : 0;

    // ---- Batches (runtime, rebuilt from serialized data) ----

    private const int MaxBatchSize = 1023;
    // Outer list: per foliage type. Inner list: 1023-element batches.
    private List<List<Matrix4x4[]>> allBatches = new List<List<Matrix4x4[]>>();

    private void OnEnable() => RebuildBatches();

    private void RebuildBatches()
    {
        allBatches.Clear();
        if (savedInstances == null) return;

        for (int t = 0; t < savedInstances.Length; t++)
        {
            var d       = savedInstances[t];
            var batches = new List<Matrix4x4[]>();
            allBatches.Add(batches);

            if (d == null || d.Count == 0) continue;

            var matrices = new List<Matrix4x4>(d.Count);
            for (int i = 0; i < d.Count; i++)
            {
                Quaternion rot = Quaternion.Euler(0f, d.rotations[i], 0f);
                matrices.Add(Matrix4x4.TRS(d.positions[i], rot, d.scales[i]));
            }

            for (int i = 0; i < matrices.Count; i += MaxBatchSize)
            {
                int batchSize = Mathf.Min(MaxBatchSize, matrices.Count - i);
                var batch = new Matrix4x4[batchSize];
                for (int j = 0; j < batchSize; j++) batch[j] = matrices[i + j];
                batches.Add(batch);
            }
        }
    }

    // ---- Generation ----

    public void Generate()
    {
        if (foliageTypes == null || foliageTypes.Length == 0)
        {
            Debug.LogError("[FoliageSpawner] Add at least one Foliage Type before generating.");
            return;
        }

        // Auto-detect XZ bounds from all colliders on the specified surface layers
        Collider[] cols = FindObjectsByType<Collider>(FindObjectsInactive.Exclude);
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        float maxY = float.MinValue;

        foreach (var col in cols)
        {
            if (((1 << col.gameObject.layer) & surfaceLayers) == 0) continue;
            Bounds b = col.bounds;
            minX = Mathf.Min(minX, b.min.x);
            maxX = Mathf.Max(maxX, b.max.x);
            minZ = Mathf.Min(minZ, b.min.z);
            maxZ = Mathf.Max(maxZ, b.max.z);
            maxY = Mathf.Max(maxY, b.max.y);
        }

        if (minX == float.MaxValue)
        {
            Debug.LogError("[FoliageSpawner] No colliders found on the specified surface layers.");
            return;
        }

        _gizmoCenter = new Vector3((minX + maxX) * 0.5f, maxY, (minZ + maxZ) * 0.5f);
        _gizmoSize   = new Vector3(maxX - minX, 0.1f, maxZ - minZ);

        float areaX      = maxX - minX;
        float areaZ      = maxZ - minZ;
        float rayOriginY = maxY + 100f;

        Random.InitState(seed);
        savedInstances = new FoliageInstanceData[foliageTypes.Length];

        for (int t = 0; t < foliageTypes.Length; t++)
        {
            var foliage = foliageTypes[t];
            savedInstances[t] = new FoliageInstanceData();
            _debugColorCount = 0;

            if (foliage.mesh == null || foliage.materials == null || foliage.materials.Length == 0 || foliage.materials[0] == null)
            {
                Debug.LogWarning($"[FoliageSpawner] '{foliage.name}' is missing Mesh or Material — skipped.");
                continue;
            }

            var positions = new List<Vector3>();
            var rotations = new List<float>();
            var scales    = new List<Vector3>();
            int colorRejected = 0;

            int targetCount = Mathf.RoundToInt(areaX * areaZ * foliage.density);

            if (foliage.spawnInPatches)
            {
                for (int p = 0; p < foliage.patchCount; p++)
                {
                    float cx = Random.Range(minX, maxX);
                    float cz = Random.Range(minZ, maxZ);

                    int patchTarget   = Random.Range(foliage.instancesPerPatch.x, foliage.instancesPerPatch.y + 1);
                    int maxAttempts   = patchTarget * (foliage.useColorFilter ? 30 : 10);
                    int patchAttempts = 0;
                    int patchSpawned  = 0;

                    while (patchSpawned < patchTarget && patchAttempts < maxAttempts)
                    {
                        patchAttempts++;
                        float angle = Random.Range(0f, Mathf.PI * 2f);
                        float dist  = Random.Range(0f, foliage.patchRadius);
                        float x = Mathf.Clamp(cx + Mathf.Cos(angle) * dist, minX, maxX);
                        float z = Mathf.Clamp(cz + Mathf.Sin(angle) * dist, minZ, maxZ);

                        Ray ray = new Ray(new Vector3(x, rayOriginY, z), Vector3.down);
                        if (!Physics.Raycast(ray, out RaycastHit hit, rayOriginY + 100f, surfaceLayers)) continue;

                        if (foliage.useColorFilter && !IsTargetColor(hit, foliage))
                        { colorRejected++; continue; }

                        AddInstance(hit.point, foliage, positions, rotations, scales);
                        patchSpawned++;
                    }

                    if (patchSpawned == 0)
                        Debug.LogWarning($"[FoliageSpawner] '{foliage.name}' patch {p}: 0/{patchTarget} spawned — patch center ({cx:F1}, {cz:F1}) may be off-surface or color-filtered out.");
                }
            }
            else
            {
                int attempts    = 0;
                int spawned     = 0;
                int maxAttempts = targetCount * (foliage.useColorFilter ? 30 : 5);

                while (spawned < targetCount && attempts < maxAttempts)
                {
                    attempts++;
                    float x = Random.Range(minX, maxX);
                    float z = Random.Range(minZ, maxZ);

                    Ray ray = new Ray(new Vector3(x, rayOriginY, z), Vector3.down);
                    if (!Physics.Raycast(ray, out RaycastHit hit, rayOriginY + 100f, surfaceLayers)) continue;

                    if (foliage.useColorFilter && !IsTargetColor(hit, foliage))
                    { colorRejected++; continue; }

                    AddInstance(hit.point, foliage, positions, rotations, scales);
                    spawned++;
                }
            }

            savedInstances[t].positions = positions.ToArray();
            savedInstances[t].rotations = rotations.ToArray();
            savedInstances[t].scales    = scales.ToArray();

            string colorNote = foliage.useColorFilter ? $" | {colorRejected} color-rejected" : "";
            Debug.Log($"[FoliageSpawner] '{foliage.name}': {savedInstances[t].Count}/{targetCount} instances{colorNote}.");
        }

        RebuildBatches();
    }

    private void AddInstance(Vector3 hitPoint, FoliageType foliage,
        List<Vector3> positions, List<float> rotations, List<Vector3> scales)
    {
        float scaleY = Random.Range(foliage.heightRange.x, foliage.heightRange.y);
        float scaleX = foliage.uniformScale ? scaleY : Random.Range(foliage.widthRange.x, foliage.widthRange.y);
        positions.Add(hitPoint + Vector3.up * yOffset);
        rotations.Add(Random.Range(0f, 360f));
        scales.Add(new Vector3(scaleX, scaleY, scaleX));
    }

    private int _debugColorCount;

    private bool IsTargetColor(RaycastHit hit, FoliageType foliage)
    {
        Color sampledColor;

        if (foliage.useMaterialColor)
        {
            // Check the renderer's material color — works on any collider type
            Renderer rend = hit.collider.GetComponent<Renderer>();
            if (rend == null || rend.sharedMaterial == null)
            {
                if (foliage.debugColors && _debugColorCount < 10)
                { _debugColorCount++; Debug.Log($"[FoliageSpawner] '{foliage.name}' hit '{hit.collider.name}' — no Renderer/Material found, rejected."); }
                return false;
            }
            var mat = rend.sharedMaterial;
            if (mat.HasProperty("_BaseColor"))
                sampledColor = mat.GetColor("_BaseColor");
            else if (mat.HasProperty("_Color"))
                sampledColor = mat.GetColor("_Color");
            else
            {
                if (foliage.debugColors && _debugColorCount < 10)
                { _debugColorCount++; Debug.Log($"[FoliageSpawner] '{foliage.name}' hit '{hit.collider.name}' — shader '{mat.shader.name}' has no _Color or _BaseColor property. Add a color property to the shader, or switch to layer-based filtering."); }
                return false;
            }
        }
        else
        {
            // Check interpolated vertex color at the hit triangle
            MeshCollider mc = hit.collider as MeshCollider;
            if (mc == null || mc.sharedMesh == null)
            {
                if (foliage.debugColors && _debugColorCount < 10)
                { _debugColorCount++; Debug.Log($"[FoliageSpawner] '{foliage.name}' hit '{hit.collider.name}' — not a MeshCollider, rejected."); }
                return false;
            }

            Color[] colors = mc.sharedMesh.colors;
            if (colors == null || colors.Length == 0)
            {
                if (foliage.debugColors && _debugColorCount < 10)
                { _debugColorCount++; Debug.Log($"[FoliageSpawner] '{foliage.name}' hit '{mc.name}' — no vertex colors, rejected."); }
                return false;
            }

            int[] tris = mc.sharedMesh.triangles;
            int idx = hit.triangleIndex * 3;
            if (idx + 2 >= tris.Length) return false;

            int i0 = tris[idx], i1 = tris[idx + 1], i2 = tris[idx + 2];
            if (i0 >= colors.Length || i1 >= colors.Length || i2 >= colors.Length) return false;

            Vector3 b = hit.barycentricCoordinate;
            sampledColor = colors[i0] * b.x + colors[i1] * b.y + colors[i2] * b.z;
        }

        float dr = sampledColor.r - foliage.targetColor.r;
        float dg = sampledColor.g - foliage.targetColor.g;
        float db = sampledColor.b - foliage.targetColor.b;
        float dist = Mathf.Sqrt(dr * dr + dg * dg + db * db);
        bool pass = dist <= foliage.colorTolerance * 1.732f;

        if (foliage.debugColors && _debugColorCount < 10)
        { _debugColorCount++; Debug.Log($"[FoliageSpawner] '{foliage.name}' hit '{hit.collider.name}' — color {sampledColor}, dist {dist:F3}, {(pass ? "PASS" : "FAIL")}"); }

        return pass;
    }

    public void Clear()
    {
        savedInstances = new FoliageInstanceData[0];
        allBatches.Clear();
        Debug.Log("[FoliageSpawner] Cleared all foliage instances.");
    }

    // ---- Rendering ----

    private void LateUpdate()
    {
        if (allBatches == null || foliageTypes == null || allBatches.Count == 0) return;

        Camera cam = Camera.main;
#if UNITY_EDITOR
        if (cam == null)
            cam = UnityEditor.SceneView.lastActiveSceneView?.camera;
#endif
        float renderDistSqr = renderDistance * renderDistance;

        for (int t = 0; t < foliageTypes.Length && t < allBatches.Count; t++)
        {
            var foliage = foliageTypes[t];
            if (foliage.mesh == null || foliage.materials == null || foliage.materials.Length == 0) continue;
            int subCount = Mathf.Min(foliage.mesh.subMeshCount, foliage.materials.Length);

            foreach (var batch in allBatches[t])
            {
                if (cam != null)
                {
                    Vector3 batchPos = batch[0].GetColumn(3);
                    if ((batchPos - cam.transform.position).sqrMagnitude > renderDistSqr * 4f)
                        continue;
                }
                for (int sub = 0; sub < subCount; sub++)
                {
                    if (foliage.materials[sub] == null) continue;
                    Graphics.DrawMeshInstanced(foliage.mesh, sub, foliage.materials[sub], batch);
                }
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (_gizmoSize == Vector3.zero) return;
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.25f);
        Gizmos.DrawCube(_gizmoCenter, _gizmoSize);
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 1f);
        Gizmos.DrawWireCube(_gizmoCenter, _gizmoSize);
    }
}

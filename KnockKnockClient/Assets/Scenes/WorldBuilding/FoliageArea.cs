using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Place this component in your scene, resize the green box in the Scene view,
/// assign foliage types, and press Generate. Instances are baked and visible in
/// edit mode without entering play mode. Multiple areas can coexist.
/// </summary>
[ExecuteAlways]
public class FoliageArea : MonoBehaviour
{
    [Header("Area")]
    [Tooltip("Size of the spawn volume in world units (resize the box in Scene view)")]
    public Vector3 size = new Vector3(10f, 5f, 10f);
    public int seed = 12345;
    [Tooltip("Layers to raycast against when finding the ground")]
    public LayerMask surfaceLayers = ~0;
    [Tooltip("Y offset applied after surface hit (negative = push into ground)")]
    public float yOffset = 0f;
    [Tooltip("How far away foliage is visible")]
    public float renderDistance = 100f;

    [Header("Foliage Types")]
    public FoliageType[] foliageTypes;

    // ---- Serialized instance data ----

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

    public int TotalInstanceCount
    {
        get { int t = 0; if (savedInstances != null) foreach (var d in savedInstances) t += d?.Count ?? 0; return t; }
    }

    public int GetTypeInstanceCount(int i) =>
        (savedInstances != null && i >= 0 && i < savedInstances.Length) ? savedInstances[i]?.Count ?? 0 : 0;

    // ---- Batches ----

    private const int MaxBatchSize = 1023;
    private List<List<Matrix4x4[]>> allBatches = new List<List<Matrix4x4[]>>();

    /// <summary>Set true by WorldPainter so it can centralize rendering.
    /// FoliageArea's own LateUpdate is skipped; WorldPainter calls DrawBatches() instead.</summary>
    [System.NonSerialized] public bool managedExternally;

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
            Debug.LogError("[FoliageArea] Add at least one Foliage Type before generating.");
            return;
        }

        Vector3 center = transform.position;
        float minX = center.x - size.x * 0.5f, maxX = center.x + size.x * 0.5f;
        float minZ = center.z - size.z * 0.5f, maxZ = center.z + size.z * 0.5f;
        float rayOriginY = center.y + size.y * 0.5f + 1f;
        float rayLength  = size.y + 2f;

        Random.InitState(seed);
        savedInstances = new FoliageInstanceData[foliageTypes.Length];

        for (int t = 0; t < foliageTypes.Length; t++)
        {
            var foliage = foliageTypes[t];
            savedInstances[t] = new FoliageInstanceData();

            if (foliage.mesh == null || foliage.materials == null || foliage.materials.Length == 0 || foliage.materials[0] == null)
            {
                Debug.LogWarning($"[FoliageArea] '{foliage.name}' has no Mesh or Materials assigned — skipped. " +
                                 "If you recently enabled multi-material support, re-assign the Materials array " +
                                 "in each FoliageType entry in the Inspector (the old 'Material' field was renamed).");
                continue;
            }

            var positions = new List<Vector3>();
            var rotations = new List<float>();
            var scales    = new List<Vector3>();

            if (foliage.spawnInPatches)
            {
                for (int p = 0; p < foliage.patchCount; p++)
                {
                    float cx = Random.Range(minX, maxX);
                    float cz = Random.Range(minZ, maxZ);

                    int patchTarget   = Random.Range(foliage.instancesPerPatch.x, foliage.instancesPerPatch.y + 1);
                    int patchAttempts = 0, patchSpawned = 0;

                    while (patchSpawned < patchTarget && patchAttempts < patchTarget * 10)
                    {
                        patchAttempts++;
                        float angle = Random.Range(0f, Mathf.PI * 2f);
                        float dist  = Random.Range(0f, foliage.patchRadius);
                        float x = Mathf.Clamp(cx + Mathf.Cos(angle) * dist, minX, maxX);
                        float z = Mathf.Clamp(cz + Mathf.Sin(angle) * dist, minZ, maxZ);

                        Ray ray = new Ray(new Vector3(x, rayOriginY, z), Vector3.down);
                        if (!Physics.Raycast(ray, out RaycastHit hit, rayLength, surfaceLayers)) continue;
                        if (TooClose(hit.point, positions, foliage.minimumDistance)) continue;

                        AddInstance(hit.point, foliage, positions, rotations, scales);
                        patchSpawned++;
                    }
                }
            }
            else
            {
                int targetCount = Mathf.Max(1, Mathf.RoundToInt(size.x * size.z * foliage.density));
                int attempts = 0, spawned = 0;
                int maxAttempts = targetCount * 10;

                while (spawned < targetCount && attempts < maxAttempts)
                {
                    attempts++;
                    float x = Random.Range(minX, maxX);
                    float z = Random.Range(minZ, maxZ);

                    Ray ray = new Ray(new Vector3(x, rayOriginY, z), Vector3.down);
                    if (!Physics.Raycast(ray, out RaycastHit hit, rayLength, surfaceLayers)) continue;
                    if (TooClose(hit.point, positions, foliage.minimumDistance)) continue;

                    AddInstance(hit.point, foliage, positions, rotations, scales);
                    spawned++;
                }
            }

            savedInstances[t].positions = positions.ToArray();
            savedInstances[t].rotations = rotations.ToArray();
            savedInstances[t].scales    = scales.ToArray();
            if (savedInstances[t].Count == 0)
                Debug.LogWarning($"[FoliageArea] '{gameObject.name}' — '{foliage.name}': 0 instances. " +
                                 $"Raycasts fired from Y={rayOriginY:F1} downward {rayLength:F1} units " +
                                 $"in X=[{minX:F1}, {maxX:F1}] Z=[{minZ:F1}, {maxZ:F1}]. " +
                                 "Check that the baked mesh exists (Bake first), its MeshCollider overlaps " +
                                 "this area, and the Surface Layers mask includes the baked mesh's layer.");
            else
                Debug.Log($"[FoliageArea] '{gameObject.name}' — '{foliage.name}': {savedInstances[t].Count} instances.");
        }

        RebuildBatches();
    }

    private static bool TooClose(Vector3 candidate, List<Vector3> placed, float minDist)
    {
        if (minDist <= 0f) return false;
        float minDistSqr = minDist * minDist;
        foreach (var p in placed)
            if ((candidate - p).sqrMagnitude < minDistSqr) return true;
        return false;
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

    public void Clear()
    {
        savedInstances = new FoliageInstanceData[0];
        allBatches.Clear();
        Debug.Log($"[FoliageArea] '{gameObject.name}' cleared.");
    }

    /// <summary>
    /// Provides serialized instance positions, rotations (Y euler), and scales for a given
    /// foliage type index. Used by WorldPainter.BakeFoliage() to merge geometry into a
    /// static mesh. Returns empty arrays if the type index is out of range.
    /// </summary>
    public void GetInstancesForBake(int typeIndex,
        out Vector3[] positions, out float[] rotations, out Vector3[] scales)
    {
        if (savedInstances == null || typeIndex < 0 || typeIndex >= savedInstances.Length)
        {
            positions = new Vector3[0]; rotations = new float[0]; scales = new Vector3[0];
            return;
        }
        var d = savedInstances[typeIndex];
        if (d == null)
        {
            positions = new Vector3[0]; rotations = new float[0]; scales = new Vector3[0];
            return;
        }
        positions = d.positions ?? new Vector3[0];
        rotations = d.rotations ?? new float[0];
        scales    = d.scales    ?? new Vector3[0];
    }

    // ---- Rendering ----

    private void LateUpdate()
    {
        // When under a WorldPainter, it handles rendering centrally with shared frustum data.
        if (managedExternally) return;

        Camera cam = Camera.main;
        if (cam == null) { DrawAllBatches(); return; }

        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
        DrawBatches(planes, cam.transform.position);
    }

    /// <summary>Called by WorldPainter each frame with pre-computed frustum planes so the
    /// computation is shared across all areas instead of repeated per-area.</summary>
    public void DrawBatches(Plane[] frustumPlanes, Vector3 camPos)
    {
        if (allBatches == null || foliageTypes == null || allBatches.Count == 0) return;

        // Cull entire area against camera frustum — skip if completely off-screen.
        if (!GeometryUtility.TestPlanesAABB(frustumPlanes, new Bounds(transform.position, size))) return;

        // Distance cull — keep the 2× factor from the original so render distance
        // behaviour is unchanged from before centralisation.
        float renderDistSqr = renderDistance * renderDistance * 4f;
        if ((transform.position - camPos).sqrMagnitude > renderDistSqr) return;

        DrawAllBatches();
    }

    public void DrawAllBatches()
    {
        for (int t = 0; t < foliageTypes.Length && t < allBatches.Count; t++)
        {
            var foliage = foliageTypes[t];
            if (foliage.mesh == null || foliage.materials == null || foliage.materials.Length == 0) continue;
            int subCount = Mathf.Min(foliage.mesh.subMeshCount, foliage.materials.Length);
            foreach (var batch in allBatches[t])
            {
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
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.15f);
        Gizmos.DrawCube(transform.position, size);
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 1f);
        Gizmos.DrawWireCube(transform.position, size);
    }
}

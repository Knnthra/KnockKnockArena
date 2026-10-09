using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Bakes smoothed normals into the tangent channel for better outline rendering.
/// This solves the issue where outlines break apart on hard edges.
/// Works with both MeshFilter and SkinnedMeshRenderer components.
///
/// Usage:
/// 1. Select a GameObject with a MeshFilter or SkinnedMeshRenderer in the scene
/// 2. Go to Tools > Bake Smooth Normals
/// 3. The mesh will have smoothed normals stored in its tangent channel
/// 4. The baked mesh is saved to Assets/Models/BakedMeshes/
/// 5. For .prefab files, the prefab asset is automatically updated
/// </summary>
public class SmoothNormalsBaker : MonoBehaviour
{
#if UNITY_EDITOR
    [MenuItem("Tools/Bake Smooth Normals")]
    public static void BakeSmoothNormals()
    {
        // Get selected object
        GameObject selectedObject = Selection.activeGameObject;

        if (selectedObject == null)
        {
            Debug.LogError("Please select a GameObject with a MeshFilter or SkinnedMeshRenderer");
            return;
        }

        // Try to get MeshFilter first, then SkinnedMeshRenderer
        MeshFilter meshFilter = selectedObject.GetComponent<MeshFilter>();
        SkinnedMeshRenderer skinnedMeshRenderer = selectedObject.GetComponent<SkinnedMeshRenderer>();

        Mesh originalMesh = null;
        bool isSkinnedMesh = false;

        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            originalMesh = meshFilter.sharedMesh;
            isSkinnedMesh = false;
        }
        else if (skinnedMeshRenderer != null && skinnedMeshRenderer.sharedMesh != null)
        {
            originalMesh = skinnedMeshRenderer.sharedMesh;
            isSkinnedMesh = true;
        }
        else
        {
            Debug.LogError("Selected GameObject must have a MeshFilter or SkinnedMeshRenderer with a mesh");
            return;
        }

        // Create a copy of the mesh
        Mesh bakedMesh = CreateMeshCopy(originalMesh);

        // Apply smooth normals to the copy
        BakeSmoothNormalsToMesh(bakedMesh);

        // Save the mesh as an asset
        string assetPath = $"Assets/Models/BakedMeshes/{originalMesh.name}_Baked.asset";

        // Check if asset already exists
        Mesh existingAsset = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existingAsset != null)
        {
            // Update existing asset
            existingAsset.Clear();
            // Clear() resets indexFormat to UInt16 — restore before writing triangles.
            existingAsset.indexFormat = bakedMesh.indexFormat;
            existingAsset.vertices = bakedMesh.vertices;
            existingAsset.normals = bakedMesh.normals;
            existingAsset.tangents = bakedMesh.tangents;
            existingAsset.uv = bakedMesh.uv;
            existingAsset.uv2 = bakedMesh.uv2;
            existingAsset.uv3 = bakedMesh.uv3;
            existingAsset.uv4 = bakedMesh.uv4;
            existingAsset.colors = bakedMesh.colors;
            existingAsset.boneWeights = bakedMesh.boneWeights;
            existingAsset.bindposes = bakedMesh.bindposes;
            existingAsset.subMeshCount = bakedMesh.subMeshCount;
            for (int i = 0; i < bakedMesh.subMeshCount; i++)
                existingAsset.SetTriangles(bakedMesh.GetTriangles(i), i);
            existingAsset.RecalculateBounds();
            EditorUtility.SetDirty(existingAsset);
            Debug.Log($"Updated existing baked mesh asset: {assetPath}");
        }
        else
        {
            // Create new asset
            AssetDatabase.CreateAsset(bakedMesh, assetPath);
            Debug.Log($"Created new baked mesh asset: {assetPath}");
        }

        // Get the final baked mesh reference
        Mesh finalBakedMesh = existingAsset != null ? existingAsset : bakedMesh;

        // Update the MeshFilter or SkinnedMeshRenderer to use the baked mesh
        if (isSkinnedMesh)
        {
            skinnedMeshRenderer.sharedMesh = finalBakedMesh;
        }
        else
        {
            meshFilter.sharedMesh = finalBakedMesh;
        }

        // Check if this object is a prefab instance
        bool isPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(selectedObject);
        string prefabPath = null;

        if (isPrefabInstance)
        {
            // Get the prefab asset path
            GameObject prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(selectedObject);
            prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot);

            // Check if it's an actual .prefab file (not an FBX or other model file)
            if (prefabPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                // Load the prefab contents for editing
                GameObject prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);

                if (prefabContents != null)
                {
                    bool updated = false;

                    if (isSkinnedMesh)
                    {
                        // Find the corresponding SkinnedMeshRenderer in the prefab
                        SkinnedMeshRenderer prefabSkinnedMeshRenderer = FindComponentInPrefab<SkinnedMeshRenderer>(prefabContents, selectedObject, prefabRoot);

                        if (prefabSkinnedMeshRenderer != null)
                        {
                            // Update the prefab's mesh reference
                            prefabSkinnedMeshRenderer.sharedMesh = finalBakedMesh;
                            updated = true;
                        }
                    }
                    else
                    {
                        // Find the corresponding MeshFilter in the prefab
                        MeshFilter prefabMeshFilter = FindComponentInPrefab<MeshFilter>(prefabContents, selectedObject, prefabRoot);

                        if (prefabMeshFilter != null)
                        {
                            // Update the prefab's mesh reference
                            prefabMeshFilter.sharedMesh = finalBakedMesh;
                            updated = true;
                        }
                    }

                    if (updated)
                    {
                        // Save the prefab with the modifications
                        PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);
                        Debug.Log($"Updated prefab asset: {prefabPath}");
                    }
                    else
                    {
                        Debug.LogWarning($"Could not find matching component in prefab: {prefabPath}");
                    }

                    // Unload the prefab contents
                    PrefabUtility.UnloadPrefabContents(prefabContents);
                }
            }
            else
            {
                // This is an FBX or model file instance - we can't modify the source asset
                // Just apply the change to the instance in the scene
                Debug.LogWarning($"Selected object is part of a model asset ({prefabPath}), not a .prefab file. The baked mesh has been applied to the scene instance only.");
                Debug.LogWarning("To persist changes, create a prefab from this object after baking.");
            }
        }
        else
        {
            // Regular scene object - mark scene as dirty
            if (isSkinnedMesh)
            {
                EditorUtility.SetDirty(skinnedMeshRenderer);
                if (skinnedMeshRenderer.gameObject.scene.name != null)
                {
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(skinnedMeshRenderer.gameObject.scene);
                }
            }
            else
            {
                EditorUtility.SetDirty(meshFilter);
                if (meshFilter.gameObject.scene.name != null)
                {
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(meshFilter.gameObject.scene);
                }
            }
        }

        // Save assets
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Smoothed normals baked and saved for mesh: {originalMesh.name}");
        Debug.Log($"Baked mesh saved to: {assetPath}");

        if (isPrefabInstance)
        {
            Debug.Log($"Prefab updated: {prefabPath} - Changes will persist when scene is reopened");
        }
        else
        {
            Debug.Log("Scene object updated - Changes will persist when scene is reopened");
        }
    }

    private static T FindComponentInPrefab<T>(GameObject prefabAsset, GameObject sceneObject, GameObject prefabRoot) where T : Component
    {
        // If the selected object is the prefab root itself
        if (sceneObject == prefabRoot)
        {
            return prefabAsset.GetComponent<T>();
        }

        // Find the relative path from the prefab root to the selected object
        Transform currentTransform = sceneObject.transform;
        List<string> pathParts = new List<string>();

        while (currentTransform != null && currentTransform.gameObject != prefabRoot)
        {
            pathParts.Insert(0, currentTransform.name);
            currentTransform = currentTransform.parent;
        }

        // Navigate the same path in the prefab asset
        Transform prefabTransform = prefabAsset.transform;
        foreach (string part in pathParts)
        {
            prefabTransform = prefabTransform.Find(part);
            if (prefabTransform == null)
            {
                return null;
            }
        }

        return prefabTransform.GetComponent<T>();
    }

    private static Mesh CreateMeshCopy(Mesh originalMesh)
    {
        Mesh copy = new Mesh();
        copy.name = originalMesh.name + "_Baked";
        // Must match the source — UInt32 meshes (>65535 verts) will corrupt sub-mesh
        // triangle indices if the copy defaults to UInt16.
        copy.indexFormat = originalMesh.indexFormat;

        // Copy all mesh data
        copy.vertices = originalMesh.vertices;
        copy.normals = originalMesh.normals;
        copy.tangents = originalMesh.tangents;
        copy.uv = originalMesh.uv;
        copy.uv2 = originalMesh.uv2;
        copy.uv3 = originalMesh.uv3;
        copy.uv4 = originalMesh.uv4;
        copy.colors = originalMesh.colors;
        copy.boneWeights = originalMesh.boneWeights;
        copy.bindposes = originalMesh.bindposes;

        // Copy submeshes — must set subMeshCount before SetTriangles to preserve multi-material meshes
        copy.subMeshCount = originalMesh.subMeshCount;
        for (int i = 0; i < originalMesh.subMeshCount; i++)
        {
            copy.SetTriangles(originalMesh.GetTriangles(i), i);
        }

        copy.RecalculateBounds();

        return copy;
    }

    public static void BakeSmoothNormalsToMesh(Mesh mesh)
    {
        // Get original mesh data
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;

        // Calculate smoothed normals by averaging normals at same positions
        Dictionary<Vector3, List<int>> vertexDict = new Dictionary<Vector3, List<int>>();

        for (int i = 0; i < vertices.Length; i++)
        {
            if (!vertexDict.ContainsKey(vertices[i]))
            {
                vertexDict[vertices[i]] = new List<int>();
            }
            vertexDict[vertices[i]].Add(i);
        }

        Vector3[] smoothNormals = new Vector3[vertices.Length];

        foreach (var kvp in vertexDict)
        {
            Vector3 avgNormal = Vector3.zero;

            foreach (int index in kvp.Value)
            {
                avgNormal += normals[index];
            }

            avgNormal.Normalize();

            foreach (int index in kvp.Value)
            {
                smoothNormals[index] = avgNormal;
            }
        }

        // Store smoothed normals in tangent channel
        // Format: xyz = smoothed normal, w = 0 to indicate it contains normals
        Vector4[] tangents = new Vector4[vertices.Length];

        for (int i = 0; i < vertices.Length; i++)
        {
            tangents[i] = new Vector4(
                smoothNormals[i].x,
                smoothNormals[i].y,
                smoothNormals[i].z,
                0 // w = 0 to flag this as containing smoothed normals
            );
        }

        mesh.tangents = tangents;
    }

    [MenuItem("Tools/Bake Smooth Normals", true)]
    public static bool ValidateBakeSmoothNormals()
    {
        GameObject selectedObject = Selection.activeGameObject;
        if (selectedObject == null) return false;

        return selectedObject.GetComponent<MeshFilter>() != null ||
               selectedObject.GetComponent<SkinnedMeshRenderer>() != null;
    }
#endif
}

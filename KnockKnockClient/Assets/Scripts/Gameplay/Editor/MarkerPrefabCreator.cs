using System.IO;
using KnockKnockArena.Gameplay;
using KnockKnockArena.Shared.Protocol.Udp;
using UnityEditor;
using UnityEngine;

namespace KnockKnockArena.Gameplay.Editor
{
    /// <summary>
    /// Creates the marker prefabs used to author pickups and spawn points — one per
    /// pickup type plus a spawn point. Add them to the World Painter's PROP PALETTE
    /// and paint them in the Props mode: props sit ON TOP of blocks (the ground
    /// stays intact underneath) and are instantiated as real objects at bake, so
    /// their marker components reach the arena export.
    ///
    /// The primitive visuals are placeholders — replace the child mesh with real
    /// models later; only the marker component and position matter to the export.
    /// </summary>
    public static class MarkerPrefabCreator
    {
        private const string Folder = "Assets/Prefabs/Markers";

        [MenuItem("KnockKnock/Create Marker Prefabs (Pickups + Spawn)")]
        public static void CreateAll()
        {
            Directory.CreateDirectory(Folder);

            foreach (PickupType type in System.Enum.GetValues(typeof(PickupType)))
            {
                CreatePrefab($"Pickup_{type}", go =>
                {
                    go.AddComponent<PickupMarker>().Type = type;
                    GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    visual.name = "Visual (placeholder)";
                    visual.transform.SetParent(go.transform, false);
                    visual.transform.localPosition = new Vector3(0f, 0.35f, 0f);
                    visual.transform.localScale = Vector3.one * 0.7f;
                    Object.DestroyImmediate(visual.GetComponent<Collider>());
                });
            }

            // No visual on the spawn point: it must be invisible in game (players
            // spawn there, nothing should stand there). SpawnPointMarker draws a
            // cyan gizmo, so it stays visible in the Scene view for authoring.
            CreatePrefab("SpawnPoint", go => go.AddComponent<SpawnPointMarker>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Marker prefabs created in {Folder}: one per pickup type + SpawnPoint. " +
                      "Add them to the World Painter's PROP PALETTE (Inspector), switch the " +
                      "painter window to the Props mode, and paint your pickups and spawns on " +
                      "top of the ground.");
        }

        private static void CreatePrefab(string name, System.Action<GameObject> build)
        {
            string path = $"{Folder}/{name}.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                return; // keep existing (possibly customized) prefabs

            GameObject temp = new GameObject(name);
            try
            {
                build(temp);
                PrefabUtility.SaveAsPrefabAsset(temp, path);
            }
            finally
            {
                Object.DestroyImmediate(temp);
            }
        }
    }
}

using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(FoliageSpawner))]
public class FoliageSpawnerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        GUILayout.Space(10);

        FoliageSpawner spawner = (FoliageSpawner)target;

        GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
        if (GUILayout.Button("Generate Foliage", GUILayout.Height(30)))
        {
            Undo.RecordObject(spawner, "Generate Foliage");
            spawner.Generate();
            EditorUtility.SetDirty(spawner);
        }

        GUILayout.Space(5);

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear", GUILayout.Height(25)))
        {
            Undo.RecordObject(spawner, "Clear Foliage");
            spawner.Clear();
            EditorUtility.SetDirty(spawner);
        }

        GUI.backgroundColor = Color.white;

        int total = spawner.TotalInstanceCount;
        if (total > 0)
        {
            GUILayout.Space(5);
            EditorGUILayout.HelpBox($"Total: {total:N0} instances", MessageType.Info);

            if (spawner.foliageTypes != null)
            {
                for (int i = 0; i < spawner.foliageTypes.Length; i++)
                {
                    int count = spawner.GetTypeInstanceCount(i);
                    if (count > 0)
                    {
                        string typeName = spawner.foliageTypes[i]?.name ?? $"Type {i}";
                        EditorGUILayout.LabelField($"  {typeName}: {count:N0}", EditorStyles.miniLabel);
                    }
                }
            }
        }
    }
}

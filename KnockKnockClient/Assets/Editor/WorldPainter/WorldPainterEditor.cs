using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(WorldPainter))]
public class WorldPainterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var painter = (WorldPainter)target;

        // Don't show the blocks/zones arrays in inspector — they're huge and cause performance issues
        // Instead, manually draw only the important settings
        DrawPropertiesExcluding(serializedObject, "m_Script", "blocks", "foliageZones", "waterfallZones");
        serializedObject.ApplyModifiedProperties();
        GUILayout.Space(8);

        EditorGUILayout.HelpBox($"{painter.BlockCount} blocks placed", MessageType.Info);

        GUILayout.Space(4);

        GUI.backgroundColor = new Color(0.5f, 0.9f, 1f);
        if (GUILayout.Button("Edit in Scene View", GUILayout.Height(30)))
            UnityEditor.EditorTools.ToolManager.SetActiveTool<WorldPainterSceneTool>();
        GUI.backgroundColor = Color.white;

        GUILayout.Space(2);

        if (GUILayout.Button("Open World Painter Window", GUILayout.Height(26)))
            WorldPainterWindow.Open(painter);

        GUILayout.Space(4);

        GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
        if (GUILayout.Button("Bake", GUILayout.Height(28)))
        {
            var p = painter;
            EditorApplication.delayCall += () =>
            {
                if (p == null) return;
                p.Bake();
                EditorUtility.SetDirty(p);
            };
        }

        GUILayout.Space(2);

        GUI.backgroundColor = new Color(0.45f, 0.85f, 0.55f);
        if (GUILayout.Button("Generate & Bake Foliage", GUILayout.Height(24)))
        {
            var p = painter;
            EditorApplication.delayCall += () =>
            {
                if (p == null) return;
                p.GenerateFoliage();
                p.BakeFoliage();
                EditorUtility.SetDirty(p);
                if (!Application.isPlaying)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(p.gameObject.scene);
            };
        }

        GUI.backgroundColor = new Color(0.35f, 0.55f, 0.95f);
        if (GUILayout.Button("Generate Water", GUILayout.Height(24)))
        {
            var p = painter;
            EditorApplication.delayCall += () =>
            {
                if (p == null) return;
                p.GenerateWaterfall();
                EditorUtility.SetDirty(p);
                if (!Application.isPlaying)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(p.gameObject.scene);
            };
        }

        GUILayout.Space(2);

        GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Save Data", GUILayout.Height(24)))
        {
            painter.SaveToFile();
        }
        if (GUILayout.Button("Load Data", GUILayout.Height(24)))
        {
            if (EditorUtility.DisplayDialog("Load WorldPainter Data",
                    "This will replace all current blocks, foliage zones, and waterfall zones with the data from disk. Continue?",
                    "Load", "Cancel"))
            {
                Undo.RecordObject(painter, "Load WorldPainter Data");
                painter.LoadFromFile();
                EditorUtility.SetDirty(painter);
            }
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(2);

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear", GUILayout.Height(24)))
        {
            if (EditorUtility.DisplayDialog("Clear World Painter",
                    "Remove all placed blocks, foliage zones/meshes, and water zones?",
                    "Clear", "Cancel"))
            {
                Undo.RecordObject(painter, "Clear WorldPainter");
                painter.Clear();
                EditorUtility.SetDirty(painter);
            }
        }

        GUI.backgroundColor = Color.white;
    }
}

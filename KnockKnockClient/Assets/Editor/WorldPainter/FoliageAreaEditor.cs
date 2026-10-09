using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;

[CustomEditor(typeof(FoliageArea))]
public class FoliageAreaEditor : Editor
{
    private readonly BoxBoundsHandle _handle = new BoxBoundsHandle();

    private void OnSceneGUI()
    {
        FoliageArea area = (FoliageArea)target;

        _handle.center = area.transform.position;
        _handle.size   = area.size;

        EditorGUI.BeginChangeCheck();
        _handle.DrawHandle();
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(area, "Resize Foliage Area");
            area.size                = _handle.size;
            area.transform.position = _handle.center;
            EditorUtility.SetDirty(area);
        }
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        GUILayout.Space(10);
        FoliageArea area = (FoliageArea)target;

        GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
        if (GUILayout.Button("Generate Foliage", GUILayout.Height(30)))
        {
            Undo.RecordObject(area, "Generate Foliage");
            Physics.SyncTransforms();
            area.Generate();
            EditorUtility.SetDirty(area);
        }

        GUILayout.Space(5);

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear", GUILayout.Height(25)))
        {
            Undo.RecordObject(area, "Clear Foliage");
            area.Clear();
            EditorUtility.SetDirty(area);
        }

        GUI.backgroundColor = Color.white;

        int total = area.TotalInstanceCount;
        if (total > 0)
        {
            GUILayout.Space(5);
            EditorGUILayout.HelpBox($"Total: {total:N0} instances", MessageType.Info);
            if (area.foliageTypes != null)
            {
                for (int i = 0; i < area.foliageTypes.Length; i++)
                {
                    int count = area.GetTypeInstanceCount(i);
                    if (count > 0)
                        EditorGUILayout.LabelField($"  {area.foliageTypes[i]?.name ?? $"Type {i}"}: {count:N0}", EditorStyles.miniLabel);
                }
            }
        }
    }
}

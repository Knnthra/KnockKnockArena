using UnityEditor;
using UnityEngine;

/// <summary>
/// Draws the AutoTileGrid field as a compact 3×3 grid of object pickers,
/// each with a small Y-offset float field below it.
///
///   Col 0 = Left  (+X neighbour only)
///   Col 1 = Center (both ±X, or neither)
///   Col 2 = Right  (-X neighbour only)
///
///   Row 0 = Front  (+Z neighbour only)
///   Row 1 = Center (both ±Z, or neither)
///   Row 2 = Back   (-Z neighbour only)
/// </summary>
[CustomPropertyDrawer(typeof(WorldPainter.AutoTileGrid))]
public class AutoTileGridDrawer : PropertyDrawer
{
    // Column / row labels shown above / left of the grid
    private static readonly string[] ColLabels = { "Left (+X)", "Center", "Right (-X)" };
    private static readonly string[] RowLabels = { "Front(+Z)", "Center", "Back (-Z)" };

    private const float RowLabelW = 62f;
    private const float ColH      = 18f;   // column-header row height
    private const float SlotH     = 18f;   // object-field height
    private const float OffH      = 14f;   // Y-offset float field height
    private const float CellPad   = 1f;    // gap between object field and offset field
    private const float SlotPad   = 2f;    // gap between rows

    // Height of one cell: prefab picker + gap + offset field
    private static float CellH => SlotH + CellPad + OffH;

    // Total grid body height: col headers + 3 cell rows
    private static float GridBodyH =>
        ColH + 3 * (CellH + SlotPad) + SlotPad;

    public override float GetPropertyHeight(SerializedProperty prop, GUIContent label)
    {
        if (!prop.isExpanded)
            return EditorGUIUtility.singleLineHeight;

        return EditorGUIUtility.singleLineHeight + SlotPad + GridBodyH;
    }

    public override void OnGUI(Rect pos, SerializedProperty prop, GUIContent label)
    {
        // Foldout header
        var headerRect = new Rect(pos.x, pos.y, pos.width, EditorGUIUtility.singleLineHeight);
        prop.isExpanded = EditorGUI.Foldout(headerRect, prop.isExpanded,
            label.text + "  (3×3 auto-tile grid)", true);

        if (!prop.isExpanded) return;

        var tiles    = prop.FindPropertyRelative("tiles");
        var offsets  = prop.FindPropertyRelative("offsets");
        var xOffsets = prop.FindPropertyRelative("xOffsets");

        // Ensure backing arrays are always exactly 9 elements
        if (tiles.arraySize    != 9) tiles.arraySize    = 9;
        if (offsets.arraySize  != 9) offsets.arraySize  = 9;
        if (xOffsets.arraySize != 9) xOffsets.arraySize = 9;

        float slotW = (pos.width - RowLabelW - SlotPad * 2) / 3f;
        float gridY = pos.y + EditorGUIUtility.singleLineHeight + SlotPad;

        // ── Column header labels ─────────────────────────────────────────────
        for (int col = 0; col < 3; col++)
        {
            var r = new Rect(
                pos.x + RowLabelW + col * (slotW + SlotPad),
                gridY,
                slotW, ColH);
            EditorGUI.LabelField(r, ColLabels[col], EditorStyles.centeredGreyMiniLabel);
        }

        gridY += ColH;

        // ── 3 rows ───────────────────────────────────────────────────────────
        for (int row = 0; row < 3; row++)
        {
            // Row label — vertically centred in the taller cell
            var rowLabelRect = new Rect(pos.x, gridY + (CellH - SlotH) * 0.5f,
                                        RowLabelW - SlotPad, SlotH);
            EditorGUI.LabelField(rowLabelRect, RowLabels[row], EditorStyles.miniLabel);

            for (int col = 0; col < 3; col++)
            {
                int   idx   = row * 3 + col;
                float slotX = pos.x + RowLabelW + col * (slotW + SlotPad);

                var slotProp = tiles.GetArrayElementAtIndex(idx);
                var slotRect = new Rect(slotX, gridY, slotW, SlotH);

                // Tint the center slot (1,1) as the fallback indicator
                if (col == 1 && row == 1)
                {
                    var bg = new Rect(slotRect.x - 1, slotRect.y - 1,
                                      slotRect.width + 2, CellH + 2);
                    EditorGUI.DrawRect(bg, new Color(0.25f, 0.55f, 0.85f, 0.18f));
                }

                EditorGUI.ObjectField(slotRect, slotProp, typeof(GameObject), GUIContent.none);

                // X and Y offset fields side-by-side below the object picker
                float offY    = gridY + SlotH + CellPad;
                float labelW  = 10f;
                float halfW   = (slotW - labelW * 2 - 2f) / 2f;
                var miniLabel = EditorStyles.centeredGreyMiniLabel;

                // X offset
                EditorGUI.LabelField(new Rect(slotX, offY, labelW, OffH), "X", miniLabel);
                var xOffProp = xOffsets.GetArrayElementAtIndex(idx);
                EditorGUI.PropertyField(new Rect(slotX + labelW, offY, halfW, OffH), xOffProp, GUIContent.none);

                // Y offset
                float yStart = slotX + labelW + halfW + 2f;
                EditorGUI.LabelField(new Rect(yStart, offY, labelW, OffH), "Y", miniLabel);
                var offProp = offsets.GetArrayElementAtIndex(idx);
                EditorGUI.PropertyField(new Rect(yStart + labelW, offY, halfW, OffH), offProp, GUIContent.none);
            }

            gridY += CellH + SlotPad;
        }
    }
}

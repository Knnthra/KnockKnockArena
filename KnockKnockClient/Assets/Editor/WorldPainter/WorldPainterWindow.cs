using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Tools > World Painter — 2D grid editor for placing blocks layer by layer.
/// Design your map here, then press Create &amp; Bake to generate the optimised mesh
/// and save it as a prefab.
/// </summary>
public class WorldPainterWindow : EditorWindow
{
    // ---- State ----

    private enum PaintMode { Blocks, Foliage, Water, Props }

    private static readonly Color PropColor = new Color(1f, 0.5f, 0.85f); // fallback — props layer
    private int _selectedProp;

    /// <summary>Per-type prop color from the palette; magenta-pink fallback.</summary>
    private Color GetPropColor(int index)
    {
        var palette = _painter != null ? _painter.propPalette : null;
        if (palette != null && index >= 0 && index < palette.Length && palette[index] != null)
            return palette[index].color;
        return PropColor;
    }

    private WorldPainter _painter;
    private int          _selectedType;
    private int          _currentLayer;
    private bool         _eraseMode;
    private Vector2      _gridScroll;
    private float        _cellSize   = 22f;
    private int          _gridWidth  = 20;
    private int          _gridDepth  = 20;
    // World-space cell coordinate of the grid's bottom-left corner — lets the
    // window view/edit regions with negative coordinates (scene tool paints there).
    private int          _originX;
    private int          _originZ;
    private int          _selectedVariant = -1;

    private GUIStyle _cellLabelStyle;

    private bool _showLayerBelow     = true;
    private bool _showLayerAbove     = true;
    private bool _fillDown;
    private PaintMode _paintMode     = PaintMode.Blocks;
    private int  _foliagePaintMask = 1;   // bitmask — bit i = type i enabled for painting

    private bool       _strokeIsErase;
    private bool       _boxPaintMode;
    private Vector2Int _boxStart;
    private Vector2Int _boxCurrent;
    private bool       _isBoxDragging;

    private bool       _isPanning;
    private Vector2    _panStartMouse;
    private Rect       _gridViewRect;
    private Vector2    _settingsScroll;

    private Vector2Int _hoveredCell;
    private bool       _mouseInGrid;
    private Vector2    _hoveredMousePos;

    // SerializedObject for foliage settings — rebuilt when painter changes
    private SerializedObject _serializedPainter;

    // ---- EditorPrefs keys ----

    private const string PrefW    = "WP_GridW";
    private const string PrefD    = "WP_GridD";
    private const string PrefZoom = "WP_CellSize";
    private const string PrefOX   = "WP_OriginX";
    private const string PrefOZ   = "WP_OriginZ";

    private void OnEnable()
    {
        _gridWidth = EditorPrefs.GetInt(PrefW,    20);
        _gridDepth = EditorPrefs.GetInt(PrefD,    20);
        _cellSize  = Mathf.Max(4f, EditorPrefs.GetFloat(PrefZoom, 22f));
        _originX   = EditorPrefs.GetInt(PrefOX, 0);
        _originZ   = EditorPrefs.GetInt(PrefOZ, 0);

        wantsMouseMove = true;   // required to receive EventType.MouseMove (not just MouseDrag)

        EditorApplication.playModeStateChanged += HandlePlayModeChange;

        // Restore painter after domain reload (play mode entry/exit destroys managed state)
        TryRestorePainter();
    }

    private void OnDisable()
    {
        EditorPrefs.SetInt(PrefW,    _gridWidth);
        EditorPrefs.SetInt(PrefD,    _gridDepth);
        EditorPrefs.SetFloat(PrefZoom, _cellSize);
        EditorPrefs.SetInt(PrefOX,   _originX);
        EditorPrefs.SetInt(PrefOZ,   _originZ);

        EditorApplication.playModeStateChanged -= HandlePlayModeChange;
    }

    private void HandlePlayModeChange(PlayModeStateChange state)
    {
        // Scene objects are re-created when entering/exiting play mode.
        // Re-link the painter reference so the window stays usable.
        if (state == PlayModeStateChange.EnteredPlayMode ||
            state == PlayModeStateChange.EnteredEditMode)
        {
            _serializedPainter = null;
            TryRestorePainter();
            Repaint();
        }
    }

    // Attempts to restore _painter after the reference is lost to a domain reload.
    // Prefers the currently selected GameObject, falls back to any WorldPainter in the scene.
    private void TryRestorePainter()
    {
        if (_painter != null) return;

        var sel = Selection.activeGameObject?.GetComponent<WorldPainter>();
        if (sel != null) { SetPainter(sel); return; }

#if UNITY_2023_1_OR_NEWER
        var found = FindAnyObjectByType<WorldPainter>();
#else
        var found = FindObjectOfType<WorldPainter>();
#endif
        if (found != null) SetPainter(found);
    }

    // ---- Static colours ----

    private static readonly Color EmptyCell     = new Color(0.18f, 0.18f, 0.18f);
    private static readonly Color GridLine      = new Color(0f,    0f,    0f,    0.45f);
    private static readonly Color FoliageColor  = new Color(0.15f, 0.75f, 0.25f);
    private static readonly Color WaterColor    = new Color(0.20f, 0.50f, 0.90f);
    private static readonly Color FoliageDefaultColor = new Color(0.15f, 0.75f, 0.25f);
    private static readonly Color MultiTypeColor      = new Color(0.08f, 0.40f, 0.13f); // dark green

    /// Returns the tile color for a foliage type, reading from the FoliageType asset.
    private Color GetFoliageTypeColor(int typeIndex)
    {
        var types = _painter?.foliageSettings?.foliageTypes;
        if (types == null || typeIndex < 0 || typeIndex >= types.Length || types[typeIndex] == null)
            return FoliageDefaultColor;
        Color c = types[typeIndex].tileColor;
        return c.a > 0.01f ? c : FoliageDefaultColor;
    }

    /// Returns a display color for a zone bitmask: single-type = that type's tileColor;
    /// multi-type = fixed dark green.
    private Color GetZoneDisplayColor(int mask)
    {
        if (mask == 0) mask = 1;
        int count = 0;
        int lastBit = 0;
        for (int i = 0; i < 31; i++)
            if ((mask & (1 << i)) != 0) { count++; lastBit = i; }
        if (count > 1) return MultiTypeColor;
        return GetFoliageTypeColor(lastBit);
    }
    private static readonly Color[] FallbackColors =
    {
        new Color(0.30f, 0.68f, 0.30f),   // green
        new Color(0.58f, 0.42f, 0.18f),   // brown
        new Color(0.55f, 0.55f, 0.55f),   // grey
        new Color(0.25f, 0.45f, 0.75f),   // blue
        new Color(0.75f, 0.60f, 0.20f),   // gold
        new Color(0.60f, 0.25f, 0.25f),   // red
    };

    // ---- Open ----

    [MenuItem("Tools/World Painter")]
    public static void Open() => Open(null);

    public static void Open(WorldPainter painter)
    {
        var win = GetWindow<WorldPainterWindow>("World Painter");
        if (painter != null) win.SetPainter(painter);
        win.minSize = new Vector2(420, 320);
    }

    private void SetPainter(WorldPainter p)
    {
        _painter           = p;
        _serializedPainter = null;   // invalidate so it gets rebuilt next repaint
    }

    // Auto-select WorldPainter when the user clicks one in the hierarchy
    private void OnSelectionChange()
    {
        var go = Selection.activeGameObject;
        if (go == null) return;
        var p = go.GetComponent<WorldPainter>();
        if (p != null) { SetPainter(p); Repaint(); }
    }

    // ---- Main GUI ----

    private void OnGUI()
    {
        DrawHeader();

        if (_painter == null)
        {
            GUILayout.Space(16);
            EditorGUILayout.HelpBox(
                "Select a WorldPainter in the Hierarchy, or click New to create one.",
                MessageType.Info);
            return;
        }

        // Clamp layer in case the painter was cleared
        _currentLayer = Mathf.Max(0, _currentLayer);

        EditorGUILayout.BeginHorizontal();
        DrawLayerList();
        EditorGUILayout.BeginVertical();
        DrawGridToolbar();
        DrawGrid();
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(3);

        if (_paintMode == PaintMode.Foliage)
        {
            _settingsScroll = EditorGUILayout.BeginScrollView(_settingsScroll,
                GUILayout.MaxHeight(position.height * 0.45f));
            DrawFoliageSettings();
            EditorGUILayout.EndScrollView();
        }
        else if (_paintMode == PaintMode.Water)
        {
            _settingsScroll = EditorGUILayout.BeginScrollView(_settingsScroll,
                GUILayout.MaxHeight(position.height * 0.45f));
            DrawWaterfallSettings();
            EditorGUILayout.EndScrollView();
        }
        else if (_paintMode == PaintMode.Props)
            DrawPropPalette();
        else
            DrawPalette();

        GUILayout.Space(3);
        DrawFooter();
    }

    // ---- Header: painter field + New button ----

    private void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        EditorGUILayout.LabelField("Painter:", GUILayout.Width(52));
        var next = (WorldPainter)EditorGUILayout.ObjectField(_painter, typeof(WorldPainter), true);
        if (next != _painter) { SetPainter(next); Repaint(); }

        if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(36)))
            CreateNewPainter();

        EditorGUILayout.EndHorizontal();
    }

    // ---- Layer list (left column) ----

    private void DrawLayerList()
    {
        int topLayer = Mathf.Max(_currentLayer, _painter.MaxUsedLayer) + 1;

        EditorGUILayout.BeginVertical(GUILayout.Width(56));
        EditorGUILayout.LabelField("Layer", EditorStyles.miniLabel, GUILayout.Width(56));

        for (int y = topLayer; y >= 0; y--)
        {
            GUI.backgroundColor = y == _currentLayer ? new Color(0.35f, 0.82f, 1f) : Color.white;
            if (GUILayout.Button($"Y = {y}", GUILayout.Width(56), GUILayout.Height(20)))
            {
                _currentLayer = y;
                Repaint();
            }
        }

        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndVertical();
    }

    // ---- Grid toolbar ----

    private void DrawGridToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        // Ghost layer toggles
        EditorGUILayout.LabelField("Ghost:", GUILayout.Width(40));
        _showLayerBelow = GUILayout.Toggle(_showLayerBelow, "▼ Below",
            EditorStyles.toolbarButton, GUILayout.Width(58));
        _showLayerAbove = GUILayout.Toggle(_showLayerAbove, "▲ Above",
            EditorStyles.toolbarButton, GUILayout.Width(58));

        GUILayout.Space(6);

        _fillDown = GUILayout.Toggle(_fillDown,
            new GUIContent("▼ Fill", "Paint/erase every layer from the current one down to 0 (blocks only)"),
            EditorStyles.toolbarButton, GUILayout.Width(44));

        GUILayout.Space(6);

        // Paint shape — Draw vs Box (independent change-check blocks to avoid same-frame cancel)
        EditorGUI.BeginChangeCheck();
        bool drawPressed = GUILayout.Toggle(!_boxPaintMode, "Draw",
            EditorStyles.toolbarButton, GUILayout.Width(42));
        if (EditorGUI.EndChangeCheck() && drawPressed) _boxPaintMode = false;

        EditorGUI.BeginChangeCheck();
        bool boxPressed = GUILayout.Toggle(_boxPaintMode, "□ Box",
            EditorStyles.toolbarButton, GUILayout.Width(44));
        if (EditorGUI.EndChangeCheck() && boxPressed) _boxPaintMode = true;

        GUILayout.Space(6);

        // Paint target — Blocks / Foliage / Water
        EditorGUI.BeginChangeCheck();
        bool blocksPressed = GUILayout.Toggle(_paintMode == PaintMode.Blocks, "Blocks",
            EditorStyles.toolbarButton, GUILayout.Width(48));
        if (EditorGUI.EndChangeCheck() && blocksPressed) _paintMode = PaintMode.Blocks;

        EditorGUI.BeginChangeCheck();
        bool foliagePressed = GUILayout.Toggle(_paintMode == PaintMode.Foliage, "Foliage",
            EditorStyles.toolbarButton, GUILayout.Width(50));
        if (EditorGUI.EndChangeCheck() && foliagePressed) _paintMode = PaintMode.Foliage;

        EditorGUI.BeginChangeCheck();
        bool waterPressed = GUILayout.Toggle(_paintMode == PaintMode.Water, "Water",
            EditorStyles.toolbarButton, GUILayout.Width(44));
        if (EditorGUI.EndChangeCheck() && waterPressed) _paintMode = PaintMode.Water;

        EditorGUI.BeginChangeCheck();
        bool propsPressed = GUILayout.Toggle(_paintMode == PaintMode.Props, "Props",
            EditorStyles.toolbarButton, GUILayout.Width(46));
        if (EditorGUI.EndChangeCheck() && propsPressed) _paintMode = PaintMode.Props;

        GUILayout.Space(6);

        // Copy layer buttons (only meaningful in block mode)
        GUI.enabled = _paintMode == PaintMode.Blocks && _currentLayer > 0;
        if (GUILayout.Button("↓ Copy", EditorStyles.toolbarButton, GUILayout.Width(52)))
            CopyLayer(_currentLayer - 1, _currentLayer);
        GUI.enabled = _paintMode == PaintMode.Blocks;
        if (GUILayout.Button("↑ Copy", EditorStyles.toolbarButton, GUILayout.Width(52)))
            CopyLayer(_currentLayer + 1, _currentLayer);
        GUI.enabled = true;

        GUILayout.FlexibleSpace();

        // Active mode indicator (right side)
        if (_paintMode == PaintMode.Foliage)
        {
            GUI.color = FoliageColor;
            GUILayout.Label("Foliage Zone", EditorStyles.boldLabel);
            GUI.color = Color.white;
        }
        else if (_paintMode == PaintMode.Water)
        {
            GUI.color = WaterColor;
            GUILayout.Label("Water Zone", EditorStyles.boldLabel);
            GUI.color = Color.white;
        }
        else if (_eraseMode)
        {
            GUI.color = new Color(1f, 0.55f, 0.55f);
            GUILayout.Label("✕ ERASE", EditorStyles.boldLabel, GUILayout.Width(64));
            GUI.color = Color.white;
        }
        else if (_painter?.palette != null && _selectedType < _painter.palette.Length)
        {
            var bt = _painter.palette[_selectedType];
            string tileName = bt?.name ?? $"T{_selectedType}";

            Rect swatchRect = GUILayoutUtility.GetRect(12, 12,
                GUILayout.Width(12), GUILayout.Height(12));
            swatchRect.y += 3;
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(swatchRect, GetTypeColor(_selectedType));
            GUILayout.Space(4);

            GUILayout.Label($"Painting: {tileName}", EditorStyles.boldLabel);
        }

        GUILayout.Space(4);
        EditorGUILayout.EndHorizontal();
    }

    // ---- 2D grid ----

    private void DrawGrid()
    {
        float totalW = _gridWidth  * _cellSize;
        float totalH = _gridDepth  * _cellSize;

        // ── Scroll wheel over grid changes layer (before ScrollView so it doesn't scroll) ──
        var evt = Event.current;
        if (evt.type == EventType.ScrollWheel && _gridViewRect.Contains(evt.mousePosition))
        {
            _currentLayer = Mathf.Max(0, _currentLayer - (int)Mathf.Sign(evt.delta.y));
            evt.Use();
            Repaint();
        }

        _gridScroll = EditorGUILayout.BeginScrollView(_gridScroll,
            GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

        Rect grid = GUILayoutUtility.GetRect(totalW, totalH);
        int paintID = GUIUtility.GetControlID(FocusType.Passive);

        // ── Middle-mouse pan ─────────────────────────────────────────────────
        int panID = GUIUtility.GetControlID(FocusType.Passive);
        if (evt.type == EventType.MouseDown && evt.button == 2 && grid.Contains(evt.mousePosition))
        {
            GUIUtility.hotControl = panID;
            _isPanning = true;
            _panStartMouse = GUIUtility.GUIToScreenPoint(evt.mousePosition);
            evt.Use();
        }
        if (evt.type == EventType.MouseDrag && _isPanning && GUIUtility.hotControl == panID)
        {
            Vector2 screenMouse = GUIUtility.GUIToScreenPoint(evt.mousePosition);
            _gridScroll += _panStartMouse - screenMouse;
            _panStartMouse = screenMouse;
            Repaint();
            evt.Use();
        }
        if (evt.type == EventType.MouseUp && evt.button == 2 && _isPanning)
        {
            GUIUtility.hotControl = 0;
            _isPanning = false;
            evt.Use();
        }

        // ── Hovered cell tracking ─────────────────────────────────────────────
        if (evt.type == EventType.MouseMove || evt.type == EventType.MouseDrag)
        {
            _mouseInGrid     = grid.Contains(evt.mousePosition);
            _hoveredCell     = GridCellAt(grid, evt.mousePosition);
            _hoveredMousePos = evt.mousePosition;
            Repaint();
        }
        if (evt.type == EventType.MouseLeaveWindow)
        {
            _mouseInGrid = false;
            Repaint();
        }

        // ── Draw ──────────────────────────────────────────────────────────────
        if (evt.type == EventType.Repaint)
        {
            if (_cellLabelStyle == null)
                _cellLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                    { alignment = TextAnchor.MiddleCenter };

            int maxUsedLayer = _painter.MaxUsedLayer;

            // Viewport culling — _gridViewRect holds the visible scroll-view rect
            // (captured after EndScrollView on the previous Repaint). CellRect
            // draws rows top-down with z inverted, so the vertical scroll offset
            // maps to screen rows first, then converts back to z indices.
            float viewW = _gridViewRect.width  > 0 ? _gridViewRect.width  : position.width;
            float viewH = _gridViewRect.height > 0 ? _gridViewRect.height : position.height;

            int minX   = Mathf.Max(0, Mathf.FloorToInt(_gridScroll.x / _cellSize));
            int maxX   = Mathf.Min(_gridWidth, minX + Mathf.CeilToInt(viewW / _cellSize) + 1);

            int rowMin = Mathf.Max(0, Mathf.FloorToInt(_gridScroll.y / _cellSize));
            int rowMax = Mathf.Min(_gridDepth, rowMin + Mathf.CeilToInt(viewH / _cellSize) + 1);
            int minZ   = _gridDepth - rowMax;
            int maxZ   = _gridDepth - rowMin;

            for (int z = minZ; z < maxZ; z++)
            {
                for (int x = minX; x < maxX; x++)
                {
                    Rect cell = CellRect(grid, x, z);
                    var  pos  = new Vector3Int(x + _originX, _currentLayer, z + _originZ);

                    // ── Current layer base ───────────────────────────────────
                    int typeIdx = _painter.GetBlockType(pos);
                    bool isEmpty = typeIdx < 0;
                    EditorGUI.DrawRect(cell, isEmpty ? EmptyCell : GetTypeColor(typeIdx));

                    // ── Ghost overlays — search all layers, show nearest block found ─
                    if (isEmpty && _showLayerBelow && _currentLayer > 0)
                    {
                        for (int dy = 1; dy <= _currentLayer; dy++)
                        {
                            int belowIdx = _painter.GetBlockType(new Vector3Int(x + _originX, _currentLayer - dy, z + _originZ));
                            if (belowIdx >= 0)
                            {
                                Color ghost = GetTypeColor(belowIdx);
                                ghost.a = Mathf.Max(0.10f, 0.45f - (dy - 1) * 0.10f);
                                EditorGUI.DrawRect(cell, ghost);
                                break;
                            }
                        }
                    }

                    if (_showLayerAbove && maxUsedLayer > _currentLayer)
                    {
                        for (int dy = 1; dy <= maxUsedLayer - _currentLayer; dy++)
                        {
                            int aboveIdx = _painter.GetBlockType(new Vector3Int(x + _originX, _currentLayer + dy, z + _originZ));
                            if (aboveIdx >= 0)
                            {
                                Color ghost = GetTypeColor(aboveIdx);
                                ghost.a = Mathf.Max(0.15f, 0.55f - (dy - 1) * 0.10f);
                                EditorGUI.DrawRect(cell, ghost);
                                break;
                            }
                        }
                    }

                    // ── Foliage zone overlay ─────────────────────────────────
                    // Color varies by foliage type; brighter when actively painting foliage
                    if (_painter.HasFoliageZone(pos))
                    {
                        int zoneMask = _painter.GetFoliageZoneMask(pos);
                        Color fc     = GetZoneDisplayColor(zoneMask);
                        fc.a = 1f;
                        EditorGUI.DrawRect(cell, fc);
                    }

                    // ── Waterfall zone overlay ────────────────────────────────
                    if (_painter.HasWaterfallZone(pos))
                    {
                        Color wc = WaterColor;
                        wc.a = _paintMode == PaintMode.Water ? 0.65f : 0.35f;
                        EditorGUI.DrawRect(cell, wc);
                    }

                    // ── Prop overlay (small centered diamond-ish square) ──────
                    if (_painter.HasProp(pos))
                    {
                        Color pc = GetPropColor(_painter.GetPropType(pos));
                        pc.a = _paintMode == PaintMode.Props ? 0.95f : 0.55f;
                        EditorGUI.DrawRect(new Rect(
                            cell.x + cell.width  * 0.28f,
                            cell.y + cell.height * 0.28f,
                            cell.width  * 0.44f,
                            cell.height * 0.44f), pc);
                    }

                    // Grid lines — skipped when cells are tiny; at that zoom they
                    // only add draw calls without being readable.
                    if (_cellSize >= 6f)
                    {
                        EditorGUI.DrawRect(new Rect(cell.xMax, cell.y,    1,         _cellSize), GridLine);
                        EditorGUI.DrawRect(new Rect(cell.x,    cell.yMax, _cellSize, 1),         GridLine);
                    }

                    // Abbreviated name label (blocks mode only)
                    if (_paintMode == PaintMode.Blocks && typeIdx >= 0 && _cellSize >= 11
                        && _painter.palette != null && typeIdx < _painter.palette.Length)
                    {
                        string bname = _painter.palette[typeIdx]?.name;
                        int variantIdx = _painter.GetBlockVariant(pos);

                        if (!string.IsNullOrEmpty(bname))
                        {
                            int chars = _cellSize >= 20 ? 3 : (_cellSize >= 15 ? 2 : 1);
                            _cellLabelStyle.fontSize = Mathf.Max(7, (int)(_cellSize * 0.38f));
                            GUI.color = new Color(1, 1, 1, 0.85f);

                            // Show variant index if one is selected
                            string label = variantIdx >= 0 ? $"{bname}[{variantIdx}]" : bname;
                            GUI.Label(cell, label.Substring(0, Mathf.Min(chars, label.Length)),
                                _cellLabelStyle);
                            GUI.color = Color.white;
                        }
                    }
                }
            }

            // ── Box-fill preview ──────────────────────────────────────────────
            if (_isBoxDragging && _boxPaintMode)
            {
                var boxEnd = evt.shift ? ConstrainToSquare(_boxCurrent) : _boxCurrent;
                int bMinX = Mathf.Min(_boxStart.x, boxEnd.x);
                int bMaxX = Mathf.Max(_boxStart.x, boxEnd.x);
                int bMinZ = Mathf.Min(_boxStart.y, boxEnd.y);
                int bMaxZ = Mathf.Max(_boxStart.y, boxEnd.y);

                Color fill;
                if (_strokeIsErase)
                    fill = new Color(1f, 0.3f, 0.3f, 0.55f);
                else if (_paintMode == PaintMode.Foliage)
                    fill = new Color(FoliageColor.r, FoliageColor.g, FoliageColor.b, 0.55f);
                else if (_paintMode == PaintMode.Water)
                    fill = new Color(WaterColor.r, WaterColor.g, WaterColor.b, 0.55f);
                else if (_paintMode == PaintMode.Props)
                {
                    Color pc = GetPropColor(_selectedProp);
                    fill = new Color(pc.r, pc.g, pc.b, 0.55f);
                }
                else
                {
                    fill   = GetTypeColor(_selectedType);
                    fill.a = 0.55f;
                }

                // Only draw the fill for cells inside the visible viewport —
                // a full-grid box drag would otherwise draw millions of rects
                for (int bz = Mathf.Max(bMinZ, minZ); bz <= Mathf.Min(bMaxZ, maxZ - 1); bz++)
                    for (int bx = Mathf.Max(bMinX, minX); bx <= Mathf.Min(bMaxX, maxX - 1); bx++)
                        if (bx >= 0 && bx < _gridWidth && bz >= 0 && bz < _gridDepth)
                            EditorGUI.DrawRect(CellRect(grid, bx, bz), fill);

                // White border around the selection rectangle
                Rect sel = new Rect(
                    grid.x + bMinX * _cellSize,
                    grid.y + (_gridDepth - 1 - bMaxZ) * _cellSize,
                    (bMaxX - bMinX + 1) * _cellSize,
                    (bMaxZ - bMinZ + 1) * _cellSize);
                EditorGUI.DrawRect(new Rect(sel.x,    sel.y,    sel.width, 1),          Color.white);
                EditorGUI.DrawRect(new Rect(sel.x,    sel.yMax, sel.width, 1),          Color.white);
                EditorGUI.DrawRect(new Rect(sel.x,    sel.y,    1,         sel.height), Color.white);
                EditorGUI.DrawRect(new Rect(sel.xMax, sel.y,    1,         sel.height), Color.white);

                // Size label centered in the selection rectangle
                int sizeX = bMaxX - bMinX + 1;
                int sizeZ = bMaxZ - bMinZ + 1;
                string sizeLabel = $"{sizeX} x {sizeZ}";
                var sizeLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize  = 12
                };
                sizeLabelStyle.normal.textColor = Color.white;
                var labelSize = sizeLabelStyle.CalcSize(new GUIContent(sizeLabel));
                var bgRect = new Rect(
                    sel.center.x - labelSize.x * 0.5f - 4,
                    sel.center.y - labelSize.y * 0.5f - 1,
                    labelSize.x + 8,
                    labelSize.y + 2);
                EditorGUI.DrawRect(bgRect, new Color(0, 0, 0, 0.6f));
                GUI.Label(bgRect, sizeLabel, sizeLabelStyle);
            }

            // Layer indicator overlay (top-left of grid)
            var layerLabelRect = new Rect(grid.x + 4, grid.y + 2, 80, 18);
            EditorGUI.DrawRect(layerLabelRect, new Color(0, 0, 0, 0.55f));
            GUI.color = new Color(0.35f, 0.82f, 1f);
            GUI.Label(layerLabelRect, $"  Layer Y = {_currentLayer}", EditorStyles.miniLabel);
            GUI.color = Color.white;

            // Hovered cell coordinate tooltip
            if (_mouseInGrid)
            {
                var coordStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    normal    = { textColor = Color.white }
                };
                string coordText = $" X:{_hoveredCell.x + _originX}  Z:{_hoveredCell.y + _originZ} ";
                Vector2 sz = coordStyle.CalcSize(new GUIContent(coordText));
                float lx = _hoveredMousePos.x + 14f;
                float ly = _hoveredMousePos.y + 6f;
                if (lx + sz.x > grid.xMax) lx = _hoveredMousePos.x - sz.x - 6f;
                if (ly + sz.y > grid.yMax) ly = _hoveredMousePos.y - sz.y - 6f;
                var bg = new Rect(lx, ly, sz.x, sz.y);
                EditorGUI.DrawRect(bg, new Color(0f, 0f, 0f, 0.72f));
                GUI.Label(bg, coordText, coordStyle);
            }
        }

        if (_boxPaintMode)
        {
            // ── Box-fill: drag defines a rectangle; release fills it ──────────
            if (evt.type == EventType.MouseDown && (evt.button == 0 || evt.button == 1)
                && grid.Contains(evt.mousePosition))
            {
                GUIUtility.hotControl = paintID;
                _strokeIsErase = evt.button == 1 || _eraseMode;
                _isBoxDragging = true;
                _boxStart = _boxCurrent = GridCellAt(grid, evt.mousePosition);
                Repaint();
                evt.Use();
            }

            if (evt.type == EventType.MouseDrag && GUIUtility.hotControl == paintID)
            {
                _boxCurrent = GridCellAt(grid, evt.mousePosition);
                Repaint();
                evt.Use();
            }

            if (evt.type == EventType.MouseUp && GUIUtility.hotControl == paintID)
            {
                // Capture final mouse position — MouseDrag may not fire at the
                // very edge when the cursor moves fast, leaving _boxCurrent stale.
                _boxCurrent = GridCellAt(grid, evt.mousePosition);

                GUIUtility.hotControl = 0;
                _isBoxDragging = false;

                var boxEnd = evt.shift ? ConstrainToSquare(_boxCurrent) : _boxCurrent;
                int minX = Mathf.Min(_boxStart.x, boxEnd.x);
                int maxX = Mathf.Max(_boxStart.x, boxEnd.x);
                int minZ = Mathf.Min(_boxStart.y, boxEnd.y);
                int maxZ = Mathf.Max(_boxStart.y, boxEnd.y);

                string undoLabel = _strokeIsErase
                    ? $"Erase {_paintMode} (Box)"
                    : $"Place {_paintMode} (Box)";
                WorldPainterSceneTool.SnapshotUndo(_painter, undoLabel);

                for (int bz = minZ; bz <= maxZ; bz++)
                    for (int bx = minX; bx <= maxX; bx++)
                        PaintCell(bx, bz, _strokeIsErase);

                EditorUtility.SetDirty(_painter);
                Repaint();
                evt.Use();
            }
        }
        else
        {
            // ── Draw mode: paint cells as the mouse moves ─────────────────────
            if (evt.type == EventType.MouseDown && (evt.button == 0 || evt.button == 1)
                && grid.Contains(evt.mousePosition))
            {
                GUIUtility.hotControl = paintID;
                _strokeIsErase = evt.button == 1 || _eraseMode;
                string undoLabel = _strokeIsErase
                    ? $"Erase {_paintMode}"
                    : $"Place {_paintMode}";
                // RegisterCompleteObjectUndo: one snapshot, no full-list diffing —
                // RecordObject stalls for seconds on maps with 100k+ blocks.
                WorldPainterSceneTool.SnapshotUndo(_painter, undoLabel);
                TryPaint(grid, evt.mousePosition, _strokeIsErase);
                evt.Use();
            }

            if (evt.type == EventType.MouseDrag && GUIUtility.hotControl == paintID)
            {
                TryPaint(grid, evt.mousePosition, _strokeIsErase);
                evt.Use();
            }

            if (evt.type == EventType.MouseUp && GUIUtility.hotControl == paintID)
            {
                GUIUtility.hotControl = 0;
                evt.Use();
            }
        }

        EditorGUILayout.EndScrollView();

        if (evt.type == EventType.Repaint)
            _gridViewRect = GUILayoutUtility.GetLastRect();
    }

    // Returns the grid cell (x, z) clamped to valid bounds — used for box selection.
    // When shift is held, constrain box to a square by matching both axes to the larger delta.
    private Vector2Int ConstrainToSquare(Vector2Int current)
    {
        int dx = current.x - _boxStart.x;
        int dy = current.y - _boxStart.y;
        int side = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
        return new Vector2Int(
            _boxStart.x + side * (dx >= 0 ? 1 : -1),
            _boxStart.y + side * (dy >= 0 ? 1 : -1));
    }

    private Vector2Int GridCellAt(Rect grid, Vector2 mousePos) => new Vector2Int(
        Mathf.Clamp(Mathf.FloorToInt((mousePos.x - grid.x) / _cellSize), 0, _gridWidth  - 1),
        Mathf.Clamp(_gridDepth - 1 - Mathf.FloorToInt((mousePos.y - grid.y) / _cellSize), 0, _gridDepth - 1));

    // Paints or erases a single cell — behaviour depends on current paint mode.
    private void PaintCell(int x, int z, bool erase)
    {
        if (x < 0 || x >= _gridWidth || z < 0 || z >= _gridDepth) return;
        var pos = new Vector3Int(x + _originX, _currentLayer, z + _originZ);

        switch (_paintMode)
        {
            case PaintMode.Foliage:
                if (erase) _painter.RemoveFoliageZone(pos);
                else       _painter.PlaceFoliageZone(pos, _foliagePaintMask);
                break;
            case PaintMode.Water:
                if (erase) _painter.RemoveWaterfallZone(pos);
                else       _painter.PlaceWaterfallZone(pos);
                break;
            case PaintMode.Props:
                if (erase) _painter.RemoveProp(pos);
                else if (_painter.propPalette != null && _painter.propPalette.Length > 0)
                    _painter.PlaceProp(pos, Mathf.Clamp(_selectedProp, 0, _painter.propPalette.Length - 1));
                break;
            default:
                int yMin = _fillDown ? 0 : _currentLayer;
                if (erase)
                {
                    for (int y = _currentLayer; y >= yMin; y--)
                        _painter.RemoveBlock(new Vector3Int(pos.x, y, pos.z));
                }
                else
                {
                    int ti = Mathf.Clamp(_selectedType, 0, (_painter.palette?.Length ?? 1) - 1);
                    for (int y = _currentLayer; y >= yMin; y--)
                        _painter.PlaceBlock(new Vector3Int(pos.x, y, pos.z), ti, _selectedVariant);
                }
                break;
        }
    }

    // Paints or erases the cell under the mouse. Rejects out-of-bounds positions.
    private void TryPaint(Rect grid, Vector2 mousePos, bool erase)
    {
        int x = Mathf.FloorToInt((mousePos.x - grid.x) / _cellSize);
        int z = _gridDepth - 1 - Mathf.FloorToInt((mousePos.y - grid.y) / _cellSize);
        if (x < 0 || x >= _gridWidth || z < 0 || z >= _gridDepth) return;
        PaintCell(x, z, erase);
        EditorUtility.SetDirty(_painter);
        Repaint();
    }

    // ---- Prop palette (props mode) ----

    private void DrawPropPalette()
    {
        if (_painter.propPalette == null || _painter.propPalette.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "Add prop types (marker/decoration prefabs) to the WorldPainter's Prop Palette " +
                "in the Inspector. Props are painted ON TOP of blocks — they never replace the " +
                "ground and are placed standing on the floor of their cell at bake. Paint them " +
                "one layer ABOVE the ground.", MessageType.Info);
            return;
        }

        EditorGUILayout.BeginHorizontal();
        for (int i = 0; i < _painter.propPalette.Length; i++)
        {
            bool selected = i == _selectedProp && !_eraseMode;
            GUI.backgroundColor = selected ? new Color(0.35f, 0.82f, 1f) : Color.white;
            var pt = _painter.propPalette[i];
            string label = string.IsNullOrEmpty(pt?.name) ? $"P{i}" : pt.name;
            if (GUILayout.Toggle(selected, label, "Button", GUILayout.Height(26)) && !selected)
            {
                _selectedProp = i;
                _eraseMode = false;
            }
            // Color strip along the bottom of the button — matches the grid overlay
            // color, so the palette and the painted cells read as the same thing.
            Rect buttonRect = GUILayoutUtility.GetLastRect();
            EditorGUI.DrawRect(new Rect(
                buttonRect.x + 3f, buttonRect.yMax - 6f,
                buttonRect.width - 6f, 4f), GetPropColor(i));
        }
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(
            "Props layer: paint at the layer ABOVE the ground. Never merges, never replaces blocks.",
            EditorStyles.miniLabel);
    }

    // ---- Palette (block mode) ----

    private void DrawPalette()
    {
        if (_painter.palette == null || _painter.palette.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "Add block types to the WorldPainter palette in the Inspector.", MessageType.Warning);
            return;
        }

        EditorGUILayout.BeginHorizontal();

        var paletteLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            { alignment = TextAnchor.MiddleCenter };

        for (int i = 0; i < _painter.palette.Length; i++)
        {
            bool selected = i == _selectedType && !_eraseMode;
            GUI.backgroundColor = selected ? new Color(0.35f, 0.82f, 1f) : GetTypeColor(i);

            var bt = _painter.palette[i];
            string label = bt?.name ?? $"T{i}";

            Texture2D preview = bt?.prefab != null
                ? AssetPreview.GetAssetPreview(bt.prefab)
                : null;

            if (preview == null && bt?.prefab != null
                && AssetPreview.IsLoadingAssetPreview(bt.prefab.GetInstanceID()))
                Repaint();

            const float thumbSize = 26f;
            const float labelH    = 14f;
            const float pad       = 2f;
            float btnH = preview != null ? thumbSize + labelH + pad * 3 : 22f;

            Rect btn = GUILayoutUtility.GetRect(50, btnH, GUILayout.ExpandWidth(false));
            if (GUI.Button(btn, GUIContent.none))
            {
                _selectedType = i;
                _eraseMode    = false;
                _selectedVariant = -1;
            }

            if (Event.current.type == EventType.Repaint)
            {
                if (preview != null)
                {
                    Rect thumb = new Rect(
                        btn.x + (btn.width - thumbSize) * 0.5f,
                        btn.y + pad, thumbSize, thumbSize);
                    GUI.DrawTexture(thumb, preview, ScaleMode.ScaleToFit);
                }

                Rect labelRect = preview != null
                    ? new Rect(btn.x, btn.yMax - labelH - pad, btn.width, labelH)
                    : btn;
                GUI.Label(labelRect, label, paletteLabelStyle);
            }
        }

        GUI.backgroundColor = _eraseMode ? new Color(1f, 0.35f, 0.35f) : Color.white;
        if (GUILayout.Button("Erase", GUILayout.Height(26), GUILayout.Width(52)))
            _eraseMode = !_eraseMode;

        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        // Show variants for selected block type
        if (_selectedType >= 0 && _selectedType < _painter.palette.Length && !_eraseMode)
        {
            var bt = _painter.palette[_selectedType];
            if (bt?.variants != null && bt.variants.Length > 0)
            {
                GUILayout.Space(4);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Tile Variants:", EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();

                GUI.backgroundColor = _selectedVariant == -1 ? new Color(0.35f, 0.82f, 1f) : Color.white;
                if (GUILayout.Button("Default", GUILayout.Height(22)))
                    _selectedVariant = -1;

                for (int v = 0; v < bt.variants.Length; v++)
                {
                    GUI.backgroundColor = _selectedVariant == v ? new Color(0.35f, 0.82f, 1f) : Color.white;
                    string variantLabel = bt.variants[v]?.prefab != null ? bt.variants[v].prefab.name : $"V{v}";
                    if (GUILayout.Button(variantLabel, GUILayout.Height(22)))
                        _selectedVariant = v;
                }

                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
        }
    }

    // ---- Foliage settings panel (foliage mode) ----

    private void DrawFoliageSettings()
    {
        // Rebuild SerializedObject if the painter changed
        if (_serializedPainter == null || _serializedPainter.targetObject != _painter)
            _serializedPainter = new SerializedObject(_painter);

        _serializedPainter.Update();

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Foliage Zone Settings", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        GUI.color = new Color(1f, 0.7f, 0.7f);
        if (GUILayout.Button("Clear Zones", GUILayout.Width(80), GUILayout.Height(18)))
        {
            if (EditorUtility.DisplayDialog("Clear Foliage Zones",
                    "Remove all painted foliage zones?", "Clear", "Cancel"))
            {
                Undo.RecordObject(_painter, "Clear Foliage Zones");
                _painter.ClearFoliageZones();
                EditorUtility.SetDirty(_painter);
            }
        }
        GUI.color = Color.white;
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(2);

        // ── Foliage type palette (toggle — multiple types can be active) ─
        var types = _painter.foliageSettings?.foliageTypes;
        if (types != null && types.Length > 0)
        {
            EditorGUILayout.LabelField("Paint types (click to toggle — multi-select supported):",
                EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < types.Length; i++)
            {
                int  bit      = 1 << i;
                bool active   = (_foliagePaintMask & bit) != 0;
                GUI.backgroundColor = active
                    ? GetFoliageTypeColor(i) * 1.5f
                    : new Color(0.3f, 0.3f, 0.3f);
                string label = types[i]?.name ?? $"Type {i}";
                if (GUILayout.Button(label, GUILayout.Height(24)))
                {
                    int next = _foliagePaintMask ^ bit;   // toggle
                    if (next != 0) _foliagePaintMask = next;   // prevent all-off
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // Show what the current selection will paint
            int typeCount = 0;
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < types.Length; i++)
                if ((_foliagePaintMask & (1 << i)) != 0)
                {
                    if (typeCount > 0) names.Append(", ");
                    names.Append(types[i]?.name ?? $"Type {i}");
                    typeCount++;
                }
            GUI.color = new Color(0.7f, 0.95f, 0.7f);
            EditorGUILayout.LabelField($"Painting: {names}", EditorStyles.miniLabel);
            GUI.color = Color.white;
            GUILayout.Space(2);
        }

        var settingsProp = _serializedPainter.FindProperty("foliageSettings");
        if (settingsProp != null)
            EditorGUILayout.PropertyField(settingsProp, new GUIContent("Settings"), true);

        _serializedPainter.ApplyModifiedProperties();

        // Validate GPU instancing — DrawMeshInstanced requires it on every material
        if (_painter.foliageSettings?.foliageTypes != null)
        {
            foreach (var ft in _painter.foliageSettings.foliageTypes)
            {
                if (ft?.materials == null) continue;
                foreach (var mat in ft.materials)
                {
                    if (mat != null && !mat.enableInstancing)
                    {
                        EditorGUILayout.HelpBox(
                            $"Material '{mat.name}' does not have GPU Instancing enabled. " +
                            "Select the material and tick 'Enable GPU Instancing' — foliage will not " +
                            "render without it.",
                            MessageType.Error);
                    }
                }
            }
        }

        GUILayout.Space(2);
        GUI.color = new Color(0.7f, 0.95f, 0.7f);
        EditorGUILayout.LabelField($"{_painter.FoliageZoneCount} zone(s) painted on Y = {_currentLayer} layer",
            EditorStyles.miniLabel);
        GUI.color = Color.white;

        EditorGUILayout.EndVertical();
    }

    // ---- Waterfall settings panel (water mode) ----

    private void DrawWaterfallSettings()
    {
        if (_serializedPainter == null || _serializedPainter.targetObject != _painter)
            _serializedPainter = new SerializedObject(_painter);

        _serializedPainter.Update();

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Waterfall Zone Settings", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        GUI.color = new Color(1f, 0.7f, 0.7f);
        if (GUILayout.Button("Clear Zones", GUILayout.Width(80), GUILayout.Height(18)))
        {
            if (EditorUtility.DisplayDialog("Clear Waterfall Zones",
                    "Remove all painted waterfall zones?", "Clear", "Cancel"))
            {
                Undo.RecordObject(_painter, "Clear Waterfall Zones");
                _painter.ClearWaterfallZones();
                EditorUtility.SetDirty(_painter);
            }
        }
        GUI.color = Color.white;
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(2);

        var settingsProp = _serializedPainter.FindProperty("waterfallSettings");
        if (settingsProp != null)
            EditorGUILayout.PropertyField(settingsProp, new GUIContent("Settings"), true);

        _serializedPainter.ApplyModifiedProperties();

        GUILayout.Space(2);
        GUI.color = new Color(0.6f, 0.8f, 1f);
        EditorGUILayout.LabelField($"{_painter.WaterfallZoneCount} waterfall zone(s) painted",
            EditorStyles.miniLabel);
        GUI.color = Color.white;

        EditorGUILayout.EndVertical();
    }

    // ---- Footer: grid size, zoom, create, clear ----

    private void DrawFooter()
    {
        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField("W:", GUILayout.Width(18));
        _gridWidth = Mathf.Max(1, EditorGUILayout.IntField(_gridWidth, GUILayout.Width(36)));
        EditorGUILayout.LabelField("D:", GUILayout.Width(18));
        _gridDepth = Mathf.Max(1, EditorGUILayout.IntField(_gridDepth, GUILayout.Width(36)));
        EditorGUILayout.LabelField(new GUIContent("X0:", "World X of the grid's left edge (can be negative)"), GUILayout.Width(24));
        _originX = EditorGUILayout.IntField(_originX, GUILayout.Width(40));
        EditorGUILayout.LabelField(new GUIContent("Z0:", "World Z of the grid's bottom edge (can be negative)"), GUILayout.Width(24));
        _originZ = EditorGUILayout.IntField(_originZ, GUILayout.Width(40));
        EditorGUILayout.LabelField("Zoom:", GUILayout.Width(38));
        _cellSize = EditorGUILayout.Slider(_cellSize, 4f, 52f, GUILayout.Width(120));

        GUILayout.FlexibleSpace();

        EditorGUILayout.LabelField($"{_painter.BlockCount} blocks", GUILayout.Width(68));

        GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
        if (GUILayout.Button("Save", GUILayout.Height(26), GUILayout.Width(48)))
            _painter.SaveToFile();
        if (GUILayout.Button("Load", GUILayout.Height(26), GUILayout.Width(48)))
        {
            if (EditorUtility.DisplayDialog("Load WorldPainter Data",
                    "Replace all current data with saved file?", "Load", "Cancel"))
            {
                Undo.RecordObject(_painter, "Load WorldPainter Data");
                _painter.LoadFromFile();
                EditorUtility.SetDirty(_painter);
            }
        }

        GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
        if (GUILayout.Button("Create & Bake", GUILayout.Height(26), GUILayout.Width(110)))
            CreateAndBake();

        GUI.backgroundColor = new Color(0.45f, 0.85f, 0.55f);
        if (GUILayout.Button("Generate & Bake Foliage", GUILayout.Height(26), GUILayout.Width(170)))
            DoGenerateAndBakeFoliage();

        GUI.backgroundColor = new Color(0.35f, 0.55f, 0.95f);
        if (GUILayout.Button("Generate Water", GUILayout.Height(26), GUILayout.Width(110)))
            DoGenerateWaterfall();

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear All", GUILayout.Height(26), GUILayout.Width(62)))
        {
            if (EditorUtility.DisplayDialog("Clear Everything",
                    "Remove all placed blocks, foliage zones, and waterfall zones?", "Clear", "Cancel"))
            {
                Undo.RecordObject(_painter, "Clear WorldPainter");
                _painter.Clear();
                _painter.ClearFoliageZones();
                _painter.ClearWaterfallZones();
                EditorUtility.SetDirty(_painter);
            }
        }

        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();
    }

    // ---- Helpers ----

    private Rect CellRect(Rect grid, int x, int z)
    {
        return new Rect(
            grid.x + x * _cellSize,
            grid.y + (_gridDepth - 1 - z) * _cellSize,
            _cellSize - 1,
            _cellSize - 1);
    }

    // Copies all blocks from fromLayer into toLayer within the grid bounds.
    private void CopyLayer(int fromLayer, int toLayer)
    {
        if (_painter == null) return;
        WorldPainterSceneTool.SnapshotUndo(_painter, $"Copy Layer {fromLayer} → {toLayer}");
        for (int z = 0; z < _gridDepth; z++)
            for (int x = 0; x < _gridWidth; x++)
            {
                int ti = _painter.GetBlockType(new Vector3Int(x + _originX, fromLayer, z + _originZ));
                if (ti >= 0)
                    _painter.PlaceBlock(new Vector3Int(x + _originX, toLayer, z + _originZ), ti);
            }
        EditorUtility.SetDirty(_painter);
        Repaint();
    }

    // Type colors are cached — the uncached path does GetComponentInChildren on
    // the prefab, which is far too slow to run per visible cell per repaint.
    private Color[] _typeColorCache;
    private int     _typeColorCacheVersion = -1;

    private Color GetTypeColor(int idx)
    {
        var palette = _painter?.palette;
        if (palette == null || idx < 0 || idx >= palette.Length)
            return Color.gray;

        if (_typeColorCache == null || _typeColorCache.Length != palette.Length
            || _typeColorCacheVersion != _painter.EditVersion)
        {
            _typeColorCache = new Color[palette.Length];
            for (int i = 0; i < palette.Length; i++)
                _typeColorCache[i] = ComputeTypeColor(i);
            _typeColorCacheVersion = _painter.EditVersion;
        }
        return _typeColorCache[idx];
    }

    private Color ComputeTypeColor(int idx)
    {
        var bt = _painter.palette[idx];

        // 1. Explicit tile color set by the user
        if (bt != null && bt.tileColor.a > 0.01f)
            return bt.tileColor;

        // 2. Auto-detect from the prefab's material
        if (bt?.prefab != null)
        {
            var mr = bt.prefab.GetComponentInChildren<MeshRenderer>();
            var mat = mr?.sharedMaterial;
            if (mat != null)
            {
                if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
                if (mat.HasProperty("_Color"))     return mat.GetColor("_Color");
            }
        }

        // 3. Fallback palette colour
        return FallbackColors[idx % FallbackColors.Length];
    }

    private void CreateNewPainter()
    {
        var go = new GameObject("WorldPainter");
        SetPainter(go.AddComponent<WorldPainter>());
        Selection.activeGameObject = go;
        Undo.RegisterCreatedObjectUndo(go, "Create WorldPainter");
        Repaint();
    }

    private void CreateAndBake()
    {
        if (_painter == null) return;

        if (_painter.BlockCount == 0)
        {
            EditorUtility.DisplayDialog("Nothing to bake", "Place some blocks first.", "OK");
            return;
        }

        var painter = _painter;
        EditorApplication.delayCall += () =>
        {
            if (painter == null) return;

            painter.Bake();

            const string folder = "Assets/Prefabs";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "Prefabs");

            string path = folder + $"/{painter.BakePrefix}_Map.prefab";
            PrefabUtility.SaveAsPrefabAssetAndConnect(
                painter.gameObject, path, InteractionMode.UserAction, out bool ok);

            if (ok)
            {
                AssetDatabase.Refresh();
                Debug.Log($"[WorldPainter] Prefab saved: {path}");
            }

            // Keep the game server's collision data in sync with the painted map
            // (writes ArenaMap.Generated.cs; remember to rebuild the server).
            ArenaServerExport.Export(painter);

            EditorUtility.SetDirty(painter);
        };
    }

    private void DoGenerateAndBakeFoliage()
    {
        if (_painter == null) return;

        if (_painter.FoliageZoneCount == 0)
        {
            EditorUtility.DisplayDialog("No Foliage Zones",
                "Switch to Foliage mode and paint zones on the grid first.", "OK");
            return;
        }

        var painter = _painter;
        EditorApplication.delayCall += () =>
        {
            if (painter == null) return;
            painter.GenerateFoliage();

            // Mark every generated FoliageArea child dirty so Unity serializes
            // the savedInstances data before the user enters play mode.
            int totalInstances = 0;
            foreach (Transform child in painter.transform)
            {
                if (!child.name.StartsWith("FoliageArea_")) continue;
                var fa = child.GetComponent<FoliageArea>();
                if (fa == null) continue;

                int count = fa.TotalInstanceCount;
                totalInstances += count;

                if (count == 0)
                    Debug.LogWarning(
                        $"[WorldPainter] {child.name}: 0 instances " +
                        $"(world pos {child.position}). " +
                        "See console for specific FoliageArea warnings. Common causes: " +
                        "(1) Materials array not assigned in FoliageType (re-assign after recent field rename), " +
                        "(2) No baked MeshCollider — run Create & Bake first, " +
                        "(3) Surface Layers mask excludes the baked mesh's layer.");
                else
                    Debug.Log($"[WorldPainter] {child.name}: {count} instances generated.");

                EditorUtility.SetDirty(fa);
            }

            if (totalInstances > 0)
                Debug.Log($"[WorldPainter] Foliage generation complete — {totalInstances} total instances.");
            else
                Debug.LogWarning("[WorldPainter] Foliage generation produced 0 instances across all zones.");

            painter.BakeFoliage();

            EditorUtility.SetDirty(painter);
            if (!Application.isPlaying)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    painter.gameObject.scene);
        };
    }

    private void DoGenerateWaterfall()
    {
        if (_painter == null) return;

        if (_painter.WaterfallZoneCount == 0)
        {
            EditorUtility.DisplayDialog("No Waterfall Zones",
                "Switch to Water mode and paint zones on the grid first.", "OK");
            return;
        }

        var painter = _painter;
        EditorApplication.delayCall += () =>
        {
            if (painter == null) return;
            painter.GenerateWaterfall();

            EditorUtility.SetDirty(painter);
            if (!Application.isPlaying)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    painter.gameObject.scene);
        };
    }

}

using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.EditorTools;

/// <summary>
/// Scene-view painting tool for WorldPainter — like the 2D Tile Palette brush,
/// but for the 3D block grid. Select a WorldPainter, then activate this tool from
/// the scene-view toolbar or the "Edit in Scene View" button on the inspector.
///
///   LMB           paint (Ctrl+LMB = erase, or toggle Erase in the panel)
///   Ctrl+Scroll   change layer      [ / ]   change brush size
///   Box mode      drag a rectangle, release to fill (hold Shift = square)
///   RMB + WASD    fly (always available)     Escape   exit the tool
///
/// The placed-but-unbaked blocks are shown as ONE ordinary mesh of colored,
/// face-culled cubes on a hidden GameObject — rebuilt (throttled) only when the
/// data changes. Nothing is submitted per frame, so the preview costs almost
/// nothing to render no matter how big the map is. Baking is unchanged.
/// </summary>
[EditorTool("World Painter Brush", typeof(WorldPainter))]
public class WorldPainterSceneTool : EditorTool
{
    private enum PaintMode { Blocks, Foliage, Water }

    // ---- Editing state (static so it survives selection changes and tool re-creation) ----

    private static PaintMode s_mode            = PaintMode.Blocks;
    private static int       s_layer;
    private static int       s_selectedType;
    private static int       s_selectedVariant = -1;
    private static bool      s_eraseMode;
    private static bool      s_boxMode;
    private static int       s_foliageMask     = 1;
    private static int       s_brushSize       = 1;
    private static bool      s_fillDown;
    private static bool      s_showOtherLayers;          // off: only the selected layer renders
    private static bool      s_ghostBelow      = true;   // transparent onion-skin of layer − 1
    private static bool      s_ghostAbove;               // transparent onion-skin of layer + 1
    private static bool      s_showBlocks      = true;
    private static bool      s_hideBaked       = true;   // hide the baked map while editing

    // ---- Stroke state ----

    private bool       _strokeActive;
    private bool       _strokeErase;
    private int        _undoGroup;
    private Vector3Int _lastPaintedCell = new(int.MinValue, int.MinValue, int.MinValue);

    private bool       _boxDragging;
    private Vector2Int _boxStart, _boxEnd;   // (x, z) at the current layer

    // ---- Hover ----

    private bool       _hasHover;
    private Vector3Int _hoverCell;

    // ---- Preview mesh state ----

    private const string PreviewRootName = "__WorldPainterPreview";
    private const int    ChunkShift      = 6;   // 64-cell preview chunks

    private GameObject         _previewRoot;
    private readonly List<Mesh> _previewMeshes = new();

    private Material _vcOpaqueMat;    // vertex-color, opaque (block cubes)
    private Material _vcClearMat;     // vertex-color, transparent (zone overlays)
    private Material _tintClearMat;   // flat tint, transparent (hover ghost)

    private WorldPainter _cachedPainter;
    private int          _cachedVersion = -1;
    private int          _cachedLayer   = -1;
    private PaintMode    _cachedMode    = (PaintMode)(-1);
    private bool         _cachedShowOther;
    private bool         _cachedGhostBelow;
    private bool         _cachedGhostAbove;
    private double       _lastRebuildTime;

    private Rect _panelRect = new(10, 10, 258, 0);

    private static readonly Color SelectedBlue = new(0.35f, 0.82f, 1f);
    private static readonly Color FoliageColor = new(0.15f, 0.75f, 0.25f);
    private static readonly Color WaterColor   = new(0.10f, 0.85f, 1f);   // bright cyan — reads against a blue sky
    private static readonly Color EraseColor   = new(1f,    0.30f, 0.30f);

    private static readonly Color[] FallbackColors =
    {
        new(0.30f, 0.68f, 0.30f),   // green
        new(0.58f, 0.42f, 0.18f),   // brown
        new(0.55f, 0.55f, 0.55f),   // grey
        new(0.25f, 0.45f, 0.75f),   // blue
        new(0.75f, 0.60f, 0.20f),   // gold
        new(0.60f, 0.25f, 0.25f),   // red
    };

    public override GUIContent toolbarIcon =>
        EditorGUIUtility.IconContent("Grid.PaintTool", "|World Painter Brush");

    // ---- Lifecycle ----

    public override void OnActivated()
    {
        DestroyStalePreviewRoots();
        _cachedVersion = -1;
        SceneView.RepaintAll();
    }

    public override void OnWillBeDeactivated()
    {
        if (target is WorldPainter p) UpdateBakedVisibility(p, hide: false);
        DestroyPreview();
        SceneView.RepaintAll();
    }

    // WorldPainter children that visually duplicate the editing preview. Hidden
    // scene-view-only (SceneVisibilityManager) while editing — the scene data
    // and the game view are untouched.
    private static bool IsBakedVisual(string childName) =>
        childName == "Baked"
        || childName == "BakedInstances"
        || childName.StartsWith("BakedWaterfall_");

    private static void UpdateBakedVisibility(WorldPainter p, bool hide)
    {
        if (p == null) return;
        var svm = UnityEditor.SceneVisibilityManager.instance;
        for (int i = 0; i < p.transform.childCount; i++)
        {
            var child = p.transform.GetChild(i);
            if (!IsBakedVisual(child.name)) continue;
            bool hidden = svm.IsHidden(child.gameObject);
            if (hide && !hidden)      svm.Hide(child.gameObject, true);
            else if (!hide && hidden) svm.Show(child.gameObject, true);
        }
    }

    // ---- Main GUI loop ----

    public override void OnToolGUI(EditorWindow window)
    {
        var sv      = window as SceneView;
        var painter = target as WorldPainter;
        if (sv == null || painter == null)
        {
            DestroyPreview();
            return;
        }

        var evt = Event.current;

        // Any click outside the panel drops keyboard focus — a focused number
        // field would otherwise eat the WASD flythrough keys during RMB fly.
        if (evt.type == EventType.MouseDown && !_panelRect.Contains(evt.mousePosition))
        {
            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
        }

        // Unity quirk: releasing RMB while WASD is still held leaves the fly
        // velocity applied and the camera glides on. Kill the residual motion
        // when RMB comes up, or when a fly key is released after flying ended.
        if ((evt.type == EventType.MouseUp && evt.button == 1) ||
            (evt.type == EventType.KeyUp && !Tools.viewToolActive && IsFlyKey(evt.keyCode)))
            StopFlyGlide(sv);

        // Steal default scene clicks so painting never deselects/box-selects — but
        // stay hands-off while a view tool (RMB fly, MMB pan, Alt orbit) is engaged.
        _paintControlId = GUIUtility.GetControlID(FocusType.Passive);
        if (!Tools.viewToolActive)
            HandleUtility.AddDefaultControl(_paintControlId);

        if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
        {
            ToolManager.RestorePreviousTool();
            evt.Use();
            return;
        }

        // Ctrl+Scroll changes the active layer.
        if (evt.type == EventType.ScrollWheel && evt.control)
        {
            s_layer = Mathf.Max(0, s_layer - (int)Mathf.Sign(evt.delta.y));
            evt.Use();
            SceneView.RepaintAll();
        }

        // [ and ] adjust the brush size.
        if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.LeftBracket)
        {
            s_brushSize = Mathf.Max(1, s_brushSize - 1);
            evt.Use();
            SceneView.RepaintAll();
        }
        else if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.RightBracket)
        {
            s_brushSize = Mathf.Min(100, s_brushSize + 1);
            evt.Use();
            SceneView.RepaintAll();
        }

        // Tile selection without leaving the brush: . cycles forward, , cycles
        // backward, 1–9 picks a palette slot directly. (Not Tab — the editor
        // uses Tab for field-focus traversal and always wins that fight.)
        // Works mid-stroke — the next painted cells use the new tile.
        if (evt.type == EventType.KeyDown && s_mode == PaintMode.Blocks
            && painter.palette != null && painter.palette.Length > 0)
        {
            int count = painter.palette.Length;
            if (evt.keyCode == KeyCode.Period || evt.keyCode == KeyCode.Comma)
            {
                int step = evt.keyCode == KeyCode.Period ? 1 : count - 1;
                s_selectedType    = (s_selectedType + step) % count;
                s_selectedVariant = -1;
                s_eraseMode       = false;
                evt.Use();
                SceneView.RepaintAll();
            }
            else if (evt.keyCode >= KeyCode.Alpha1 && evt.keyCode <= KeyCode.Alpha9)
            {
                int idx = evt.keyCode - KeyCode.Alpha1;
                if (idx < count)
                {
                    s_selectedType    = idx;
                    s_selectedVariant = -1;
                    s_eraseMode       = false;
                    evt.Use();
                    SceneView.RepaintAll();
                }
            }
        }

        bool overPanel = _panelRect.Contains(evt.mousePosition);

        // If the mouse left the window mid-stroke, the MouseUp never arrives —
        // end the stroke here so the next click starts cleanly.
        if (evt.type == EventType.MouseLeaveWindow)
        {
            if (_strokeActive || _boxDragging) GUIUtility.hotControl = 0;
            _strokeActive = false;
            _boxDragging  = false;
        }

        // Hover cell under the mouse (plane at the current layer). No cursor and no
        // painting while the camera is being flown/panned/orbited.
        _hasHover = !overPanel && !evt.alt && !Tools.viewToolActive
                    && TryGetCell(painter, evt.mousePosition, out _hoverCell);
        // Repaint on drags too — box preview, cursor and the throttled mesh
        // rebuild all depend on the view actually refreshing mid-stroke.
        if (evt.type == EventType.MouseMove || evt.type == EventType.MouseDrag)
            sv.Repaint();

        if (!Tools.viewToolActive)
            HandlePainting(painter, evt, overPanel);

        // Keep the preview mesh in sync (throttled) and glued to the painter.
        // Baked visibility is re-asserted here too, since a re-bake while the
        // brush is active recreates the baked children unhidden.
        if (evt.type == EventType.Layout)
        {
            EnsureResources();
            RebuildPreviewIfNeeded(painter);
            SyncPreviewTransform(painter);
            UpdateBakedVisibility(painter, s_hideBaked);
        }

        _sceneCamera = sv.camera;

        // Each section isolated: one failure must never abort the whole GUI pass
        // (that's how the panel and box preview vanish). Errors go to the Console.
        if (evt.type == EventType.Repaint)
        {
            try { DrawGridAndCursor(painter); }
            catch (System.Exception ex) { Debug.LogException(ex); }
            try { DrawHoverGhost(painter, sv.camera); }
            catch (System.Exception ex) { Debug.LogException(ex); }
        }

        // Keep the view refreshing for as long as a stroke or box drag is live —
        // drag events alone don't guarantee repaints.
        if (_strokeActive || _boxDragging)
            sv.Repaint();

        try { DrawPanel(painter); }
        catch (ExitGUIException) { throw; }
        catch (System.Exception ex) { Debug.LogException(ex); }

        DrawLayerBadge(sv);
    }

    // Big top-center badge so the active layer is never a mystery.
    private static GUIStyle s_badgeStyle;

    private void DrawLayerBadge(SceneView sv)
    {
        s_badgeStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 18,
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = Color.white },
        };

        string text = $"Layer {s_layer}"
            + (s_fillDown ? "  ▼ fill" : "")
            + (s_eraseMode ? "  — ERASING" : "");
        Vector2 size = s_badgeStyle.CalcSize(new GUIContent(text));

        Handles.BeginGUI();
        var rect = new Rect((sv.position.width - size.x - 24f) * 0.5f, 8f, size.x + 24f, 30f);
        EditorGUI.DrawRect(rect, s_eraseMode
            ? new Color(0.5f, 0.05f, 0.05f, 0.75f)
            : new Color(0f, 0f, 0f, 0.65f));
        GUI.Label(rect, text, s_badgeStyle);
        Handles.EndGUI();
    }

    private int _paintControlId;
    private Camera _sceneCamera;

    // ---- Undo ----

    // Above this many blocks a full-object undo snapshot costs tens of MB per
    // stroke; the accumulating stack can run the editor out of memory.
    internal const int UndoBlockLimit = 200_000;

    internal static void SnapshotUndo(WorldPainter p, string label)
    {
        if (p.BlockCount > UndoBlockLimit) return;
        Undo.RegisterCompleteObjectUndo(p, label);
    }

    // ---- Fly-glide stopper ----

    private static bool s_motionReflected;
    private static PropertyInfo s_motionProp;
    private static FieldInfo    s_motionField;
    private static MethodInfo   s_resetMotion;

    private static bool IsFlyKey(KeyCode k) =>
        k == KeyCode.W || k == KeyCode.A || k == KeyCode.S || k == KeyCode.D ||
        k == KeyCode.Q || k == KeyCode.E;

    // SceneViewMotion is internal — reach it via reflection and call ResetMotion()
    // to zero the flythrough velocity. Silently no-ops if the internals ever change.
    private static void StopFlyGlide(SceneView sv)
    {
        try
        {
            if (!s_motionReflected)
            {
                s_motionReflected = true;
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                s_motionProp  = typeof(SceneView).GetProperty("sceneViewMotion", F);
                s_motionField = typeof(SceneView).GetField("m_SceneViewMotion", F);
                var motionType = s_motionProp?.PropertyType ?? s_motionField?.FieldType;
                s_resetMotion = motionType?.GetMethod("ResetMotion", F);
            }

            object motion = s_motionProp != null ? s_motionProp.GetValue(sv) : s_motionField?.GetValue(sv);
            if (motion != null && s_resetMotion != null)
                s_resetMotion.Invoke(motion, null);
        }
        catch
        {
            // Editor internals moved — the glide just keeps Unity's default behavior.
        }
    }

    // ---- Painting ----

    private void HandlePainting(WorldPainter p, Event evt, bool overPanel)
    {
        if (evt.alt) return;   // camera navigation

        if (s_boxMode)
        {
            if (evt.type == EventType.MouseDown && evt.button == 0 && !overPanel && _hasHover)
            {
                GUIUtility.hotControl = _paintControlId;   // guarantees we get the drags
                _boxDragging = true;
                _strokeErase = evt.control || s_eraseMode;
                _boxStart = _boxEnd = new Vector2Int(_hoverCell.x, _hoverCell.z);
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && _boxDragging)
            {
                if (_hasHover) _boxEnd = new Vector2Int(_hoverCell.x, _hoverCell.z);
                evt.Use();
            }
            else if (evt.type == EventType.MouseUp && evt.button == 0 && _boxDragging)
            {
                GUIUtility.hotControl = 0;
                _boxDragging = false;
                if (_hasHover) _boxEnd = new Vector2Int(_hoverCell.x, _hoverCell.z);
                var end = evt.shift ? ConstrainToSquare(_boxStart, _boxEnd) : _boxEnd;

                Undo.SetCurrentGroupName(_strokeErase ? "Erase Box (World Painter)" : "Fill Box (World Painter)");
                int group = Undo.GetCurrentGroup();
                SnapshotUndo(p, "World Painter Box");

                for (int z = Mathf.Min(_boxStart.y, end.y); z <= Mathf.Max(_boxStart.y, end.y); z++)
                    for (int x = Mathf.Min(_boxStart.x, end.x); x <= Mathf.Max(_boxStart.x, end.x); x++)
                        PaintSingleCell(p, x, z);

                Undo.CollapseUndoOperations(group);
                EditorUtility.SetDirty(p);
                _lastRebuildTime = 0;   // sync the preview right away
                evt.Use();
            }
        }
        else
        {
            if (evt.type == EventType.MouseDown && evt.button == 0 && !overPanel && _hasHover)
            {
                GUIUtility.hotControl = _paintControlId;   // guarantees we get the drags
                _strokeActive = true;
                _strokeErase  = evt.control || s_eraseMode;
                Undo.SetCurrentGroupName(_strokeErase ? "Erase (World Painter)" : "Paint (World Painter)");
                _undoGroup = Undo.GetCurrentGroup();
                // One snapshot per stroke — recording per drag event serializes the
                // whole block list every few milliseconds and stalls big maps.
                SnapshotUndo(p, "World Painter Stroke");
                _lastPaintedCell = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
                PaintTo(p, _hoverCell);
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && evt.button == 0 && _strokeActive)
            {
                if (_hasHover) PaintTo(p, _hoverCell);
                evt.Use();
            }
            else if (evt.type == EventType.MouseUp && evt.button == 0 && _strokeActive)
            {
                GUIUtility.hotControl = 0;
                _strokeActive = false;
                Undo.CollapseUndoOperations(_undoGroup);
                _lastRebuildTime = 0;   // sync the preview right away
                evt.Use();
            }
        }
    }

    // Paints along the line from the previously painted cell so fast drags leave
    // no gaps. Undo is snapshotted once at stroke start, not here.
    private void PaintTo(WorldPainter p, Vector3Int cell)
    {
        if (_lastPaintedCell.x == int.MinValue || _lastPaintedCell == cell)
        {
            PaintCell(p, cell.x, cell.z);
        }
        else
        {
            int x0 = _lastPaintedCell.x, z0 = _lastPaintedCell.z;
            int steps = Mathf.Max(Mathf.Abs(cell.x - x0), Mathf.Abs(cell.z - z0));
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                PaintCell(p, Mathf.RoundToInt(Mathf.Lerp(x0, cell.x, t)),
                             Mathf.RoundToInt(Mathf.Lerp(z0, cell.z, t)));
            }
        }

        _lastPaintedCell = cell;
        EditorUtility.SetDirty(p);
    }

    // Applies the brush footprint (s_brushSize × s_brushSize) centered on (x, z).
    private void PaintCell(WorldPainter p, int x, int z)
    {
        int min = -(s_brushSize - 1) / 2;
        int max = s_brushSize / 2;
        for (int dz = min; dz <= max; dz++)
            for (int dx = min; dx <= max; dx++)
                PaintSingleCell(p, x + dx, z + dz);
    }

    private void PaintSingleCell(WorldPainter p, int x, int z)
    {
        var pos = new Vector3Int(x, s_layer, z);

        switch (s_mode)
        {
            case PaintMode.Foliage:
                if (_strokeErase) p.RemoveFoliageZone(pos);
                else              p.PlaceFoliageZone(pos, s_foliageMask);
                break;
            case PaintMode.Water:
            {
                // Fill Down applies to water too — a column of zones from the
                // current layer to 0.
                int wMin = s_fillDown ? 0 : s_layer;
                if (_strokeErase)
                {
                    for (int y = s_layer; y >= wMin; y--)
                        p.RemoveWaterfallZone(new Vector3Int(x, y, z));
                }
                else
                {
                    for (int y = s_layer; y >= wMin; y--)
                        p.PlaceWaterfallZone(new Vector3Int(x, y, z));
                }
                break;
            }
            default:
                // Fill Down: apply to every layer from the current one to 0 so a
                // single pass creates solid ground instead of a floating shell.
                int yMin = s_fillDown ? 0 : s_layer;
                if (_strokeErase)
                {
                    for (int y = s_layer; y >= yMin; y--)
                        p.RemoveBlock(new Vector3Int(x, y, z));
                }
                else
                {
                    int ti = Mathf.Clamp(s_selectedType, 0, (p.palette?.Length ?? 1) - 1);
                    for (int y = s_layer; y >= yMin; y--)
                        p.PlaceBlock(new Vector3Int(x, y, z), ti, s_selectedVariant);
                }
                break;
        }
    }

    private static Vector2Int ConstrainToSquare(Vector2Int start, Vector2Int current)
    {
        int dx = current.x - start.x;
        int dy = current.y - start.y;
        int side = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
        return new Vector2Int(
            start.x + side * (dx >= 0 ? 1 : -1),
            start.y + side * (dy >= 0 ? 1 : -1));
    }

    // Intersects the mouse ray with the current layer's plane in painter-local space.
    private static bool TryGetCell(WorldPainter p, Vector2 guiPos, out Vector3Int cell) =>
        TryGetCellFromRay(p, HandleUtility.GUIPointToWorldRay(guiPos), out cell);

    private static bool TryGetCellFromRay(WorldPainter p, Ray ray, out Vector3Int cell)
    {
        cell = default;
        float gs = p.gridSize;
        if (gs <= 0f) return false;

        var w2l = p.transform.worldToLocalMatrix;
        Vector3 o = w2l.MultiplyPoint3x4(ray.origin);
        Vector3 d = w2l.MultiplyVector(ray.direction);

        float planeY = (s_layer - 0.5f) * gs;   // bottom face of the layer
        if (Mathf.Abs(d.y) < 1e-6f) return false;
        float t = (planeY - o.y) / d.y;
        if (t < 0f) return false;

        Vector3 hit = o + d * t;
        cell = new Vector3Int(
            Mathf.RoundToInt(hit.x / gs), s_layer, Mathf.RoundToInt(hit.z / gs));
        return true;
    }

    // ---- Preview mesh ----

    private void EnsureResources()
    {
        if (_vcOpaqueMat == null)
        {
            var vcShader = Shader.Find("Hidden/WorldPainterPreviewVC");
            if (vcShader != null)
            {
                _vcOpaqueMat = new Material(vcShader) { hideFlags = HideFlags.HideAndDontSave };

                _vcClearMat = new Material(vcShader) { hideFlags = HideFlags.HideAndDontSave };
                _vcClearMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _vcClearMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _vcClearMat.SetFloat("_ZWrite", 0f);
                _vcClearMat.renderQueue = 3000;
            }
        }

        if (_tintClearMat == null)
        {
            var tintShader = Shader.Find("Hidden/WorldPainterPreview");
            if (tintShader != null)
            {
                _tintClearMat = new Material(tintShader) { hideFlags = HideFlags.HideAndDontSave };
                _tintClearMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _tintClearMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _tintClearMat.SetFloat("_ZWrite", 0f);
                _tintClearMat.renderQueue = 3000;
            }
        }
    }

    private class MeshBuild
    {
        public readonly List<Vector3> verts  = new();
        public readonly List<Color32> colors = new();
        public readonly List<int>     tris   = new();
    }

    private void RebuildPreviewIfNeeded(WorldPainter p)
    {
        bool cacheValid = _previewRoot != null && p == _cachedPainter
            && p.EditVersion == _cachedVersion && s_layer == _cachedLayer
            && s_mode == _cachedMode && s_showOtherLayers == _cachedShowOther
            && s_ghostBelow == _cachedGhostBelow && s_ghostAbove == _cachedGhostAbove;
        if (cacheValid) return;

        // Throttle: every drag event bumps EditVersion, and rebuilding the whole
        // preview per event makes strokes on big maps crawl. The Layout pass runs
        // constantly, so a skipped rebuild is retried until it syncs.
        if (EditorApplication.timeSinceStartup - _lastRebuildTime < 0.15) return;
        _lastRebuildTime = EditorApplication.timeSinceStartup;

        _cachedPainter    = p;
        _cachedVersion    = p.EditVersion;
        _cachedLayer      = s_layer;
        _cachedMode       = s_mode;
        _cachedShowOther  = s_showOtherLayers;
        _cachedGhostBelow = s_ghostBelow;
        _cachedGhostAbove = s_ghostAbove;

        if (_previewRoot == null)
        {
            _previewRoot = new GameObject(PreviewRootName) { hideFlags = HideFlags.HideAndDontSave };
        }
        else
        {
            for (int i = _previewRoot.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(_previewRoot.transform.GetChild(i).gameObject);
        }
        foreach (var m in _previewMeshes) if (m != null) Object.DestroyImmediate(m);
        _previewMeshes.Clear();

        float gs = p.gridSize;
        float h  = gs * 0.5f;
        var ghostTint = new Color(0.45f, 0.45f, 0.50f);

        // ---- Blocks: face-culled colored cubes, chunked for frustum culling ----
        // Onion-skin: adjacent layers can render as a transparent ghost mesh so
        // new blocks are easy to align with what's directly above/below.
        var chunks  = new Dictionary<(int cx, int cz), MeshBuild>();
        var ghostMb = new MeshBuild();
        foreach (var (pos, type, _) in p.AllBlocks)
        {
            bool onLayer = pos.y == s_layer;
            bool onion   = !onLayer && !s_showOtherLayers
                && ((pos.y == s_layer - 1 && s_ghostBelow) ||
                    (pos.y == s_layer + 1 && s_ghostAbove));
            if (!onLayer && !s_showOtherLayers && !onion) continue;

            Color c = CachedTypeColor(p, type);
            if (!onLayer && !onion) c = Color.Lerp(c, ghostTint, 0.6f);
            if (((pos.x + pos.z) & 1) == 0) c *= 0.94f;   // subtle checker so cells read
            c.a = onion ? 0.30f : 1f;

            MeshBuild mb;
            if (onion)
            {
                mb = ghostMb;
            }
            else
            {
                var key = (pos.x >> ChunkShift, pos.z >> ChunkShift);
                if (!chunks.TryGetValue(key, out mb))
                    chunks[key] = mb = new MeshBuild();
            }

            // Onion-skin ghosts get shrunken, cull-free cubes — a solid fill-down
            // slab would otherwise cull itself down to an invisible flat sheet.
            EmitBlockCube(p, mb, pos, c, gs, cullFaces: !onion, half: onion ? h * 0.88f : h);
        }

        foreach (var kvp in chunks)
            CreatePreviewChild($"Blocks_{kvp.Key.cx}_{kvp.Key.cz}", kvp.Value, _vcOpaqueMat);

        CreatePreviewChild("GhostLayers", ghostMb, _vcClearMat);

        // ---- Zone overlays for the active paint mode: flat quads above cell tops ----
        if (s_mode == PaintMode.Foliage || s_mode == PaintMode.Water)
        {
            var zb = new MeshBuild();
            Color zc = s_mode == PaintMode.Foliage ? FoliageColor : WaterColor;
            zc.a = s_mode == PaintMode.Foliage ? 0.5f : 0.75f;
            Color zcOther = new(zc.r, zc.g, zc.b, 0.15f);   // other layers: barely-there hint
            float lift = h + gs * 0.02f;

            if (s_mode == PaintMode.Foliage)
            {
                foreach (var (pos, _) in p.AllFoliageZones)
                {
                    if (pos.y != s_layer && !s_showOtherLayers) continue;
                    AddZoneQuad(zb, pos.y == s_layer ? zc : zcOther, pos, gs, h, lift);
                }
            }
            else
            {
                foreach (var pos in p.AllWaterfallZones)
                {
                    if (pos.y != s_layer && !s_showOtherLayers) continue;
                    AddZoneQuad(zb, pos.y == s_layer ? zc : zcOther, pos, gs, h, lift);
                }
            }

            CreatePreviewChild("Zones", zb, _vcClearMat);
        }

        _previewRoot.SetActive(s_showBlocks);
        SceneView.RepaintAll();
    }

    private static void EmitBlockCube(WorldPainter p, MeshBuild mb, Vector3Int pos,
        Color c, float gs, bool cullFaces, float half)
    {
        float h = half;
        float x = pos.x * gs, y = pos.y * gs, z = pos.z * gs;

        if (!cullFaces || !p.HasBlock(new Vector3Int(pos.x, pos.y + 1, pos.z)))
            AddFace(mb, c,
                new Vector3(x - h, y + h, z - h), new Vector3(x - h, y + h, z + h),
                new Vector3(x + h, y + h, z + h), new Vector3(x + h, y + h, z - h));
        if (!cullFaces || !p.HasBlock(new Vector3Int(pos.x, pos.y - 1, pos.z)))
            AddFace(mb, c * 0.55f,
                new Vector3(x - h, y - h, z - h), new Vector3(x + h, y - h, z - h),
                new Vector3(x + h, y - h, z + h), new Vector3(x - h, y - h, z + h));
        if (!cullFaces || !p.HasBlock(new Vector3Int(pos.x + 1, pos.y, pos.z)))
            AddFace(mb, c * 0.80f,
                new Vector3(x + h, y - h, z + h), new Vector3(x + h, y - h, z - h),
                new Vector3(x + h, y + h, z - h), new Vector3(x + h, y + h, z + h));
        if (!cullFaces || !p.HasBlock(new Vector3Int(pos.x - 1, pos.y, pos.z)))
            AddFace(mb, c * 0.80f,
                new Vector3(x - h, y - h, z - h), new Vector3(x - h, y - h, z + h),
                new Vector3(x - h, y + h, z + h), new Vector3(x - h, y + h, z - h));
        if (!cullFaces || !p.HasBlock(new Vector3Int(pos.x, pos.y, pos.z + 1)))
            AddFace(mb, c * 0.70f,
                new Vector3(x - h, y - h, z + h), new Vector3(x + h, y - h, z + h),
                new Vector3(x + h, y + h, z + h), new Vector3(x - h, y + h, z + h));
        if (!cullFaces || !p.HasBlock(new Vector3Int(pos.x, pos.y, pos.z - 1)))
            AddFace(mb, c * 0.70f,
                new Vector3(x + h, y - h, z - h), new Vector3(x - h, y - h, z - h),
                new Vector3(x - h, y + h, z - h), new Vector3(x + h, y + h, z - h));
    }

    private static void AddZoneQuad(MeshBuild mb, Color c, Vector3Int pos, float gs, float h, float lift)
    {
        float x = pos.x * gs, y = pos.y * gs + lift, z = pos.z * gs;
        AddFace(mb, c,
            new Vector3(x - h, y, z - h), new Vector3(x - h, y, z + h),
            new Vector3(x + h, y, z + h), new Vector3(x + h, y, z - h));
    }

    private static void AddFace(MeshBuild mb, Color c,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        int i = mb.verts.Count;
        mb.verts.Add(v0); mb.verts.Add(v1); mb.verts.Add(v2); mb.verts.Add(v3);
        Color32 c32 = c;
        mb.colors.Add(c32); mb.colors.Add(c32); mb.colors.Add(c32); mb.colors.Add(c32);
        mb.tris.Add(i); mb.tris.Add(i + 1); mb.tris.Add(i + 2);
        mb.tris.Add(i); mb.tris.Add(i + 2); mb.tris.Add(i + 3);
    }

    private void CreatePreviewChild(string name, MeshBuild mb, Material mat)
    {
        if (mb.verts.Count == 0 || mat == null) return;

        var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        mesh.indexFormat = mb.verts.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(mb.verts);
        mesh.SetColors(mb.colors);
        mesh.SetTriangles(mb.tris, 0);
        mesh.RecalculateBounds();
        _previewMeshes.Add(mesh);

        var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(_previewRoot.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial    = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;
    }

    private void SyncPreviewTransform(WorldPainter p)
    {
        if (_previewRoot == null) return;
        var t = p.transform;
        _previewRoot.transform.SetPositionAndRotation(t.position, t.rotation);
        _previewRoot.transform.localScale = t.lossyScale;
        if (_previewRoot.activeSelf != s_showBlocks)
            _previewRoot.SetActive(s_showBlocks);
    }

    private void DestroyPreview()
    {
        if (_previewRoot != null) Object.DestroyImmediate(_previewRoot);
        _previewRoot = null;
        foreach (var m in _previewMeshes) if (m != null) Object.DestroyImmediate(m);
        _previewMeshes.Clear();
        _cachedPainter = null;
        _cachedVersion = -1;
    }

    // HideAndDontSave objects survive domain reloads — clean up any leftovers
    // from a previous session or an editor crash.
    private void DestroyStalePreviewRoots()
    {
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go == null || go.name != PreviewRootName) continue;
            if (EditorUtility.IsPersistent(go)) continue;
            if (go.transform.parent != null) continue;
            Object.DestroyImmediate(go);
        }
        _previewRoot = null;
    }

    // ---- Hover ghost: transparent silhouette of the selected prefab at the cursor ----

    private class MeshEntry
    {
        public Mesh      mesh;
        public int       submesh;
        public Matrix4x4 toBlock;   // prefab child → normalized block-local space
    }

    private readonly Dictionary<GameObject, List<MeshEntry>> _prefabEntries = new();
    private float _entriesGridSize = -1f;

    // Created lazily — Unity forbids constructing MaterialPropertyBlock in a
    // ScriptableObject field initializer (it runs in the constructor).
    private MaterialPropertyBlock _hoverProps;

    // Splits a palette prefab into (mesh, submesh) entries, each with its transform
    // into normalized block space — identical fit/centering to Bake().
    private List<MeshEntry> GetPrefabEntries(GameObject prefab, float gs)
    {
        if (!Mathf.Approximately(_entriesGridSize, gs))
        {
            _prefabEntries.Clear();
            _entriesGridSize = gs;
        }
        if (_prefabEntries.TryGetValue(prefab, out var cached)) return cached;

        var entries = new List<MeshEntry>();
        Bounds b = WorldPainter.ComputePrefabBounds(prefab);
        float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
        float fit = maxDim > 0.001f ? gs / maxDim : gs;
        Matrix4x4 rootInv = prefab.transform.worldToLocalMatrix;
        Matrix4x4 norm = Matrix4x4.TRS(-b.center * fit, Quaternion.identity, Vector3.one * fit);

        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(false))
        {
            if (mf.sharedMesh == null) continue;
            Matrix4x4 childToRoot = rootInv * mf.transform.localToWorldMatrix;
            for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                entries.Add(new MeshEntry
                {
                    mesh    = mf.sharedMesh,
                    submesh = s,
                    toBlock = norm * childToRoot,
                });
        }

        _prefabEntries[prefab] = entries;
        return entries;
    }

    // Drawn during Repaint only — a handful of queued draws at the cursor.
    private void DrawHoverGhost(WorldPainter p, Camera cam)
    {
        // Not while a stroke/box drag is live — the footprint highlight is enough.
        if (_strokeActive || _boxDragging) return;
        if (!_hasHover || s_mode != PaintMode.Blocks || s_eraseMode || _tintClearMat == null) return;
        if (p.palette == null || p.palette.Length == 0) return;

        int type = Mathf.Clamp(s_selectedType, 0, p.palette.Length - 1);
        var bt = p.palette[type];
        if (bt?.prefab == null) return;

        float gs = p.gridSize;
        var entries = GetPrefabEntries(bt.prefab, gs);
        if (entries.Count == 0) return;

        Color c = CachedTypeColor(p, type);
        _hoverProps ??= new MaterialPropertyBlock();
        _hoverProps.SetColor("_Color", new Color(c.r, c.g, c.b, 0.45f));

        // Ghost every cell of the brush footprint; for large brushes only the
        // center (the footprint outline already shows the extent).
        int size = s_boxMode ? 1 : s_brushSize;
        int bMin = -(size - 1) / 2;
        int bMax = size / 2;
        bool centerOnly = size * size > 25;

        Matrix4x4 l2w = p.transform.localToWorldMatrix;
        for (int dz = bMin; dz <= bMax; dz++)
            for (int dx = bMin; dx <= bMax; dx++)
            {
                if (centerOnly && (dx != 0 || dz != 0)) continue;
                Matrix4x4 block = l2w * Matrix4x4.Translate(new Vector3(
                    (_hoverCell.x + dx) * gs,
                    _hoverCell.y * gs + bt.yOffset,
                    (_hoverCell.z + dz) * gs));
                foreach (var e in entries)
                    Graphics.DrawMesh(e.mesh, block * e.toBlock, _tintClearMat, 0, cam, e.submesh, _hoverProps);
            }
    }

    // ---- Grid + cursor handles ----

    private void DrawGridAndCursor(WorldPainter p)
    {
        float gs = p.gridSize;
        // Overlays sit just above the layer's TOP face and ignore the depth
        // buffer — at the bottom face they vanish under already-painted ground.
        float planeY = (s_layer + 0.5f) * gs + gs * 0.02f;
        bool erasing = _strokeErase && (_strokeActive || _boxDragging)
                       || (!_strokeActive && !_boxDragging && (Event.current.control || s_eraseMode));

        Color modeColor = s_mode switch
        {
            PaintMode.Foliage => FoliageColor,
            PaintMode.Water   => WaterColor,
            _                 => CachedTypeColor(p, s_selectedType),
        };
        Color cursor = erasing ? EraseColor : modeColor;

        var prevZTest = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        using (new Handles.DrawingScope(p.transform.localToWorldMatrix))
        {
            // Grid: centered on the hovered cell, or on whatever the camera is
            // looking at when the mouse is elsewhere — visible whenever the tool
            // is active. Every 10th line is stronger for orientation.
            int gcx, gcz;
            bool haveGridCenter;
            if (_hasHover)
            {
                gcx = _hoverCell.x; gcz = _hoverCell.z; haveGridCenter = true;
            }
            else
            {
                Vector3Int viewCell = default;
                haveGridCenter = _sceneCamera != null && TryGetCellFromRay(p,
                    _sceneCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)), out viewCell);
                gcx = viewCell.x;
                gcz = viewCell.z;
            }

            if (haveGridCenter)
            {
                const int R = 20;
                var minor = new Color(1f, 1f, 1f, 0.30f);
                var major = new Color(1f, 1f, 1f, 0.65f);
                for (int i = -R; i <= R + 1; i++)
                {
                    Handles.color = ((gcx + i) % 10 + 10) % 10 == 0 ? major : minor;
                    Handles.DrawLine(
                        new Vector3((gcx + i - 0.5f) * gs, planeY, (gcz - R - 0.5f) * gs),
                        new Vector3((gcx + i - 0.5f) * gs, planeY, (gcz + R + 0.5f) * gs));
                    Handles.color = ((gcz + i) % 10 + 10) % 10 == 0 ? major : minor;
                    Handles.DrawLine(
                        new Vector3((gcx - R - 0.5f) * gs, planeY, (gcz + i - 0.5f) * gs),
                        new Vector3((gcx + R + 0.5f) * gs, planeY, (gcz + i - 0.5f) * gs));
                }
            }

            if (_boxDragging)
            {
                var end = Event.current.shift ? ConstrainToSquare(_boxStart, _boxEnd) : _boxEnd;
                int minX = Mathf.Min(_boxStart.x, end.x), maxX = Mathf.Max(_boxStart.x, end.x);
                int minZ = Mathf.Min(_boxStart.y, end.y), maxZ = Mathf.Max(_boxStart.y, end.y);

                var corners = new[]
                {
                    new Vector3((minX - 0.5f) * gs, planeY, (minZ - 0.5f) * gs),
                    new Vector3((minX - 0.5f) * gs, planeY, (maxZ + 0.5f) * gs),
                    new Vector3((maxX + 0.5f) * gs, planeY, (maxZ + 0.5f) * gs),
                    new Vector3((maxX + 0.5f) * gs, planeY, (minZ - 0.5f) * gs),
                };
                Handles.DrawSolidRectangleWithOutline(corners,
                    new Color(cursor.r, cursor.g, cursor.b, 0.25f), cursor);

                Handles.Label(new Vector3((minX + maxX) * 0.5f * gs, planeY, (minZ + maxZ) * 0.5f * gs),
                    $"{maxX - minX + 1} x {maxZ - minZ + 1}", EditorStyles.whiteBoldLabel);
            }
            else if (_hasHover)
            {
                // Brush footprint (box mode always uses a single cell as the anchor).
                int size = s_boxMode ? 1 : s_brushSize;
                int bMin = -(size - 1) / 2;
                int bMax = size / 2;
                float x0 = (_hoverCell.x + bMin - 0.5f) * gs;
                float x1 = (_hoverCell.x + bMax + 0.5f) * gs;
                float z0 = (_hoverCell.z + bMin - 0.5f) * gs;
                float z1 = (_hoverCell.z + bMax + 0.5f) * gs;

                Handles.color = cursor;
                Handles.DrawWireCube(
                    new Vector3((x0 + x1) * 0.5f, _hoverCell.y * gs, (z0 + z1) * 0.5f),
                    new Vector3(x1 - x0, gs, z1 - z0));

                var corners = new[]
                {
                    new Vector3(x0, planeY, z0),
                    new Vector3(x0, planeY, z1),
                    new Vector3(x1, planeY, z1),
                    new Vector3(x1, planeY, z0),
                };
                Handles.DrawSolidRectangleWithOutline(corners,
                    new Color(cursor.r, cursor.g, cursor.b, 0.30f), cursor);
            }
        }
        Handles.zTest = prevZTest;
    }

    // ---- Floating panel ----

    private void DrawPanel(WorldPainter p)
    {
        // Plain area + box instead of GUI.Window: windows join the IMGUI focus
        // system and a focused window can swallow keyboard events, which kills
        // the scene view's RMB+WASD flythrough.
        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(10, 10, 258, 2000));
        GUILayout.BeginVertical("World Painter", GUI.skin.window);
        PanelContents(p);
        GUILayout.EndVertical();
        if (Event.current.type == EventType.Repaint)
        {
            var content = GUILayoutUtility.GetLastRect();
            _panelRect = new Rect(10, 10, 258, content.height);
        }
        GUILayout.EndArea();
        Handles.EndGUI();
    }

    private void PanelContents(WorldPainter p)
    {
        s_mode = (PaintMode)GUILayout.Toolbar((int)s_mode, new[] { "Blocks", "Foliage", "Water" });

        EditorGUILayout.BeginHorizontal();
        s_boxMode = GUILayout.Toolbar(s_boxMode ? 1 : 0, new[] { "Draw", "Box" }) == 1;
        GUI.backgroundColor = s_eraseMode ? EraseColor : Color.white;
        if (GUILayout.Button(s_eraseMode ? "Erasing!" : "Erase", GUILayout.Width(64)))
            s_eraseMode = !s_eraseMode;
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Layer", GUILayout.Width(36));
        if (GUILayout.Button("−", GUILayout.Width(24))) s_layer = Mathf.Max(0, s_layer - 1);
        s_layer = Mathf.Max(0, EditorGUILayout.IntField(s_layer, GUILayout.Width(34)));
        if (GUILayout.Button("+", GUILayout.Width(24))) s_layer++;
        GUILayout.Label("Ctrl+Scroll", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Brush", GUILayout.Width(36));
        GUI.enabled = !s_boxMode;   // box drag defines its own area
        s_brushSize = EditorGUILayout.IntSlider(s_brushSize, 1, 100);
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        s_fillDown        = GUILayout.Toggle(s_fillDown, "Fill down to layer 0 (blocks + water)");
        s_showOtherLayers = GUILayout.Toggle(s_showOtherLayers, "Show other layers (faded)");
        GUI.enabled = !s_showOtherLayers;   // onion-skin only makes sense in single-layer view
        s_ghostBelow = GUILayout.Toggle(s_ghostBelow, "Ghost layer below (transparent)");
        s_ghostAbove = GUILayout.Toggle(s_ghostAbove, "Ghost layer above (transparent)");
        GUI.enabled = true;
        s_showBlocks      = GUILayout.Toggle(s_showBlocks, "Show unbaked blocks");
        s_hideBaked       = GUILayout.Toggle(s_hideBaked, "Hide baked map while editing");

        GUILayout.Space(2);

        if (s_mode == PaintMode.Blocks)
            DrawBlockPalette(p);
        else if (s_mode == PaintMode.Foliage)
            DrawFoliagePalette(p);
        else
            GUILayout.Label($"{p.WaterfallZoneCount} water zone(s) painted", EditorStyles.miniLabel);

        GUILayout.Space(2);
        GUILayout.Label($"{p.BlockCount} blocks  |  Ctrl+Click = erase  |  [ ] = brush size",
            EditorStyles.miniLabel);
        GUILayout.Label("Tile: . = next · , = prev · 1–9 = pick", EditorStyles.miniLabel);
        GUILayout.Label("Camera: RMB fly · MMB pan · Alt+LMB orbit", EditorStyles.miniLabel);
        if (p.BlockCount > UndoBlockLimit)
        {
            GUI.color = new Color(1f, 0.75f, 0.4f);
            GUILayout.Label("Undo disabled — map too large to snapshot safely.",
                EditorStyles.miniLabel);
            GUI.color = Color.white;
        }

        GUI.backgroundColor = new Color(1f, 0.85f, 0.45f);
        if (GUILayout.Button("Exit Brush — select/move objects (Esc)"))
            ToolManager.RestorePreviousTool();
        GUI.backgroundColor = Color.white;

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Save")) p.SaveToFile();
        GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
        if (GUILayout.Button("Bake"))
        {
            var painter = p;
            EditorApplication.delayCall += () =>
            {
                if (painter == null) return;
                painter.Bake();
                EditorUtility.SetDirty(painter);
            };
        }
        GUI.backgroundColor = new Color(0.45f, 0.85f, 0.55f);
        if (GUILayout.Button("Foliage"))
        {
            var painter = p;
            EditorApplication.delayCall += () =>
            {
                if (painter == null) return;
                painter.GenerateFoliage();
                painter.BakeFoliage();
                EditorUtility.SetDirty(painter);
            };
        }
        GUI.backgroundColor = new Color(0.35f, 0.55f, 0.95f);
        if (GUILayout.Button("Water"))
        {
            var painter = p;
            EditorApplication.delayCall += () =>
            {
                if (painter == null) return;
                painter.GenerateWaterfall();
                EditorUtility.SetDirty(painter);
            };
        }
        GUI.backgroundColor = Color.white;
        if (GUILayout.Button("Grid…")) WorldPainterWindow.Open(p);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBlockPalette(WorldPainter p)
    {
        if (p.palette == null || p.palette.Length == 0)
        {
            EditorGUILayout.HelpBox("Palette is empty — add block types on the WorldPainter.",
                MessageType.Info);
            return;
        }

        const int perRow = 3;
        for (int i = 0; i < p.palette.Length; i++)
        {
            if (i % perRow == 0) EditorGUILayout.BeginHorizontal();

            bool selected = i == s_selectedType && !s_eraseMode;
            GUI.backgroundColor = selected
                ? new Color(0.25f, 1f, 0.25f)     // selected: clearly green
                : new Color(0.95f, 0.35f, 0.35f); // unselected: red
            if (GUILayout.Button(p.palette[i]?.name ?? $"T{i}", GUILayout.Height(22)))
            {
                s_selectedType    = i;
                s_selectedVariant = -1;
                s_eraseMode       = false;
            }

            if (i % perRow == perRow - 1 || i == p.palette.Length - 1)
                EditorGUILayout.EndHorizontal();
        }
        GUI.backgroundColor = Color.white;

        var bt = (s_selectedType >= 0 && s_selectedType < p.palette.Length)
            ? p.palette[s_selectedType] : null;
        if (bt?.variants != null && bt.variants.Length > 0)
        {
            GUILayout.Label("Variant:", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = s_selectedVariant == -1 ? SelectedBlue : Color.white;
            if (GUILayout.Button("Auto", GUILayout.Height(20))) s_selectedVariant = -1;
            for (int v = 0; v < bt.variants.Length; v++)
            {
                GUI.backgroundColor = s_selectedVariant == v ? SelectedBlue : Color.white;
                string label = bt.variants[v]?.prefab != null ? bt.variants[v].prefab.name : $"V{v}";
                if (GUILayout.Button(label, GUILayout.Height(20))) s_selectedVariant = v;
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }
    }

    private void DrawFoliagePalette(WorldPainter p)
    {
        var types = p.foliageSettings?.foliageTypes;
        if (types == null || types.Length == 0)
        {
            EditorGUILayout.HelpBox("Add Foliage Types in the WorldPainter's Foliage Settings.",
                MessageType.Info);
            return;
        }

        GUILayout.Label("Paint types (toggle, multi-select):", EditorStyles.miniLabel);
        EditorGUILayout.BeginHorizontal();
        for (int i = 0; i < types.Length && i < 31; i++)
        {
            int bit = 1 << i;
            bool active = (s_foliageMask & bit) != 0;
            GUI.backgroundColor = active
                ? GetFoliageTypeColor(types, i) * 1.5f
                : new Color(0.3f, 0.3f, 0.3f);
            if (GUILayout.Button(types[i]?.name ?? $"Type {i}", GUILayout.Height(22)))
            {
                int next = s_foliageMask ^ bit;
                if (next != 0) s_foliageMask = next;   // never all-off
            }
        }
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        GUILayout.Label($"{p.FoliageZoneCount} zone(s) painted", EditorStyles.miniLabel);
    }

    // ---- Colors ----

    // Cached per palette entry — the uncached lookup walks the prefab's children,
    // far too slow for per-repaint use. Invalidated by any edit (EditVersion).
    private Color[]      _typeColorCache;
    private int          _typeColorVersion = -1;
    private WorldPainter _typeColorPainter;

    private Color CachedTypeColor(WorldPainter p, int idx)
    {
        var pal = p?.palette;
        if (pal == null || idx < 0 || idx >= pal.Length) return Color.gray;

        if (_typeColorPainter != p || _typeColorCache == null
            || _typeColorCache.Length != pal.Length || _typeColorVersion != p.EditVersion)
        {
            _typeColorCache = new Color[pal.Length];
            for (int i = 0; i < pal.Length; i++)
                _typeColorCache[i] = ComputeTypeColor(p, i);
            _typeColorPainter = p;
            _typeColorVersion = p.EditVersion;
        }
        return _typeColorCache[idx];
    }

    private static Color GetFoliageTypeColor(FoliageType[] types, int i)
    {
        if (i < 0 || i >= types.Length || types[i] == null) return FoliageColor;
        Color c = types[i].tileColor;
        return c.a > 0.01f ? c : FoliageColor;
    }

    private static Color ComputeTypeColor(WorldPainter p, int idx)
    {
        var bt = p.palette[idx];
        if (bt != null && bt.tileColor.a > 0.01f)
            return bt.tileColor;

        if (bt?.prefab != null)
        {
            var mat = bt.prefab.GetComponentInChildren<MeshRenderer>()?.sharedMaterial;
            if (mat != null)
            {
                if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
                if (mat.HasProperty("_Color"))     return mat.GetColor("_Color");
            }
        }

        return FallbackColors[idx % FallbackColors.Length];
    }
}

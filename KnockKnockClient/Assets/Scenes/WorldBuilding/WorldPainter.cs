using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Stores a grid of placed blocks (by palette index). Each palette entry is a
/// prefab — any number of child MeshRenderers with any number of materials are
/// all merged into the baked output.
///
/// Open  Tools > World Painter  to paint the grid and press Create &amp; Bake.
/// </summary>
[ExecuteAlways]
public class WorldPainter : MonoBehaviour
{
    [Header("Grid")]
    [Tooltip("World-space size of one grid cell. Model your prefabs to fit this size.")]
    public float gridSize = 1f;

    [Header("Palette")]
    public BlockType[] palette;

    [Tooltip("Prop layer palette (Props paint mode): markers and decorations painted ON TOP of " +
             "blocks. Props live on their own layer — they never replace ground blocks and are " +
             "never merged; each is instantiated as a real object standing on the floor of its " +
             "cell at bake. Use for spawn points, pickup markers and decorative props.")]
    public PropType[] propPalette;

    [Header("Foliage")]
    public FoliageZoneSettings foliageSettings = new FoliageZoneSettings();

    [Header("Waterfall")]
    public WaterfallSettings waterfallSettings = new WaterfallSettings();

    [Header("Persistence")]
    [Tooltip("Unique name for this WorldPainter instance. Used for the JSON save file " +
             "and for unique baked mesh asset paths. Leave empty to use the default 'WorldPainter' prefix.")]
    public string dataFileName = "";

    [Header("Bake Settings")]
    [Tooltip("Grid cell size within each chunk. Chunks are chunkSize × chunkSize cells. " +
             "Smaller = more chunks but better streaming; larger = fewer chunks but more geometry per mesh.")]
    public int chunkSize = 32;

    [Tooltip("Physics layer assigned to ALL baked geometry — the visual chunks as well as " +
             "the colliders (BakedColliders, BakedWalls, NavMesh). Must match the Surface Layers " +
             "mask on FoliageArea for foliage raycasts to hit the ground. NOTE: this is a layer " +
             "INDEX, and indices differ between projects — check that it names the layer you mean.")]
    public int bakeLayer = 6;   // layer index, NOT a name: verify per project (see Bake())

    [Header("Baked Renderer")]
    [Tooltip("Shadow-band softness on the baked mesh (0 = hard/crisp, 1 = very soft/blobby). " +
             "Lower values give sharper shadows. Higher values reduce shadow-cascade seam flicker. " +
             "Re-bake or re-select the object after changing this.")]
    [Range(0f, 1f)]
    public float bakedCelSmoothness = 0.1f;

    // ---- Data types ----

    [System.Serializable]
    public class BlockType
    {
        public string     name   = "Block";
        [Tooltip("Prefab to stamp at each painted cell. Supports multiple child meshes and multiple materials. " +
                 "Also used as the final fallback when Auto-Tile is on and all matching grid slots are empty.")]
        public GameObject prefab;
        [Tooltip("Vertical offset applied to this block type during bake (world units). " +
                 "Positive = raised, negative = lowered relative to the grid layer.")]
        public float      yOffset   = 0f;
        [Tooltip("When enabled, shared faces between this tile and its neighbours are never culled. " +
                 "Use for open structures (bridges, fences, arches) where the side faces must stay visible.")]
        public bool       noCull    = false;
        [Tooltip("When disabled, this block type is emitted into the 'NavMesh_NotWalkable' collider child " +
                 "so a NavMeshSurface can treat it as impassable. Walkable blocks go into 'NavMesh_Walkable'.")]
        public bool       walkable  = true;
        [Tooltip("When enabled, this block type is skipped during mesh merging and instead " +
                 "instantiated as a real child GameObject so it keeps its original components and hierarchy.")]
        public bool       excludeFromBake = false;

        [Tooltip("When enabled, blocks of this type produce no visual geometry. Their positions are " +
                 "merged into a single invisible MeshCollider child ('BakedWalls'), with shared faces " +
                 "between adjacent wall blocks culled. Ideal for invisible boundary walls around the map.")]
        public bool       mergeCollider   = false;

        [Tooltip("Exported to the GAME SERVER as blocking collision (painted cells merged into " +
                 "axis-aligned boxes on the XZ plane). Enable on visible wall/obstacle types that must " +
                 "stop players and shots online. Types with Merge Collider are ALWAYS exported as " +
                 "server walls too, so invisible boundary walls block on both sides.")]
        public bool       serverWall      = false;

        [Header("Auto-Tile")]
        [Tooltip("When enabled, adjacent tiles of the same type automatically choose a variant based on which sides have a same-type neighbour.")]
        public bool         isAutoTile = false;
        public AutoTileGrid autoTile   = new();

        [Header("Variants")]
        [Tooltip("Manual tile variants. Select which variant to use when painting blocks of this type.")]
        public TileVariant[] variants = new TileVariant[0];

        [Tooltip("Display color in the World Painter grid. Leave at default (clear) to auto-detect from material.")]
        public Color        tileColor  = Color.clear;
    }

    /// <summary>
    /// 3×3 grid of prefab variants selected by a block's neighbourhood.
    ///
    ///   Col 0 (Left)  = has same-type neighbour at +X, but NOT at -X
    ///   Col 1 (Center)= both ±X neighbours present  OR  neither
    ///   Col 2 (Right) = has same-type neighbour at -X, but NOT at +X
    ///
    ///   Row 0 (Front) = has same-type neighbour at +Z, but NOT at -Z
    ///   Row 1 (Center)= both ±Z neighbours present  OR  neither
    ///   Row 2 (Back)  = has same-type neighbour at -Z, but NOT at +Z
    ///
    /// Indexed as tiles[row * 3 + col].  Empty slots fall back toward (1,1).
    /// </summary>
    [System.Serializable]
    public class AutoTileGrid
    {
        public GameObject[] tiles    = new GameObject[9];
        [Tooltip("Per-slot Y offset (world units) applied on top of the BlockType yOffset during bake. " +
                 "Use this to align prefabs that have different pivot heights.")]
        public float[]      offsets  = new float[9];
        [Tooltip("Per-slot X offset (world units) applied during bake. " +
                 "Use this to laterally nudge a variant that has an off-centre pivot.")]
        public float[]      xOffsets = new float[9];

        /// <summary>Returns the best available prefab for the given grid slot,
        /// falling back: exact → same-col center-row → same-row center-col → center → null.</summary>
        public GameObject Get(int col, int row)
        {
            int i = ResolveIndex(col, row);
            return i >= 0 ? tiles[i] : null;
        }

        /// <summary>Returns the Y offset for the resolved slot (same fallback cascade as Get).</summary>
        public float GetOffset(int col, int row)
        {
            int i = ResolveIndex(col, row);
            return (i >= 0 && offsets != null && i < offsets.Length) ? offsets[i] : 0f;
        }

        /// <summary>Returns the X offset for the resolved slot (same fallback cascade as Get).</summary>
        public float GetXOffset(int col, int row)
        {
            int i = ResolveIndex(col, row);
            return (i >= 0 && xOffsets != null && i < xOffsets.Length) ? xOffsets[i] : 0f;
        }

        private int ResolveIndex(int col, int row)
        {
            int i = row * 3 + col;
            if (i >= 0 && i < tiles.Length && tiles[i]) return i;
            if (row != 1) { i = 3 + col;     if (tiles[i]) return i; }
            if (col != 1) { i = row * 3 + 1; if (tiles[i]) return i; }
            return (tiles[4] != null) ? 4 : -1;
        }
    }

    [System.Serializable]
    public class TileVariant
    {
        public GameObject prefab;
        [Tooltip("Y offset applied during bake (world units).")]
        public float yOffset = 0f;
        [Tooltip("X offset applied during bake (world units).")]
        public float xOffset = 0f;
    }

    [System.Serializable]
    public class FoliageZoneSettings
    {
        [Tooltip("Foliage types to spawn (mesh, material, density, etc.)")]
        public FoliageType[] foliageTypes;
        public int    seed           = 42;
        [Tooltip("Y offset applied to each instance after surface hit (negative = push into ground)")]
        public float  yOffset        = 0f;
        [Tooltip("How far away foliage instances are visible")]
        public float  renderDistance = 100f;
        [Tooltip("Layers to raycast against when finding the ground surface")]
        public LayerMask surfaceLayers = ~0;
        [Tooltip("Half-height of the spawn volume above each grid layer (world units)")]
        public float  volumeHeight   = 3f;
    }

    [System.Serializable]
    public class WaterfallSettings
    {
        [Tooltip("How many grid units the waterfall extends downward from each painted cell")]
        public int   depth    = 4;
        [Tooltip("Material using the LowPolyWaterfall shader")]
        public Material material;
    }

    [System.Serializable]
    private class WaterfallZoneEntry
    {
        public Vector3Int pos;
        public WaterfallZoneEntry(Vector3Int p) { pos = p; }
    }


    [System.Serializable]
    private class BlockEntry
    {
        public Vector3Int pos;
        public int        typeIndex;
        public int        variantIndex = -1;
        public BlockEntry(Vector3Int p, int t, int v = -1) { pos = p; typeIndex = t; variantIndex = v; }
    }

    [System.Serializable]
    public class PropType
    {
        public string     name = "Prop";
        [Tooltip("Prefab placed at each painted prop cell — spawn markers, pickup markers, " +
                 "decorations. Instantiated as a real child object at bake (never merged), " +
                 "standing on the floor of its cell.")]
        public GameObject prefab;
        [Tooltip("Extra vertical offset applied at bake (world units).")]
        public float      yOffset = 0f;
        [Tooltip("Color this prop type is drawn with in the World Painter grid overlay " +
                 "and palette, so different prop types are distinguishable while painting. " +
                 "Editor visualization only — has no effect on the baked map.")]
        public Color      color   = new Color(1f, 0.5f, 0.85f);
        [Tooltip("When enabled, this prop's cell is exported to the SERVER as a solid " +
                 "wall (axis-aligned box covering the full cell footprint) — use for " +
                 "crates and other obstacles players cannot walk through. Ignored on " +
                 "marker prefabs (spawn/pickup markers are never solid).")]
        public bool       serverObstacle = false;
    }

    [System.Serializable]
    private class PropEntry
    {
        public Vector3Int pos;
        public int        typeIndex;
        public PropEntry(Vector3Int p, int t) { pos = p; typeIndex = t; }
    }

    [System.Serializable]
    private class FoliageZoneEntry
    {
        public Vector3Int pos;
        /// <summary>Bitmask — bit i set means foliageTypes[i] spawns in this zone.
        /// 0 is treated as 1 (first type) for backward compatibility.</summary>
        public int typeMask;
        public FoliageZoneEntry(Vector3Int p, int mask) { pos = p; typeMask = mask; }
    }

    // ---- Serialized block list ----

    [SerializeField, HideInInspector]
    private List<BlockEntry> blocks = new List<BlockEntry>();

    [SerializeField, HideInInspector]
    private List<FoliageZoneEntry> foliageZones = new List<FoliageZoneEntry>();

    [SerializeField, HideInInspector]
    private List<WaterfallZoneEntry> waterfallZones = new List<WaterfallZoneEntry>();

    [SerializeField, HideInInspector]
    private List<PropEntry> props = new List<PropEntry>();

    // ---- Runtime fast-lookup ----

    private Dictionary<Vector3Int, int> blockMap          = new Dictionary<Vector3Int, int>();
    private HashSet<Vector3Int>         foliageZoneSet    = new HashSet<Vector3Int>();
    private Dictionary<Vector3Int, int> foliageZoneTypes  = new Dictionary<Vector3Int, int>();
    private HashSet<Vector3Int>         waterfallZoneSet  = new HashSet<Vector3Int>();

    // Index of each entry in its serialized list, so Place/Remove/GetVariant are O(1)
    // instead of scanning the whole list — critical for big maps.
    private Dictionary<Vector3Int, int> blockIndex         = new Dictionary<Vector3Int, int>();
    private Dictionary<Vector3Int, int> variantMap         = new Dictionary<Vector3Int, int>();
    private Dictionary<Vector3Int, int> foliageZoneIndex   = new Dictionary<Vector3Int, int>();
    private Dictionary<Vector3Int, int> waterfallZoneIndex = new Dictionary<Vector3Int, int>();
    private Dictionary<Vector3Int, int> propMap            = new Dictionary<Vector3Int, int>();
    private Dictionary<Vector3Int, int> propIndex          = new Dictionary<Vector3Int, int>();

    /// <summary>Incremented on every edit. Editor tools compare this against a cached
    /// value to know when their preview data is stale.</summary>
    [System.NonSerialized] public int EditVersion;

    /// <summary>Read-only view of every painted block (grid position + palette type
    /// index). Used by the arena server-export tool, which reads the PAINTED data
    /// directly — no bake required.</summary>
    public System.Collections.Generic.IEnumerable<(Vector3Int pos, int typeIndex)> EnumerateBlocks()
    {
        foreach (var b in blocks)
            yield return (b.pos, b.typeIndex);
    }

    // Cached FoliageArea children — rebuilt after generation or domain reload so we never
    // call GetComponentsInChildren every frame.
    private FoliageArea[] _foliageRenderers = new FoliageArea[0];

    private void OnEnable()
    {
        RebuildMap();
        RebuildFoliageSet();
        RebuildWaterfallSet();
        RebuildPropMap();
        ApplyBakedRendererProperties();   // MPB is not serialized — reapply after domain reload
        RebuildFoliageRenderers();        // managedExternally is NonSerialized — must re-set each reload
    }

    private void RebuildFoliageRenderers()
    {
        // If a baked foliage mesh exists the static MeshRenderer handles it —
        // skip the per-frame DrawMeshInstanced path entirely.
        if (transform.Find("BakedFoliage") != null)
        {
            _foliageRenderers = new FoliageArea[0];
            return;
        }
        _foliageRenderers = GetComponentsInChildren<FoliageArea>();
        foreach (var fa in _foliageRenderers) fa.managedExternally = true;
    }

    // Single LateUpdate for all FoliageArea children — frustum planes and Camera.main
    // are computed once and shared, eliminating per-area overhead and allowing
    // proper AABB-vs-frustum culling to skip off-screen areas entirely.
    private void LateUpdate()
    {
        if (_foliageRenderers == null || _foliageRenderers.Length == 0) return;

        Camera cam = Camera.main;
        if (cam == null)
        {
            foreach (var fa in _foliageRenderers) if (fa) fa.DrawAllBatches();
            return;
        }

        Plane[]  planes = GeometryUtility.CalculateFrustumPlanes(cam);
        Vector3  camPos = cam.transform.position;
        foreach (var fa in _foliageRenderers)
            if (fa) fa.DrawBatches(planes, camPos);
    }

    private void RebuildMap()
    {
        blockMap.Clear();
        blockIndex.Clear();
        variantMap.Clear();
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            blockMap[b.pos]   = b.typeIndex;
            blockIndex[b.pos] = i;
            variantMap[b.pos] = b.variantIndex;
        }
        EditVersion++;
    }

    private void RebuildFoliageSet()
    {
        foliageZoneSet.Clear();
        foliageZoneTypes.Clear();
        foliageZoneIndex.Clear();
        for (int i = 0; i < foliageZones.Count; i++)
        {
            var fz = foliageZones[i];
            foliageZoneSet.Add(fz.pos);
            foliageZoneTypes[fz.pos] = fz.typeMask;
            foliageZoneIndex[fz.pos] = i;
        }
        EditVersion++;
    }

    private void RebuildWaterfallSet()
    {
        waterfallZoneSet.Clear();
        waterfallZoneIndex.Clear();
        for (int i = 0; i < waterfallZones.Count; i++)
        {
            waterfallZoneSet.Add(waterfallZones[i].pos);
            waterfallZoneIndex[waterfallZones[i].pos] = i;
        }
        EditVersion++;
    }

    /// <summary>Rebuilds all runtime lookup dictionaries from the serialized lists.
    /// Editor tools call this after undo/redo restores the lists behind our back.</summary>
    public void RebuildRuntimeState()
    {
        RebuildMap();
        RebuildFoliageSet();
        RebuildWaterfallSet();
    }

#if UNITY_EDITOR
    // Undo/redo and inspector edits restore the serialized lists without touching
    // the lookup dictionaries — OnValidate fires in both cases, so resync here.
    private void OnValidate() => RebuildRuntimeState();
#endif

    // Applies per-renderer overrides via material instances (not MaterialPropertyBlock,
    // which breaks SRP Batcher compatibility).
    private void ApplyBakedRendererProperties()
    {
        var bakedParent = transform.Find("Baked");
        if (bakedParent == null) return;

        // Apply overrides to each chunk's renderer
        foreach (Transform chunkTr in bakedParent)
        {
            var mr = chunkTr.GetComponent<MeshRenderer>();
            if (mr != null)
                ApplyBakedMaterialOverrides(mr);
        }
    }

    private void ApplyBakedMaterialOverrides(MeshRenderer mr)
    {
        // Create material instances so SRP Batcher can still batch by shader.
        var mats = mr.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
        {
            if (mats[i] == null) continue;
            mats[i] = new Material(mats[i]);
            // Rim light: the step-function ring sweeps visibly across large flat
            // surfaces as the camera orbits — disable it on the world mesh.
            mats[i].SetFloat("_RimIntensity", 0f);
            // Shadow-band softness: lower = sharper shadows; higher = softer but
            // reduces the visible flicker at shadow-cascade boundary crossings.
            mats[i].SetFloat("_CelSmoothness", bakedCelSmoothness);
        }
        mr.sharedMaterials = mats;
    }

    // ---- Public API ----

    public int BlockCount         => blocks.Count;
    public int FoliageZoneCount   => foliageZones.Count;
    public int WaterfallZoneCount => waterfallZones.Count;

    private int _maxUsedLayerCache;
    private int _maxUsedLayerVersion = -1;

    public int MaxUsedLayer
    {
        get
        {
            // O(n) scan cached per edit — editor windows read this every repaint.
            if (_maxUsedLayerVersion != EditVersion)
            {
                int m = 0;
                foreach (var b in blocks) if (b.pos.y > m) m = b.pos.y;
                _maxUsedLayerCache   = m;
                _maxUsedLayerVersion = EditVersion;
            }
            return _maxUsedLayerCache;
        }
    }

    public bool HasBlock(Vector3Int pos) => blockMap.ContainsKey(pos);

    public int GetBlockType(Vector3Int pos) =>
        blockMap.TryGetValue(pos, out int t) ? t : -1;

    public int GetBlockVariant(Vector3Int pos) =>
        variantMap.TryGetValue(pos, out int v) ? v : -1;

    public void PlaceBlock(Vector3Int pos, int typeIndex, int variantIndex = -1)
    {
        if (blockIndex.TryGetValue(pos, out int i))
        {
            blocks[i].typeIndex    = typeIndex;
            blocks[i].variantIndex = variantIndex;
        }
        else
        {
            blockIndex[pos] = blocks.Count;
            blocks.Add(new BlockEntry(pos, typeIndex, variantIndex));
        }
        blockMap[pos]   = typeIndex;
        variantMap[pos] = variantIndex;
        EditVersion++;
    }

    public void RemoveBlock(Vector3Int pos)
    {
        if (!blockIndex.TryGetValue(pos, out int i)) return;
        // Swap-remove keeps removal O(1); list order doesn't matter for baking.
        int last = blocks.Count - 1;
        if (i != last)
        {
            blocks[i] = blocks[last];
            blockIndex[blocks[i].pos] = i;
        }
        blocks.RemoveAt(last);
        blockIndex.Remove(pos);
        blockMap.Remove(pos);
        variantMap.Remove(pos);
        EditVersion++;
    }

    // ---- Editor tooling access (read-only enumeration, no copies) ----

    public IEnumerable<(Vector3Int pos, int typeIndex, int variantIndex)> AllBlocks
    {
        get { foreach (var b in blocks) yield return (b.pos, b.typeIndex, b.variantIndex); }
    }

    public IEnumerable<(Vector3Int pos, int typeMask)> AllFoliageZones
    {
        get { foreach (var z in foliageZones) yield return (z.pos, z.typeMask); }
    }

    public IEnumerable<Vector3Int> AllWaterfallZones
    {
        get { foreach (var z in waterfallZones) yield return z.pos; }
    }

    public bool HasFoliageZone(Vector3Int pos)     => foliageZoneSet.Contains(pos);
    public int  GetFoliageZoneMask(Vector3Int pos) =>
        foliageZoneTypes.TryGetValue(pos, out int m) ? m : 1;

    public void PlaceFoliageZone(Vector3Int pos, int typeMask = 1)
    {
        int mask = typeMask == 0 ? 1 : typeMask;   // 0 → first type for safety
        if (foliageZoneIndex.TryGetValue(pos, out int i))
        {
            foliageZones[i].typeMask = mask;
        }
        else
        {
            foliageZoneIndex[pos] = foliageZones.Count;
            foliageZones.Add(new FoliageZoneEntry(pos, mask));
            foliageZoneSet.Add(pos);
        }
        foliageZoneTypes[pos] = mask;
        EditVersion++;
    }

    public void RemoveFoliageZone(Vector3Int pos)
    {
        if (!foliageZoneIndex.TryGetValue(pos, out int i)) return;
        int last = foliageZones.Count - 1;
        if (i != last)
        {
            foliageZones[i] = foliageZones[last];
            foliageZoneIndex[foliageZones[i].pos] = i;
        }
        foliageZones.RemoveAt(last);
        foliageZoneIndex.Remove(pos);
        foliageZoneSet.Remove(pos);
        foliageZoneTypes.Remove(pos);
        EditVersion++;
    }

    // ---- Prop layer (separate from blocks: painted ON TOP, never merged) ----

    public int PropCount => props.Count;

    public bool HasProp(Vector3Int pos) => propMap.ContainsKey(pos);

    public int GetPropType(Vector3Int pos) =>
        propMap.TryGetValue(pos, out int t) ? t : -1;

    public IEnumerable<(Vector3Int pos, int typeIndex)> AllProps
    {
        get { foreach (var p in props) yield return (p.pos, p.typeIndex); }
    }

    public void PlaceProp(Vector3Int pos, int typeIndex)
    {
        if (propIndex.TryGetValue(pos, out int i))
        {
            props[i].typeIndex = typeIndex;
        }
        else
        {
            propIndex[pos] = props.Count;
            props.Add(new PropEntry(pos, typeIndex));
        }
        propMap[pos] = typeIndex;
        EditVersion++;
    }

    public void RemoveProp(Vector3Int pos)
    {
        if (!propIndex.TryGetValue(pos, out int i)) return;
        int last = props.Count - 1;
        if (i != last)
        {
            props[i] = props[last];
            propIndex[props[i].pos] = i;
        }
        props.RemoveAt(last);
        propIndex.Remove(pos);
        propMap.Remove(pos);
        EditVersion++;
    }

    private void RebuildPropMap()
    {
        propMap.Clear();
        propIndex.Clear();
        for (int i = 0; i < props.Count; i++)
        {
            propMap[props[i].pos]   = props[i].typeIndex;
            propIndex[props[i].pos] = i;
        }
    }

    public void ClearFoliageZones()
    {
        foliageZones.Clear();
        foliageZoneSet.Clear();
        foliageZoneTypes.Clear();
        foliageZoneIndex.Clear();
        EditVersion++;
        Debug.Log("[WorldPainter] Foliage zones cleared.");
    }

    // ---- Waterfall Zone API ----

    public bool HasWaterfallZone(Vector3Int pos) => waterfallZoneSet.Contains(pos);

    public void PlaceWaterfallZone(Vector3Int pos)
    {
        if (waterfallZoneSet.Contains(pos)) return;
        waterfallZoneIndex[pos] = waterfallZones.Count;
        waterfallZones.Add(new WaterfallZoneEntry(pos));
        waterfallZoneSet.Add(pos);
        EditVersion++;
    }

    public void RemoveWaterfallZone(Vector3Int pos)
    {
        if (!waterfallZoneIndex.TryGetValue(pos, out int i)) return;
        int last = waterfallZones.Count - 1;
        if (i != last)
        {
            waterfallZones[i] = waterfallZones[last];
            waterfallZoneIndex[waterfallZones[i].pos] = i;
        }
        waterfallZones.RemoveAt(last);
        waterfallZoneIndex.Remove(pos);
        waterfallZoneSet.Remove(pos);
        EditVersion++;
    }

    public void ClearWaterfallZones()
    {
        waterfallZones.Clear();
        waterfallZoneSet.Clear();
        waterfallZoneIndex.Clear();
        EditVersion++;
        Debug.Log("[WorldPainter] Waterfall zones cleared.");
    }

    public void Clear()
    {
        blocks.Clear();
        blockMap.Clear();
        blockIndex.Clear();
        variantMap.Clear();
        ClearFoliageZones();
        ClearWaterfallZones();
        EditVersion++;
        DestroyBakedChild();
        DestroyWaterfallChildren();
        DestroyFoliageChildren();
        Debug.Log("[WorldPainter] Cleared.");
    }

    /// <summary>Destroys generated foliage children (FoliageArea_* and BakedFoliage).</summary>
    public void DestroyFoliageChildren()
    {
        var baked = transform.Find("BakedFoliage");
        if (baked != null) DestroyImmediate(baked.gameObject);
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch.name.StartsWith("FoliageArea_"))
                DestroyImmediate(ch.gameObject);
        }
        _foliageRenderers = new FoliageArea[0];
    }

    // ---- Cardinal directions for face culling ----

    private static readonly Vector3Int[] Cardinals =
    {
        new Vector3Int( 1,  0,  0), new Vector3Int(-1,  0,  0),
        new Vector3Int( 0,  1,  0), new Vector3Int( 0, -1,  0),
        new Vector3Int( 0,  0,  1), new Vector3Int( 0,  0, -1),
    };

    // ---- Chunk-based Baking ----

    /// <summary>
    /// Manages chunked geometry buffers for baking. Divides the world into 32x32 grid chunks
    /// and maintains separate mesh data per chunk to enable efficient streaming/culling.
    /// </summary>
    private class ChunkedBake
    {
        public int chunkSize = 32;

        // Chunk grid anchor — set to the map's minimum block coordinate so a map
        // that fits within chunkSize lands in ONE chunk regardless of where it
        // sits in the world (fixed multiples of chunkSize would split it).
        public int originX, originZ;

        [System.Serializable]
        public class ChunkData
        {
            public List<Vector3> verts    = new();
            public List<Vector3> normals  = new();
            public List<Vector2> uvs      = new();
            public Dictionary<Material, int> matToSub = new();
            public List<List<int>> tris   = new();
        }

        private Dictionary<(int cx, int cz), ChunkData> chunks = new();

        /// <summary>
        /// Gets or creates chunk data for the grid position (x, z).
        /// Automatically handles chunk boundaries via chunkSize.
        /// </summary>
        public ChunkData For(int x, int z)
        {
            int cx = Mathf.FloorToInt((float)(x - originX) / chunkSize);
            int cz = Mathf.FloorToInt((float)(z - originZ) / chunkSize);
            var key = (cx, cz);

            if (!chunks.TryGetValue(key, out var chunk))
            {
                chunk = new ChunkData();
                chunks[key] = chunk;
            }

            return chunk;
        }

        /// <summary>
        /// Registers a material in the specified chunk, returning its submesh index.
        /// Materials are tracked per-chunk to allow independent mesh building.
        /// </summary>
        public int RegisterMaterial(ChunkData chunk, Material mat, List<Material> globalMats)
        {
            if (!chunk.matToSub.TryGetValue(mat, out int idx))
            {
                idx = chunk.tris.Count;
                chunk.matToSub[mat] = idx;
                chunk.tris.Add(new List<int>());

                // Track globally so we don't lose material order
                if (!globalMats.Contains(mat))
                    globalMats.Add(mat);
            }
            return idx;
        }

        /// <summary>
        /// Creates one GameObject per chunk with its merged mesh, under the baked parent.
        /// Returns the total number of GameObjects created.
        /// Note: Caller should apply material overrides using ApplyBakedRendererProperties()
        /// after calling this method.
        /// </summary>
        /// <param name="layer">Layer for the generated chunk objects. Pass -1 to leave
        /// them on the default layer. The visual chunks need the same layer as the
        /// baked colliders, or raycasts that filter on it miss the ground.</param>
        public int CreateChunkObjects(Transform parent, string prefix, List<Material> allMats, string tag = null, int layer = -1)
        {
            int count = 0;

            foreach (var kvp in chunks)
            {
                var (cx, cz) = kvp.Key;
                var chunk = kvp.Value;

                if (chunk.verts.Count == 0) continue;

                // Build mesh for this chunk
                var mesh = new Mesh { name = $"{prefix}_Baked_{cx}_{cz}" };
                mesh.indexFormat = chunk.verts.Count > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;

                mesh.SetVertices(chunk.verts);

                if (chunk.uvs.Count == chunk.verts.Count)
                    mesh.SetUVs(0, chunk.uvs);

                mesh.subMeshCount = chunk.tris.Count;
                for (int i = 0; i < chunk.tris.Count; i++)
                    mesh.SetTriangles(chunk.tris[i], i);

                if (chunk.normals.Count == chunk.verts.Count)
                    mesh.SetNormals(chunk.normals);
                else
                    mesh.RecalculateNormals();

                mesh.RecalculateBounds();

                // Bake smooth normals
                WorldPainter.BakeSmoothNormalsToMesh(mesh);

                // Save mesh asset
#if UNITY_EDITOR
                string groundDir = "Assets/Models/BakedMeshes/Ground";
                string groundPath = groundDir + $"/{prefix}_Baked_{cx}_{cz}.asset";
                if (!System.IO.Directory.Exists(Application.dataPath + "/Models/BakedMeshes/Ground"))
                    System.IO.Directory.CreateDirectory(Application.dataPath + "/Models/BakedMeshes/Ground");

                var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(groundPath);
                if (existing != null)
                {
                    existing.Clear();
                    UnityEditor.EditorUtility.CopySerialized(mesh, existing);
                    mesh = existing;
                }
                else
                {
                    UnityEditor.AssetDatabase.CreateAsset(mesh, groundPath);
                }
#endif

                // Create GameObject for this chunk
                var chunkGo = new GameObject($"Baked_{cx}_{cz}");
                chunkGo.transform.SetParent(parent, false);
                chunkGo.tag = tag ?? "";
                if (layer >= 0) chunkGo.layer = layer;
                chunkGo.AddComponent<MeshFilter>().sharedMesh = mesh;

                var mr = chunkGo.AddComponent<MeshRenderer>();

                // Assign materials in the order they appear in this chunk
                var chunkMats = new List<Material>();
                for (int i = 0; i < chunk.tris.Count; i++)
                {
                    // Find which material corresponds to submesh i
                    foreach (var matKvp in chunk.matToSub)
                    {
                        if (matKvp.Value == i)
                        {
                            chunkMats.Add(matKvp.Key);
                            break;
                        }
                    }
                }
                mr.sharedMaterials = chunkMats.ToArray();

                count++;
            }

#if UNITY_EDITOR
            // Single flush after all chunk assets are written — calling SaveAssets
            // per chunk makes large bakes take minutes instead of seconds.
            if (count > 0)
                UnityEditor.AssetDatabase.SaveAssets();
#endif

            return count;
        }

        /// <summary>
        /// Returns all chunks that intersect with the given set of grid positions.
        /// Useful for gathering which chunks need colliders.
        /// </summary>
        public Dictionary<(int cx, int cz), HashSet<Vector3Int>> GetChunkPositions(HashSet<Vector3Int> positions)
        {
            var result = new Dictionary<(int cx, int cz), HashSet<Vector3Int>>();

            foreach (var pos in positions)
            {
                int cx = Mathf.FloorToInt((float)(pos.x - originX) / chunkSize);
                int cz = Mathf.FloorToInt((float)(pos.z - originZ) / chunkSize);
                var key = (cx, cz);

                if (!result.TryGetValue(key, out var set))
                {
                    set = new HashSet<Vector3Int>();
                    result[key] = set;
                }
                set.Add(pos);
            }

            return result;
        }
    }

    // ---- Bake ----

    public void Bake()
    {
        if (palette == null || palette.Length == 0)
        {
            Debug.LogError("[WorldPainter] Palette is empty."); return;
        }

        // Layer indices are per project: a scene copied from another project can point
        // Bake Layer at an index that does not exist here, and everything lands on a
        // nameless layer where raycast masks silently miss it.
        if (string.IsNullOrEmpty(LayerMask.LayerToName(bakeLayer)))
        {
            int ground = LayerMask.NameToLayer("Ground");
            Debug.LogWarning($"[WorldPainter] Bake Layer is {bakeLayer}, which has no name in this " +
                             $"project — baked geometry would end up on an unnamed layer." +
                             (ground >= 0
                                ? $" Using \"Ground\" (layer {ground}) instead; set Bake Layer to {ground} to silence this."
                                : " Create the layer you want and set Bake Layer to its index."));
            if (ground >= 0) bakeLayer = ground;
        }
        if (blocks.Count == 0)
        {
            Debug.LogWarning("[WorldPainter] No blocks placed."); return;
        }

        DestroyBakedChild();

        // Anchor the chunk grid to the map's minimum corner so chunk splits
        // depend on map size, not on where the map sits in the world.
        int minBX = int.MaxValue, minBZ = int.MaxValue;
        foreach (var b in blocks)
        {
            if (b.pos.x < minBX) minBX = b.pos.x;
            if (b.pos.z < minBZ) minBZ = b.pos.z;
        }

        // Initialize chunked baking system
        var chunked = new ChunkedBake { chunkSize = chunkSize, originX = minBX, originZ = minBZ };
        var uniqueMats = new List<Material>();

        int totalTris = 0, culledTris = 0, wallBlockCount = 0;
        var visualBlockSet      = new HashSet<Vector3Int>();
        var walkableBlockSet    = new HashSet<Vector3Int>();
        var notWalkableBlockSet = new HashSet<Vector3Int>();
        float gs = gridSize;

        // Blocks marked excludeFromBake are collected here and instantiated as real
        // child GameObjects after the mesh pass — they keep their original components.
        var excludedBlocks = new List<(GameObject prefab, Vector3 localPos, float scale)>();

        // Cache per-prefab bounding boxes so we only compute them once per type.
        // The fitScale normalises any mesh size to exactly one grid cell.
        var boundsCache = new Dictionary<GameObject, (Bounds bounds, float fitScale)>();

        foreach (var block in blocks)
        {
            int ti = block.typeIndex;
            if (ti < 0 || ti >= palette.Length || palette[ti]?.prefab == null) continue;

            var  bt      = palette[ti];
            GameObject prefab = null;
            float      autoYOff = 0f;
            float      autoXOff = 0f;

            // Check if a manual variant is selected
            if (block.variantIndex >= 0 && bt.variants != null && block.variantIndex < bt.variants.Length
                && bt.variants[block.variantIndex]?.prefab != null)
            {
                prefab = bt.variants[block.variantIndex].prefab;
                autoYOff = bt.variants[block.variantIndex].yOffset;
                autoXOff = bt.variants[block.variantIndex].xOffset;
            }
            else if (bt.isAutoTile)
            {
                // Auto-tile: pick the variant whose slot matches the neighbourhood.
                GameObject resolved = null;
                (resolved, autoYOff, autoXOff) = ResolveAutoTile(block.pos, ti, bt);
                prefab = (resolved != null) ? resolved : bt.prefab;
            }
            else
            {
                prefab = bt.prefab;
            }

            if (prefab == null) continue;

            float yOffset = bt.yOffset + autoYOff;
            bool  noCull  = bt.noCull;

            // Local-space origin of this block.  Y adds the per-type and per-slot offsets;
            // X adds the per-slot lateral nudge for variants with off-centre pivots.
            Vector3 blockOrigin = new(
                block.pos.x * gs + autoXOff,
                block.pos.y * gs + yOffset,
                block.pos.z * gs);

            // Compute (or retrieve) this prefab's bounding box in root-local space,
            // then derive a uniform scale that maps the largest dimension to exactly gs.
            // This prevents blocks from overlapping (and Z-fighting) when the source
            // mesh is larger or smaller than one grid unit.
            if (!boundsCache.TryGetValue(prefab, out var normData))
            {
                Bounds b = ComputePrefabBounds(prefab);
                float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
                float fit = maxDim > 0.001f ? gs / maxDim : gs;
                normData = (b, fit);
                boundsCache[prefab] = normData;
            }
            Bounds prefabBounds = normData.bounds;
            float   fitScale    = normData.fitScale;

            // Excluded blocks skip mesh merging and get instantiated as-is instead.
            if (bt.excludeFromBake)
            {
                Vector3 localPos = blockOrigin - prefabBounds.center * fitScale;
                excludedBlocks.Add((prefab, localPos, fitScale));
                continue;
            }

            // Wall blocks are merged into a single MeshCollider (BakedWalls) — no visual output.
            if (bt.mergeCollider) { wallBlockCount++; continue; }

            visualBlockSet.Add(block.pos);
            if (bt.walkable) walkableBlockSet.Add(block.pos);
            else             notWalkableBlockSet.Add(block.pos);

            // Walk every MeshFilter in the prefab
            var filters = prefab.GetComponentsInChildren<MeshFilter>(false);
            foreach (var mf in filters)
            {
                Mesh src = mf.sharedMesh;
                if (src == null) continue;

                var srcMR = mf.GetComponent<MeshRenderer>();
                if (srcMR == null) continue;

                // Child transform relative to the prefab ROOT (not world).
                // Using mf.localToWorldMatrix alone would include the root's own
                // world transform (e.g. FBX axis-correction rotation/scale) and
                // cause double-scaling.
                Matrix4x4 childToRoot = prefab.transform.worldToLocalMatrix
                                        * mf.transform.localToWorldMatrix;

                // Scale the mesh so its largest dimension equals gs, then centre
                // it at blockOrigin.  This maps any prefab size to exactly one cell.
                Matrix4x4 toLocal = Matrix4x4.TRS(
                    blockOrigin - prefabBounds.center * fitScale,
                    Quaternion.identity,
                    Vector3.one * fitScale)
                    * childToRoot;

                Vector3[]  srcV  = src.vertices;
                Vector3[]  srcN  = src.normals;
                Vector2[]  srcUV = src.uv;
                Material[] mats  = srcMR.sharedMaterials;

                // Inverse-transpose of the vertex transform matrix.
                // Needed so normals remain perpendicular to the surface when the
                // matrix contains non-uniform scale (e.g. from childToRoot).
                Matrix4x4 normalMatrix = toLocal.inverse.transpose;

                for (int sub = 0; sub < src.subMeshCount; sub++)
                {
                    Material mat = (mats != null && sub < mats.Length) ? mats[sub] : null;
                    if (mat == null) continue;

                    int[] subTris = src.GetTriangles(sub);

                    for (int k = 0; k < subTris.Length; k += 3)
                    {
                        totalTris++;
                        int i0 = subTris[k], i1 = subTris[k + 1], i2 = subTris[k + 2];

                        // World-space vertices (used only for normal computation)
                        Vector3 wv0 = toLocal.MultiplyPoint3x4(srcV[i0]);
                        Vector3 wv1 = toLocal.MultiplyPoint3x4(srcV[i1]);
                        Vector3 wv2 = toLocal.MultiplyPoint3x4(srcV[i2]);

                        // Geometric normal → find dominant cardinal direction
                        Vector3 geoN = Vector3.Cross(wv1 - wv0, wv2 - wv0).normalized;
                        Vector3Int dom = DominantDirection(geoN, out float strength);

                        // Cull if a neighbour block occupies that side — but only when
                        // BOTH the current block and the neighbour are solid (noCull = false).
                        // If either side opts out, both faces are kept so open structures
                        // (bridges, fences, arches) remain visually correct.
                        if (strength > 0.5f && HasBlock(block.pos + dom) && !noCull)
                        {
                            int  nbrType   = GetBlockType(block.pos + dom);
                            bool nbrNoCull = nbrType >= 0 && nbrType < palette.Length
                                            && (palette[nbrType]?.noCull ?? false);
                            if (!nbrNoCull) { culledTris++; continue; }
                        }

                        // Get the chunk for this block and add vertices to it
                        var chunk = chunked.For(block.pos.x, block.pos.z);
                        int baseVert = chunk.verts.Count;
                        chunk.verts.Add(wv0); chunk.verts.Add(wv1); chunk.verts.Add(wv2);

                        if (srcN.Length > i2)
                        {
                            chunk.normals.Add(normalMatrix.MultiplyVector(srcN[i0]).normalized);
                            chunk.normals.Add(normalMatrix.MultiplyVector(srcN[i1]).normalized);
                            chunk.normals.Add(normalMatrix.MultiplyVector(srcN[i2]).normalized);
                        }

                        if (srcUV.Length > i2)
                        {
                            chunk.uvs.Add(srcUV[i0]); chunk.uvs.Add(srcUV[i1]); chunk.uvs.Add(srcUV[i2]);
                        }

                        // Register material in this chunk
                        int subIdx = chunked.RegisterMaterial(chunk, mat, uniqueMats);

                        chunk.tris[subIdx].Add(baseVert);
                        chunk.tris[subIdx].Add(baseVert + 1);
                        chunk.tris[subIdx].Add(baseVert + 2);
                    }
                }
            }
        }

        // Instantiate excluded blocks under a dedicated container so they stay selectable
        // and keep all their own components (colliders, scripts, particles, etc.).
        if (excludedBlocks.Count > 0)
        {
            var container = new GameObject("BakedInstances");
            container.transform.SetParent(transform, false);
            container.layer = bakeLayer;
            foreach (var (instPrefab, localPos, scale) in excludedBlocks)
            {
                var instance = Instantiate(instPrefab, container.transform);
                instance.transform.localPosition = localPos;
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale    = Vector3.one * scale;
            }
        }

        // ---- Wall collider strips ----
        // Greedy cuboid expansion: sorted blocks ensure the outermost corner is always
        // picked first, so a full rectangular perimeter collapses to exactly 4 BoxColliders.
        if (wallBlockCount > 0)
        {
            var wallSet = new HashSet<Vector3Int>();
            foreach (var block in blocks)
            {
                int wti = block.typeIndex;
                if (wti >= 0 && wti < palette.Length && palette[wti]?.mergeCollider == true)
                    wallSet.Add(block.pos);
            }

            var wallContainer = new GameObject("BakedWalls");
            wallContainer.transform.SetParent(transform, false);
            wallContainer.layer = bakeLayer;

            // Sort (X → Y → Z) so outer walls are always discovered before inner faces.
            var sortedWalls = new List<Vector3Int>(wallSet);
            sortedWalls.Sort((a, b) =>
                a.x != b.x ? a.x.CompareTo(b.x) :
                a.y != b.y ? a.y.CompareTo(b.y) :
                             a.z.CompareTo(b.z));

            var remaining = new HashSet<Vector3Int>(wallSet);
            int wallBoxCount = 0;

            while (remaining.Count > 0)
            {
                // Pick the first un-consumed block in sorted order.
                Vector3Int s = default;
                foreach (var p in sortedWalls)
                    if (remaining.Contains(p)) { s = p; break; }

                // Expand along X as far as possible.
                int xMax = s.x;
                while (remaining.Contains(new Vector3Int(xMax + 1, s.y, s.z))) xMax++;

                // Expand along Z: every X in [s.x..xMax] must have the candidate Z.
                int zMax = s.z;
                while (true)
                {
                    bool ok = true;
                    for (int x = s.x; x <= xMax && ok; x++)
                        if (!remaining.Contains(new Vector3Int(x, s.y, zMax + 1))) ok = false;
                    if (!ok) break;
                    zMax++;
                }

                // Expand along Y: every (X,Z) in the slab must have the candidate Y.
                int yMax = s.y;
                while (true)
                {
                    bool ok = true;
                    for (int x = s.x; x <= xMax && ok; x++)
                    for (int z = s.z; z <= zMax && ok; z++)
                        if (!remaining.Contains(new Vector3Int(x, yMax + 1, z))) ok = false;
                    if (!ok) break;
                    yMax++;
                }

                var go = new GameObject("WallBox");
                go.transform.SetParent(wallContainer.transform, false);
                go.layer = bakeLayer;
                go.transform.localPosition = new Vector3(
                    (s.x + xMax) * 0.5f * gs,
                    (s.y + yMax) * 0.5f * gs,
                    (s.z + zMax) * 0.5f * gs);
                var bc = go.AddComponent<BoxCollider>();
                bc.center = Vector3.zero;
                bc.size   = new Vector3(
                    (xMax - s.x + 1) * gs,
                    (yMax - s.y + 1) * gs,
                    (zMax - s.z + 1) * gs);

                for (int x = s.x; x <= xMax; x++)
                for (int y = s.y; y <= yMax; y++)
                for (int z = s.z; z <= zMax; z++)
                    remaining.Remove(new Vector3Int(x, y, z));

                wallBoxCount++;
            }

            Debug.Log($"[WorldPainter] BakedWalls: {wallBlockCount} wall block(s) → {wallBoxCount} BoxCollider(s).");
        }

        BakePropsLayer();

        // Create chunk GameObjects and colliders
        var bakedParent = new GameObject("Baked");
        bakedParent.transform.SetParent(transform, false);
        bakedParent.layer = bakeLayer;

        int chunkCount = chunked.CreateChunkObjects(bakedParent.transform, BakePrefix, uniqueMats, "Ground", bakeLayer);

        // Apply material overrides to all chunk renderers
        if (chunkCount > 0)
        {
            foreach (Transform chunkTr in bakedParent.transform)
            {
                var mr = chunkTr.GetComponent<MeshRenderer>();
                if (mr != null)
                    ApplyBakedMaterialOverrides(mr);
            }
        }

        if (chunkCount == 0)
        {
            Debug.LogWarning("[WorldPainter] Bake produced no geometry — check prefab assignments.");
            DestroyImmediate(bakedParent);
            return;
        }

        // ---- Per-chunk colliders ----
        // Group visual blocks by chunk and create colliders per chunk
        var visualChunks = chunked.GetChunkPositions(visualBlockSet);
        int totalVisualBoxes = 0;

        foreach (var kvp in visualChunks)
        {
            var (cx, cz) = kvp.Key;
            var positions = kvp.Value;
            var colliderParent = new GameObject($"BakedColliders_{cx}_{cz}");
            colliderParent.transform.SetParent(transform, false);
            colliderParent.layer = bakeLayer;
            colliderParent.tag = "Ground";

            int boxCount = CreateGreedyBoxColliders(colliderParent, positions, gs, "Ground");
            totalVisualBoxes += boxCount;
        }

        if (totalVisualBoxes > 0)
            Debug.Log($"[WorldPainter] BakedColliders: {visualBlockSet.Count} block(s) in {visualChunks.Count} chunk(s) → {totalVisualBoxes} BoxCollider(s).");

        // Per-walkability NavMesh box collider children — invisible to camera,
        // visible to NavMeshSurface. Uses the same greedy cuboid algorithm.
        var walkableChunks = chunked.GetChunkPositions(walkableBlockSet);
        foreach (var kvp in walkableChunks)
        {
            var (cx, cz) = kvp.Key;
            var positions = kvp.Value;
            var navParent = new GameObject($"NavMesh_Walkable_{cx}_{cz}");
            navParent.transform.SetParent(transform, false);
            CreateGreedyBoxColliders(navParent, positions, gs);
        }

        var notWalkableChunks = chunked.GetChunkPositions(notWalkableBlockSet);
        foreach (var kvp in notWalkableChunks)
        {
            var (cx, cz) = kvp.Key;
            var positions = kvp.Value;
            var navParent = new GameObject($"NavMesh_NotWalkable_{cx}_{cz}");
            navParent.transform.SetParent(transform, false);
            CreateGreedyBoxColliders(navParent, positions, gs);
        }

        // Exclude visual mesh and physics colliders from NavMesh baking — the per-walkability
        // children above are the authoritative source for the NavMeshSurface.
#if UNITY_AI_NAVIGATION
        for (int i = 0; i < bakedParent.transform.childCount; i++)
        {
            var chunk = bakedParent.transform.GetChild(i);
            chunk.gameObject.AddComponent<Unity.AI.Navigation.NavMeshModifier>().ignoreFromBuild = true;
        }

        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (child.name.StartsWith("BakedColliders_"))
            {
                child.gameObject.AddComponent<Unity.AI.Navigation.NavMeshModifier>().ignoreFromBuild = true;
            }
            else if (child.name.StartsWith("NavMesh_Walkable_"))
            {
                var m = child.gameObject.AddComponent<Unity.AI.Navigation.NavMeshModifier>();
                m.overrideArea = true;
                m.area = 0;
            }
            else if (child.name.StartsWith("NavMesh_NotWalkable_"))
            {
                var m = child.gameObject.AddComponent<Unity.AI.Navigation.NavMeshModifier>();
                m.overrideArea = true;
                m.area = 1;
            }
        }
#endif

        string excludedNote = excludedBlocks.Count > 0
            ? $", {excludedBlocks.Count} excluded block(s) instantiated"
            : "";
        Debug.Log($"[WorldPainter] Baked {blocks.Count} blocks into {chunkCount} chunk(s) — " +
                  $"{totalTris - culledTris}/{totalTris} tris kept, " +
                  $"{uniqueMats.Count} material(s){excludedNote}.");
    }

    // ---- Foliage Generation ----

    /// <summary>
    /// For each Y-layer that has painted foliage zones, creates a FoliageArea child
    /// GameObject sized to cover those zones and calls Generate() on it.
    /// Requires the scene to have a baked MeshCollider (from Bake()) for raycasts to land.
    /// </summary>
    public void GenerateFoliage()
    {
        if (foliageSettings == null || foliageSettings.foliageTypes == null
            || foliageSettings.foliageTypes.Length == 0)
        {
            Debug.LogError("[WorldPainter] Add Foliage Types in Foliage Settings before generating.");
            return;
        }
        if (foliageZones.Count == 0)
        {
            Debug.LogWarning("[WorldPainter] No foliage zones painted."); return;
        }

        // Destroy stale baked foliage — instanced rendering takes over until re-baked.
        var bakedFoliageOld = transform.Find("BakedFoliage");
        if (bakedFoliageOld != null) DestroyImmediate(bakedFoliageOld.gameObject);

        // Destroy previously auto-generated foliage area children
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch.name.StartsWith("FoliageArea_"))
                DestroyImmediate(ch.gameObject);
        }

        // Create one FoliageArea per painted zone tile — each area is exactly one
        // grid cell in size so position and extent match what was painted precisely.
        float gs = gridSize;
        Physics.SyncTransforms();

        // ---- Preflight diagnostic ----
        // Check the baked child exists; cast a single test ray above the first zone to
        // verify the physics engine can see the surface before spawning all areas.
        var bakedParent = transform.Find("Baked");
        bool hasColliders = false;
        for (int i = 0; i < transform.childCount && !hasColliders; i++)
        {
            var ch = transform.GetChild(i);
            if (ch.name.StartsWith("BakedColliders_")) { hasColliders = true; break; }
        }

        if (bakedParent == null || !hasColliders)
        {
            Debug.LogWarning("[WorldPainter] No baked colliders found. " +
                             "Run 'Create & Bake' first so raycasts have a surface to land on.");
        }
        else if (foliageZones.Count > 0)
        {
            var firstZone = foliageZones[0];
            float testX  = transform.position.x + firstZone.pos.x * gs;
            float testZ  = transform.position.z + firstZone.pos.z * gs;
            float testY  = transform.position.y + firstZone.pos.y * gs + gs * 10f;
            var testRay  = new Ray(new Vector3(testX, testY, testZ), Vector3.down);
            if (Physics.Raycast(testRay, out RaycastHit testHit, gs * 20f, foliageSettings.surfaceLayers))
                Debug.Log($"[WorldPainter] Preflight raycast HIT '{testHit.collider.name}' " +
                          $"at Y={testHit.point.y:F2} (layer {testHit.collider.gameObject.layer}) — physics OK.");
            else
                Debug.LogWarning($"[WorldPainter] Preflight raycast MISSED at " +
                                 $"X={testX:F1} Z={testZ:F1} from Y={testY:F1} down {gs*20f:F1} units. " +
                                 $"Surface Layers mask={foliageSettings.surfaceLayers.value}. " +
                                 $"Baked chunks exist. " +
                                 $"Make sure the baked colliders' layer is included in Surface Layers " +
                                 $"and the blocks at zone ({firstZone.pos.x},{firstZone.pos.y},{firstZone.pos.z}) exist.");
        }

        for (int idx = 0; idx < foliageZones.Count; idx++)
        {
            var fz = foliageZones[idx];

            // Block centers sit at pos * gs in the WorldPainter's local space.
            float cx = fz.pos.x * gs;
            float cz = fz.pos.z * gs;
            float cy = fz.pos.y * gs + foliageSettings.volumeHeight;

            var go = new GameObject($"FoliageArea_{fz.pos.x}_{fz.pos.y}_{fz.pos.z}");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(cx, cy, cz);

            var fa  = go.AddComponent<FoliageArea>();
            fa.size = new Vector3(gs, foliageSettings.volumeHeight * 2f, gs);
            fa.seed           = foliageSettings.seed + idx;
            fa.yOffset        = foliageSettings.yOffset;
            fa.renderDistance = foliageSettings.renderDistance;
            fa.surfaceLayers  = foliageSettings.surfaceLayers;

            // Build the type list from the zone's bitmask — each set bit includes
            // the corresponding FoliageType from the global pool.
            int mask = fz.typeMask == 0 ? 1 : fz.typeMask;   // 0 → first type (backward compat)
            if (foliageSettings.foliageTypes != null && foliageSettings.foliageTypes.Length > 0)
            {
                var picked = new List<FoliageType>();
                for (int bit = 0; bit < foliageSettings.foliageTypes.Length && bit < 31; bit++)
                    if ((mask & (1 << bit)) != 0)
                        picked.Add(foliageSettings.foliageTypes[bit]);
                fa.foliageTypes = picked.Count > 0 ? picked.ToArray() : foliageSettings.foliageTypes;
            }
            else
            {
                fa.foliageTypes = foliageSettings.foliageTypes;
            }

            fa.managedExternally = true;
            fa.Generate();
        }

        // Cache the new areas so LateUpdate can render them without GetComponentsInChildren each frame.
        _foliageRenderers = GetComponentsInChildren<FoliageArea>();

        Debug.Log($"[WorldPainter] Generated foliage for {foliageZones.Count} zone(s).");
    }

    // ---- Foliage Bake ----

    /// <summary>
    /// Merges all FoliageArea instance data into a single static mesh per material and
    /// creates a "BakedFoliage" child GameObject — eliminating all per-frame
    /// DrawMeshInstanced overhead. The mesh is saved to
    /// Assets/Models/BakedMeshes/WorldPainter_Baked_Foliage.asset.
    ///
    /// After baking the instanced rendering path is disabled automatically.
    /// Call GenerateFoliage() again to rebuild from scratch (this destroys the baked mesh).
    /// </summary>
    public void BakeFoliage()
    {
        var areas = GetComponentsInChildren<FoliageArea>();
        if (areas.Length == 0)
        {
            Debug.LogWarning("[WorldPainter] No FoliageArea children found. Run Generate Foliage first.");
            return;
        }

        // Destroy any previous baked foliage child
        var oldBaked = transform.Find("BakedFoliage");
        if (oldBaked != null) DestroyImmediate(oldBaked.gameObject);

        var uniqueMats = new List<Material>();
        var matToSub   = new Dictionary<Material, int>();
        var allTris    = new List<List<int>>();
        var allVerts   = new List<Vector3>();
        var allNormals = new List<Vector3>();
        var allUVs     = new List<Vector2>();
        int totalInstances = 0;

        foreach (var fa in areas)
        {
            if (fa.foliageTypes == null) continue;

            for (int t = 0; t < fa.foliageTypes.Length; t++)
            {
                var foliage = fa.foliageTypes[t];
                if (foliage?.mesh == null || foliage.materials == null || foliage.materials.Length == 0)
                    continue;

                fa.GetInstancesForBake(t, out Vector3[] positions, out float[] rotations, out Vector3[] scales);
                if (positions.Length == 0) continue;

                Vector3[] srcV  = foliage.mesh.vertices;
                Vector3[] srcN  = foliage.mesh.normals;
                Vector2[] srcUV = foliage.mesh.uv;

                // Pre-fetch triangle lists per sub-mesh (avoid repeated GetTriangles inside the loop)
                int subCount = Mathf.Min(foliage.mesh.subMeshCount, foliage.materials.Length);
                int[][] subTrisArr = new int[subCount][];
                for (int s = 0; s < subCount; s++)
                    subTrisArr[s] = foliage.mesh.GetTriangles(s);

                // Resolve material→sub-mesh indices once per foliage type
                int[] matSubIdx = new int[subCount];
                for (int s = 0; s < subCount; s++)
                {
                    Material mat = foliage.materials[s];
                    if (mat == null) { matSubIdx[s] = -1; continue; }
                    if (!matToSub.TryGetValue(mat, out int idx))
                    {
                        idx = uniqueMats.Count;
                        matToSub[mat] = idx;
                        uniqueMats.Add(mat);
                        allTris.Add(new List<int>());
                    }
                    matSubIdx[s] = idx;
                }

                // savedInstances positions are world-space raycast hits.
                // To store vertices in the BakedFoliage child's local space (== WorldPainter
                // local space, since the child sits at localPosition=zero), combine each
                // instance's world-space TRS with the world→local matrix of this transform.
                Matrix4x4 worldToLocal = transform.worldToLocalMatrix;

                for (int i = 0; i < positions.Length; i++)
                {
                    totalInstances++;
                    Quaternion rot      = Quaternion.Euler(0f, rotations[i], 0f);
                    Matrix4x4  trs      = Matrix4x4.TRS(positions[i], rot, scales[i]);
                    // Combined: instance local→world, then world→parent-local
                    Matrix4x4  combined = worldToLocal * trs;
                    Matrix4x4  normM    = combined.inverse.transpose;
                    int        baseV    = allVerts.Count;

                    for (int v = 0; v < srcV.Length; v++)
                    {
                        allVerts.Add(combined.MultiplyPoint3x4(srcV[v]));
                        if (srcN.Length > v)
                            allNormals.Add(normM.MultiplyVector(srcN[v]).normalized);
                        if (srcUV.Length > v)
                            allUVs.Add(srcUV[v]);
                    }

                    for (int s = 0; s < subCount; s++)
                    {
                        int idx = matSubIdx[s];
                        if (idx < 0) continue;
                        int[] tris = subTrisArr[s];
                        for (int k = 0; k < tris.Length; k++)
                            allTris[idx].Add(baseV + tris[k]);
                    }
                }
            }
        }

        if (allVerts.Count == 0)
        {
            Debug.LogWarning("[WorldPainter] BakeFoliage produced no geometry — generate foliage first and ensure instances were placed.");
            return;
        }

        var mesh = new Mesh { name = $"{BakePrefix}_Baked_Foliage" };
        mesh.indexFormat = allVerts.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;

        mesh.SetVertices(allVerts);
        if (allUVs.Count == allVerts.Count) mesh.SetUVs(0, allUVs);
        mesh.subMeshCount = uniqueMats.Count;
        for (int i = 0; i < allTris.Count; i++) mesh.SetTriangles(allTris[i], i);
        if (allNormals.Count == allVerts.Count)
            mesh.SetNormals(allNormals);
        else
            mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Bake smooth normals into the tangent channel for clean outlines.
        BakeSmoothNormalsToMesh(mesh);

#if UNITY_EDITOR
        string foliageDir  = "Assets/Models/BakedMeshes/Foliage";
        string foliagePath = foliageDir + $"/{BakePrefix}_Baked_Foliage.asset";
        if (!System.IO.Directory.Exists(Application.dataPath + "/Models/BakedMeshes/Foliage"))
            System.IO.Directory.CreateDirectory(Application.dataPath + "/Models/BakedMeshes/Foliage");

        var existingMesh = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(foliagePath);
        if (existingMesh != null)
        {
            existingMesh.Clear();
            UnityEditor.EditorUtility.CopySerialized(mesh, existingMesh);
            UnityEditor.AssetDatabase.SaveAssets();
            mesh = existingMesh;
        }
        else
        {
            UnityEditor.AssetDatabase.CreateAsset(mesh, foliagePath);
            UnityEditor.AssetDatabase.SaveAssets();
        }

        // Clean up legacy files from old save locations.
        string oldFoliageRoot  = "Assets/Models/BakedMeshes/WorldPainter_Baked_Foliage.asset";
        string oldFoliageBaked = "Assets/Models/BakedMeshes/Foliage/WorldPainter_Baked_Foliage_Baked.asset";
        if (UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(oldFoliageRoot) != null)
            UnityEditor.AssetDatabase.DeleteAsset(oldFoliageRoot);
        if (UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(oldFoliageBaked) != null)
            UnityEditor.AssetDatabase.DeleteAsset(oldFoliageBaked);
#endif

        var child = new GameObject("BakedFoliage");
        child.transform.SetParent(transform, false);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        child.AddComponent<MeshRenderer>().sharedMaterials = uniqueMats.ToArray();

        // Remove FoliageArea children — the static mesh replaces them entirely.
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch.name.StartsWith("FoliageArea_"))
                DestroyImmediate(ch.gameObject);
        }

        // Disable the per-frame instanced rendering path — static mesh handles it now.
        _foliageRenderers = new FoliageArea[0];

        Debug.Log($"[WorldPainter] Foliage baked: {totalInstances} instances → " +
                  $"{allVerts.Count} verts, {uniqueMats.Count} material(s).");
    }

    // ---- Waterfall Generation ----

    /// <summary>
    /// Generates waterfall meshes from painted waterfall zones. For each Y-layer that
    /// has zones, creates a child GameObject positioned so the shader's positionOS.y=0.5
    /// maps to the layer top. Top faces get normal up; exposed side faces extend down
    /// by waterfallSettings.depth grid units.
    /// </summary>
    public void GenerateWaterfall()
    {
        if (waterfallSettings == null || waterfallSettings.material == null)
        {
            Debug.LogError("[WorldPainter] Assign a waterfall material before generating.");
            return;
        }
        if (waterfallZones.Count == 0)
        {
            Debug.LogWarning("[WorldPainter] No waterfall zones painted.");
            return;
        }

        DestroyWaterfallChildren();

        float gs    = gridSize;
        int   depth = Mathf.Max(1, waterfallSettings.depth);

        // Group zones by Y layer — but only the TOP-most zone of each vertical
        // column produces geometry. Stacked fill-down zones form one volume with
        // one surface, not a sheet per layer.
        var layers = new Dictionary<int, List<Vector3Int>>();
        foreach (var wz in waterfallZones)
        {
            if (waterfallZoneSet.Contains(wz.pos + new Vector3Int(0, 1, 0)))
                continue;   // a zone sits directly above — this cell is interior
            if (!layers.TryGetValue(wz.pos.y, out var list))
            {
                list = new List<Vector3Int>();
                layers[wz.pos.y] = list;
            }
            list.Add(wz.pos);
        }

        foreach (var kvp in layers)
        {
            int layerY = kvp.Key;
            var cells   = kvp.Value;

            var verts   = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris    = new List<int>();

            // The child is positioned so that its local Y=0 is at the top of the layer,
            // and the mesh extends downward. The shader expects positionOS.y in [0..1]
            // range where 0.5 is the top surface. We'll build geometry in object space
            // with the top face at y=0.5 and sides going down from y=0.5 to y=0.5 - sideHeight.

            // Build a HashSet for fast neighbour lookups within this layer
            var cellSet = new HashSet<Vector3Int>(cells);

            foreach (var cell in cells)
            {
                // Sides reach the bottom of this cell's zone column (fill-down),
                // or the configured depth for a single free-floating zone —
                // whichever is deeper.
                int column = 1;
                while (waterfallZoneSet.Contains(new Vector3Int(cell.x, cell.y - column, cell.z)))
                    column++;
                float sideHeight = Mathf.Max(column, depth) * gs;

                // Cell center in local space (relative to the child object)
                float cx = (cell.x - cells[0].x) * gs; // we'll offset the child position later
                float cz = (cell.z - cells[0].z) * gs;

                // We need absolute positions — compute relative to child origin
                float lx = cell.x * gs;
                float lz = cell.z * gs;

                float half = gs * 0.5f;

                // Top face quad (normal up) at y = 0.5
                AddQuad(verts, normals, tris,
                    new Vector3(lx - half, 0.5f, lz - half),
                    new Vector3(lx - half, 0.5f, lz + half),
                    new Vector3(lx + half, 0.5f, lz + half),
                    new Vector3(lx + half, 0.5f, lz - half),
                    Vector3.up);

                // Side faces — only on exposed edges (no waterfall neighbour)
                // +X side
                if (!cellSet.Contains(cell + new Vector3Int(1, 0, 0)))
                {
                    AddQuad(verts, normals, tris,
                        new Vector3(lx + half, 0.5f - sideHeight,  lz + half),
                        new Vector3(lx + half, 0.5f - sideHeight,  lz - half),
                        new Vector3(lx + half, 0.5f,               lz - half),
                        new Vector3(lx + half, 0.5f,               lz + half),
                        Vector3.right);
                }
                // -X side
                if (!cellSet.Contains(cell + new Vector3Int(-1, 0, 0)))
                {
                    AddQuad(verts, normals, tris,
                        new Vector3(lx - half, 0.5f - sideHeight,  lz - half),
                        new Vector3(lx - half, 0.5f - sideHeight,  lz + half),
                        new Vector3(lx - half, 0.5f,               lz + half),
                        new Vector3(lx - half, 0.5f,               lz - half),
                        Vector3.left);
                }
                // +Z side
                if (!cellSet.Contains(cell + new Vector3Int(0, 0, 1)))
                {
                    AddQuad(verts, normals, tris,
                        new Vector3(lx - half, 0.5f - sideHeight,  lz + half),
                        new Vector3(lx + half, 0.5f - sideHeight,  lz + half),
                        new Vector3(lx + half, 0.5f,               lz + half),
                        new Vector3(lx - half, 0.5f,               lz + half),
                        Vector3.forward);
                }
                // -Z side
                if (!cellSet.Contains(cell + new Vector3Int(0, 0, -1)))
                {
                    AddQuad(verts, normals, tris,
                        new Vector3(lx + half, 0.5f - sideHeight,  lz - half),
                        new Vector3(lx - half, 0.5f - sideHeight,  lz - half),
                        new Vector3(lx - half, 0.5f,               lz - half),
                        new Vector3(lx + half, 0.5f,               lz - half),
                        Vector3.back);
                }
            }

            if (verts.Count == 0) continue;

            var mesh = new Mesh { name = $"{BakePrefix}_BakedWaterfall_Y{layerY}" };
            mesh.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

#if UNITY_EDITOR
            string dir  = "Assets/Models/BakedMeshes";
            string path = $"{dir}/{BakePrefix}_BakedWaterfall_Y{layerY}.asset";
            if (!System.IO.Directory.Exists(Application.dataPath + "/Models/BakedMeshes"))
                System.IO.Directory.CreateDirectory(Application.dataPath + "/Models/BakedMeshes");

            var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                existing.Clear();
                UnityEditor.EditorUtility.CopySerialized(mesh, existing);
                UnityEditor.AssetDatabase.SaveAssets();
                mesh = existing;
            }
            else
            {
                UnityEditor.AssetDatabase.CreateAsset(mesh, path);
                UnityEditor.AssetDatabase.SaveAssets();
            }
#endif

            // Position the child so that local Y=0.5 aligns with the top of this layer.
            // Layer top in WorldPainter local space = layerY * gs + gs * 0.5
            // We want child localY such that childLocalY + 0.5 = layerY * gs + gs * 0.5
            // → childLocalY = layerY * gs + gs * 0.5 - 0.5
            float childY = layerY * gs + gs * 0.5f - 0.5f;

            var child = new GameObject($"BakedWaterfall_Y{layerY}");
            child.transform.SetParent(transform, false);
            child.transform.localPosition = new Vector3(0f, childY, 0f);

            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = child.AddComponent<MeshRenderer>();
            mr.sharedMaterial = waterfallSettings.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Box colliders for the top surface (greedy merge like ground)
            var cellPositions = new HashSet<Vector3Int>(cells);
            CreateGreedyBoxColliders($"BakedWaterfall_Colliders_Y{layerY}", cellPositions, gs);
        }

        Debug.Log($"[WorldPainter] Generated waterfall for {waterfallZones.Count} zone(s) across {layers.Count} layer(s).");
    }

    private static void AddQuad(List<Vector3> verts, List<Vector3> normals, List<int> tris,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 normal)
    {
        int b = verts.Count;
        verts.Add(v0); verts.Add(v1); verts.Add(v2); verts.Add(v3);
        normals.Add(normal); normals.Add(normal); normals.Add(normal); normals.Add(normal);
        tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
        tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
    }

    private void DestroyWaterfallChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch.name.StartsWith("BakedWaterfall_"))
                DestroyImmediate(ch.gameObject);
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// Returns the prefab variant from the auto-tile 3×3 grid that best matches
    /// the block's neighbourhood, or null if every grid slot is empty.
    ///
    /// Column role (X axis):
    ///   0 = Left  — same-type neighbour at +X only
    ///   1 = Center — both ±X, or neither
    ///   2 = Right  — same-type neighbour at -X only
    ///
    /// Row role (Z axis):
    ///   0 = Front  — same-type neighbour at +Z only
    ///   1 = Center — both ±Z, or neither
    ///   2 = Back   — same-type neighbour at -Z only
    /// </summary>
    private (GameObject prefab, float yOffset, float xOffset) ResolveAutoTile(Vector3Int pos, int typeIndex, BlockType bt)
    {
        bool hasRight = GetBlockType(pos + new Vector3Int( 1, 0,  0)) == typeIndex;
        bool hasLeft  = GetBlockType(pos + new Vector3Int(-1, 0,  0)) == typeIndex;
        bool hasFwd   = GetBlockType(pos + new Vector3Int( 0, 0,  1)) == typeIndex;
        bool hasBack  = GetBlockType(pos + new Vector3Int( 0, 0, -1)) == typeIndex;

        // Column: Left=0 (+X only), Center=1 (both or neither), Right=2 (-X only)
        int col = (hasRight && !hasLeft) ? 0
                : (!hasRight && hasLeft) ? 2
                : 1;

        // Row: Front=0 (+Z only), Center=1 (both or neither), Back=2 (-Z only)
        int row = (hasFwd && !hasBack) ? 0
                : (!hasFwd && hasBack) ? 2
                : 1;

        return (bt.autoTile.Get(col, row), bt.autoTile.GetOffset(col, row), bt.autoTile.GetXOffset(col, row));
    }

    // Computes the axis-aligned bounding box of all child meshes in the prefab,
    // expressed in the prefab root's local space. Public so the scene brush can
    // normalize its ghost previews with the exact same fit math as the bake.
    public static Bounds ComputePrefabBounds(GameObject prefab)
    {
        bool first = true;
        Bounds b = default;
        Matrix4x4 rootInv = prefab.transform.worldToLocalMatrix;
        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(false))
        {
            if (mf.sharedMesh == null) continue;
            Matrix4x4 childToRoot = rootInv * mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices)
            {
                Vector3 vr = childToRoot.MultiplyPoint3x4(v);
                if (first) { b = new Bounds(vr, Vector3.zero); first = false; }
                else b.Encapsulate(vr);
            }
        }
        return first ? new Bounds(Vector3.zero, Vector3.one) : b;
    }

    private static Vector3Int DominantDirection(Vector3 n, out float strength)
    {
        Vector3Int best = Vector3Int.up; float bestD = -1f;
        foreach (var d in Cardinals)
        {
            float dot = Vector3.Dot(n, new Vector3(d.x, d.y, d.z));
            if (dot > bestD) { bestD = dot; best = d; }
        }
        strength = bestD;
        return best;
        
    }

    /// <summary>
    /// Greedy cuboid expansion: merges a set of grid positions into the fewest
    /// axis-aligned BoxColliders, created under a new container child.
    /// Returns the number of BoxColliders created (0 if positions is empty).
    /// </summary>
    private int CreateGreedyBoxColliders(string containerName, HashSet<Vector3Int> positions, float gs, string tag = null)
    {
        if (positions.Count == 0) return 0;

        var container = new GameObject(containerName);
        container.transform.SetParent(transform, false);
        container.layer = bakeLayer;
        if (tag != null) container.tag = tag;

        return CreateGreedyBoxColliders(container, positions, gs, tag);
    }

    /// <summary>
    /// Greedy cuboid expansion: merges a set of grid positions into the fewest
    /// axis-aligned BoxColliders, added as children to the provided parent.
    /// Returns the number of BoxColliders created (0 if positions is empty).
    /// </summary>
    private int CreateGreedyBoxColliders(GameObject parent, HashSet<Vector3Int> positions, float gs, string tag = null)
    {
        if (positions.Count == 0) return 0;

        var sorted = new List<Vector3Int>(positions);
        sorted.Sort((a, b) =>
            a.x != b.x ? a.x.CompareTo(b.x) :
            a.y != b.y ? a.y.CompareTo(b.y) :
                         a.z.CompareTo(b.z));

        var remaining = new HashSet<Vector3Int>(positions);
        int boxCount = 0;

        while (remaining.Count > 0)
        {
            Vector3Int s = default;
            foreach (var p in sorted)
                if (remaining.Contains(p)) { s = p; break; }

            // Expand along X as far as possible.
            int xMax = s.x;
            while (remaining.Contains(new Vector3Int(xMax + 1, s.y, s.z))) xMax++;

            // Expand along Z: every X in [s.x..xMax] must have the candidate Z.
            int zMax = s.z;
            while (true)
            {
                bool ok = true;
                for (int x = s.x; x <= xMax && ok; x++)
                    if (!remaining.Contains(new Vector3Int(x, s.y, zMax + 1))) ok = false;
                if (!ok) break;
                zMax++;
            }

            // Expand along Y: every (X,Z) in the slab must have the candidate Y.
            int yMax = s.y;
            while (true)
            {
                bool ok = true;
                for (int x = s.x; x <= xMax && ok; x++)
                for (int z = s.z; z <= zMax && ok; z++)
                    if (!remaining.Contains(new Vector3Int(x, yMax + 1, z))) ok = false;
                if (!ok) break;
                yMax++;
            }

            var go = new GameObject($"Box_{boxCount}");
            go.transform.SetParent(parent.transform, false);
            go.layer = bakeLayer;
            if (tag != null) go.tag = tag;
            go.transform.localPosition = new Vector3(
                (s.x + xMax) * 0.5f * gs,
                (s.y + yMax) * 0.5f * gs,
                (s.z + zMax) * 0.5f * gs);
            var bc = go.AddComponent<BoxCollider>();
            bc.center = Vector3.zero;
            bc.size = new Vector3(
                (xMax - s.x + 1) * gs,
                (yMax - s.y + 1) * gs,
                (zMax - s.z + 1) * gs);

            for (int x = s.x; x <= xMax; x++)
            for (int y = s.y; y <= yMax; y++)
            for (int z = s.z; z <= zMax; z++)
                remaining.Remove(new Vector3Int(x, y, z));

            boxCount++;
        }

        return boxCount;
    }

    /// <summary>
    /// Bakes averaged (smooth) normals into the tangent channel so the outline
    /// shader can produce clean edges.  Mirrors the logic in SmoothNormalsBaker
    /// but lives here so the runtime assembly can call it during Bake().
    /// </summary>
    internal static void BakeSmoothNormalsToMesh(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals  = mesh.normals;

        var vertexDict = new Dictionary<Vector3, List<int>>();
        for (int i = 0; i < vertices.Length; i++)
        {
            if (!vertexDict.ContainsKey(vertices[i]))
                vertexDict[vertices[i]] = new List<int>();
            vertexDict[vertices[i]].Add(i);
        }

        var smoothNormals = new Vector3[vertices.Length];
        foreach (var kvp in vertexDict)
        {
            Vector3 avg = Vector3.zero;
            foreach (int idx in kvp.Value) avg += normals[idx];
            avg.Normalize();
            foreach (int idx in kvp.Value) smoothNormals[idx] = avg;
        }

        var tangents = new Vector4[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            tangents[i] = new Vector4(smoothNormals[i].x, smoothNormals[i].y, smoothNormals[i].z, 0f);

        mesh.tangents = tangents;
    }

    /// <summary>Instantiates the prop layer as real child objects under "BakedProps".
    /// Props stand on the FLOOR of their cell (paint them one layer above the
    /// ground), keep their prefab link and components (markers!), and are never
    /// merged into the chunk meshes.</summary>
    private void BakePropsLayer()
    {
        var existing = transform.Find("BakedProps");
        if (existing != null) DestroyImmediate(existing.gameObject);
        if (props.Count == 0 || propPalette == null || propPalette.Length == 0) return;

        var container = new GameObject("BakedProps");
        container.transform.SetParent(transform, false);

        float gs = gridSize;
        int made = 0;
        foreach (var p in props)
        {
            PropType pt = (p.typeIndex >= 0 && p.typeIndex < propPalette.Length)
                ? propPalette[p.typeIndex] : null;
            if (pt?.prefab == null) continue;

            GameObject go;
#if UNITY_EDITOR
            go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(pt.prefab, container.transform);
#else
            go = Instantiate(pt.prefab, container.transform);
#endif
            go.transform.localPosition = new Vector3(
                p.pos.x * gs,
                (p.pos.y - 0.5f) * gs + pt.yOffset,
                p.pos.z * gs);
            made++;
        }
        Debug.Log($"[WorldPainter] BakedProps: {made} prop(s) instantiated.");
    }

    private void DestroyBakedChild()
    {
        // Destroy the Baked parent (contains all chunk meshes)
        var bakedParent = transform.Find("Baked");
        if (bakedParent != null) DestroyImmediate(bakedParent.gameObject);

        // Destroy all per-chunk colliders
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch.name.StartsWith("BakedColliders_") ||
                ch.name.StartsWith("NavMesh_Walkable_") ||
                ch.name.StartsWith("NavMesh_NotWalkable_"))
            {
                DestroyImmediate(ch.gameObject);
            }
        }

        // Destroy other baked children
        var bi  = transform.Find("BakedInstances");
        if (bi  != null) DestroyImmediate(bi.gameObject);
        var bw  = transform.Find("BakedWalls");
        if (bw  != null) DestroyImmediate(bw.gameObject);
        var bp  = transform.Find("BakedProps");
        if (bp  != null) DestroyImmediate(bp.gameObject);

        // Also clean up any legacy single-chunk naming from previous versions
        var nw  = transform.Find("NavMesh_Walkable");
        if (nw  != null) DestroyImmediate(nw.gameObject);
        var nnw = transform.Find("NavMesh_NotWalkable");
        if (nnw != null) DestroyImmediate(nnw.gameObject);
        var bcc = transform.Find("BakedColliders");
        if (bcc != null) DestroyImmediate(bcc.gameObject);
    }

    // ---- Persistence ----

    /// <summary>
    /// Returns dataFileName if set, otherwise "WorldPainter" for backward compatibility.
    /// Used as the prefix for all baked mesh asset paths.
    /// </summary>
    public string BakePrefix => string.IsNullOrEmpty(dataFileName) ? "WorldPainter" : dataFileName;

#if UNITY_EDITOR
    [System.Serializable]
    private class WorldPainterData
    {
        public List<BlockEntry>         blocks         = new List<BlockEntry>();
        public List<FoliageZoneEntry>   foliageZones   = new List<FoliageZoneEntry>();
        public List<WaterfallZoneEntry> waterfallZones = new List<WaterfallZoneEntry>();
        public List<PropEntry>          props          = new List<PropEntry>();
    }

    public void SaveToFile()
    {
        if (string.IsNullOrEmpty(dataFileName))
        {
            Debug.LogError("[WorldPainter] dataFileName is empty — set a unique name before saving.");
            return;
        }

        string dir = Application.dataPath + "/WorldPainterData";
        if (!System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);

        var data = new WorldPainterData
        {
            blocks         = this.blocks,
            foliageZones   = this.foliageZones,
            waterfallZones = this.waterfallZones,
            props          = this.props
        };

        string json = JsonUtility.ToJson(data, true);
        string path = $"{dir}/{dataFileName}.json";
        System.IO.File.WriteAllText(path, json);
        UnityEditor.AssetDatabase.Refresh();
        Debug.Log($"[WorldPainter] Saved {blocks.Count} blocks, {foliageZones.Count} foliage zones, " +
                  $"{waterfallZones.Count} waterfall zones to {path}");
    }

    public void LoadFromFile()
    {
        if (string.IsNullOrEmpty(dataFileName))
        {
            Debug.LogError("[WorldPainter] dataFileName is empty — set a unique name before loading.");
            return;
        }

        string path = Application.dataPath + $"/WorldPainterData/{dataFileName}.json";
        if (!System.IO.File.Exists(path))
        {
            Debug.LogError($"[WorldPainter] File not found: {path}");
            return;
        }

        string json = System.IO.File.ReadAllText(path);
        var data = JsonUtility.FromJson<WorldPainterData>(json);

        blocks         = data.blocks         ?? new List<BlockEntry>();
        foliageZones   = data.foliageZones   ?? new List<FoliageZoneEntry>();
        waterfallZones = data.waterfallZones ?? new List<WaterfallZoneEntry>();
        props          = data.props          ?? new List<PropEntry>();

        RebuildMap();
        RebuildFoliageSet();
        RebuildWaterfallSet();
        RebuildPropMap();

        Debug.Log($"[WorldPainter] Loaded {blocks.Count} blocks, {foliageZones.Count} foliage zones, " +
                  $"{waterfallZones.Count} waterfall zones from {path}");
    }
#endif
}

internal static class GameObjectExt
{
    public static T GetOrAdd<T>(this GameObject go) where T : Component =>
        go.GetComponent<T>() ?? go.AddComponent<T>();
}

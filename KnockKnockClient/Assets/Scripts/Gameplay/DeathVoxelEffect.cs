using System.Collections.Generic;
using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// The death effect: the character turns into a cube statue OF ITSELF, and the
    /// cubes are sucked straight up one by one, top first, each from its own spot —
    /// no vortex, no gathering point — shrinking away as they rise.
    ///
    /// The cubes trace the actual MODEL: every renderer's mesh surface is sampled
    /// in the pose it died in, snapped to a voxel grid and coloured from that
    /// renderer's material. The silhouette is the character's own, so the statue
    /// reads as "the character became cubes", not as boxes stacked where the
    /// character stood. A mesh that is not readable at runtime falls back to a
    /// bounds fill rather than breaking the effect.
    ///
    /// One particle system carries all the cubes (one draw call), but the MOTION is
    /// driven manually: modules cannot express "hold still until it is your turn".
    /// Each cube's turn is encoded in its lifetime — startLifetime = its delay + a
    /// fixed flight budget — so age against that delay is the whole wait-or-fly
    /// decision, with no side table to keep aligned as particles die.
    ///
    /// Cosmetic, like every effect here: the server said "dead", and this is only
    /// what dead looks like.
    /// </summary>
    public sealed class DeathVoxelEffect : MonoBehaviour
    {
        /// <summary>Rough number of cubes to aim for; grid spacing derives from it.</summary>
        private const int TargetVoxels = 220;

        private const float MinSpacing = 0.15f;
        private const float MaxSpacing = 0.6f;

        /// <summary>How fast the peel front travels DOWN the figure, in metres of
        /// height per second. The vacuum takes the top of the head first.</summary>
        private const float PeelSpeed = 3f;

        /// <summary>Per-cube random extra wait, so the front frays instead of
        /// slicing the figure in clean horizontal sheets.</summary>
        private const float PeelJitter = 0.12f;

        /// <summary>A cube's whole flight, launch to gone. Its shrink is timed
        /// against this, so nothing ever pops out at full size.</summary>
        private const float FlightSeconds = 1.0f;

        /// <summary>Straight-up pull: acceleration and terminal speed.</summary>
        private const float RiseAccel = 30f;
        private const float MaxRiseSpeed = 12f;

        /// <summary>When in its flight a cube starts shrinking (fraction of
        /// FlightSeconds). Before this it rises at full size.</summary>
        private const float ShrinkStart = 0.4f;

        private static Material _material;
        private static Mesh _cubeMesh;

        private ParticleSystem _system;
        private ParticleSystem.Particle[] _buffer;
        private float _cubeSize;

        /// <summary>Turns the model under <paramref name="modelRoot"/> into its cube
        /// statue at the current pose. Call BEFORE hiding the model, while the
        /// renderers still know where and what colour they are.</summary>
        /// <summary>One renderer's mesh, ready to sample: geometry arrays pulled once,
        /// per-submesh materials resolved to a tint plus a READABLE texture — in this
        /// art style the colour lives in the palette texture at each vertex's UV,
        /// while _BaseColor is plain white.</summary>
        private sealed class SamplePart
        {
            public Mesh Mesh;
            public Matrix4x4 ToWorld;
            public bool Temporary;
            public Bounds Bounds;
            public Vector3[] Vertices;
            public Vector2[] Uvs;
            public Material[] Materials;
        }

        public static void Spawn(Transform modelRoot)
        {
            // Gather the body's meshes. The cel outlines are inverted-hull SHELLS of
            // the same geometry in the outline colour — sampling them would double
            // every surface and poison the statue's colours, so they are skipped.
            List<SamplePart> parts = new List<SamplePart>();
            float totalArea = 0f;
            foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                    continue;
                if (renderer.name.Contains("Outline"))
                    continue;

                Mesh mesh = null;
                bool temporary = false;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    mesh = new Mesh();
                    skinned.BakeMesh(mesh); // the pose it died in
                    temporary = true;
                }
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null)
                        mesh = filter.sharedMesh;
                }
                if (mesh == null)
                    continue;

                SamplePart part = new SamplePart
                {
                    Mesh = mesh,
                    ToWorld = renderer.transform.localToWorldMatrix,
                    Temporary = temporary,
                    Bounds = renderer.bounds,
                    Materials = renderer.sharedMaterials,
                };
                if (mesh.isReadable)
                {
                    part.Vertices = mesh.vertices;
                    part.Uvs = mesh.uv;
                    totalArea += SurfaceArea(part);
                }
                else
                {
                    totalArea += BoundsArea(renderer.bounds);
                }
                parts.Add(part);
            }
            if (parts.Count == 0 || totalArea <= 0f)
                return;

            // Spacing so the sampled SHELL comes out near the target cube count:
            // cubes ≈ area / spacing².
            float spacing = Mathf.Clamp(
                Mathf.Sqrt(totalArea / TargetVoxels), MinSpacing, MaxSpacing);

            // One voxel per grid cell across the whole body, first colour wins —
            // that is what makes it a coherent statue instead of overlapping shells.
            Dictionary<Vector3Int, Color> grid = new Dictionary<Vector3Int, Color>();
            float topY = float.NegativeInfinity;
            float bottomY = float.PositiveInfinity;
            foreach (SamplePart part in parts)
            {
                if (part.Vertices != null)
                    SampleSurface(part, spacing, grid);
                else
                    SampleBounds(part.Bounds, TintOf(FirstMaterial(part)), spacing, grid);
                if (part.Temporary)
                    Destroy(part.Mesh);
                topY = Mathf.Max(topY, part.Bounds.max.y);
                bottomY = Mathf.Min(bottomY, part.Bounds.min.y);
            }
            if (grid.Count == 0)
                return;

            Vector3 centre = Vector3.zero;
            foreach (KeyValuePair<Vector3Int, Color> cell in grid)
                centre += CellCentre(cell.Key, spacing);
            centre /= grid.Count;
            centre.y = bottomY;

            List<ParticleSystem.Particle> voxels = new List<ParticleSystem.Particle>(grid.Count);
            foreach (KeyValuePair<Vector3Int, Color> cell in grid)
            {
                Vector3 position = CellCentre(cell.Key, spacing);

                // The vacuum takes the TOP first: a cube's wait grows with its
                // distance below the highest point. Encoded in the lifetime, so the
                // driver recovers it without any side table.
                float delay = (topY - position.y) / PeelSpeed + Random.Range(0f, PeelJitter);

                float tone = Random.Range(0.95f, 1.05f); // narrow: the statue should MATCH, not vary
                ParticleSystem.Particle voxel = new ParticleSystem.Particle();
                voxel.position = position - centre; // local to the system
                voxel.startColor = new Color(
                    Mathf.Clamp01(cell.Value.r * tone),
                    Mathf.Clamp01(cell.Value.g * tone),
                    Mathf.Clamp01(cell.Value.b * tone), 1f);
                voxel.startSize = spacing * 0.95f;
                voxel.startLifetime = delay + FlightSeconds;
                voxel.remainingLifetime = voxel.startLifetime;
                voxel.velocity = Vector3.zero;          // a statue, until its turn
                voxel.rotation3D = Vector3.zero;        // grid-aligned while standing
                voxel.angularVelocity3D = Vector3.zero; // tumbling starts at launch
                voxels.Add(voxel);
            }

            GameObject go = new GameObject("Death Voxels");
            go.transform.position = centre;

            ParticleSystem system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            // Modules stay OUT of the motion: the driver below owns it. The system
            // is just the container that draws the cubes and ages their lifetimes.
            var main = system.main;
            main.loop = false;
            main.playOnAwake = false;
            main.maxParticles = voxels.Count;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;

            var emission = system.emission;
            emission.enabled = false;

            ParticleSystemRenderer renderer0 = go.GetComponent<ParticleSystemRenderer>();
            renderer0.renderMode = ParticleSystemRenderMode.Mesh;
            renderer0.mesh = CubeMesh();
            renderer0.alignment = ParticleSystemRenderSpace.World;
            renderer0.sharedMaterial = VoxelMaterial();
            renderer0.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            system.Play();
            system.SetParticles(voxels.ToArray(), voxels.Count);

            DeathVoxelEffect driver = go.AddComponent<DeathVoxelEffect>();
            driver._system = system;
            driver._buffer = new ParticleSystem.Particle[voxels.Count];
            driver._cubeSize = spacing * 0.95f;
        }

        private void Update()
        {
            if (_system == null)
            {
                Destroy(gameObject);
                return;
            }

            int count = _system.GetParticles(_buffer);
            if (count == 0)
            {
                Destroy(gameObject);
                return;
            }

            float dt = Time.deltaTime;
            for (int i = 0; i < count; i++)
            {
                ParticleSystem.Particle voxel = _buffer[i];

                // Its turn yet? The delay is the lifetime minus the flight budget.
                float age = voxel.startLifetime - voxel.remainingLifetime;
                float delay = voxel.startLifetime - FlightSeconds;
                if (age < delay)
                {
                    _buffer[i] = voxel; // still part of the statue
                    continue;
                }

                // First frame of flight: start tumbling gently.
                if (voxel.angularVelocity3D == Vector3.zero)
                    voxel.angularVelocity3D = Random.insideUnitSphere * 200f;

                // Straight up from its own spot — the whole "vacuum" is vertical.
                Vector3 velocity = voxel.velocity;
                velocity.y = Mathf.Min(velocity.y + RiseAccel * dt, MaxRiseSpeed);
                voxel.velocity = velocity;

                // Shrink through the later part of the flight, so the cube rises at
                // full size first and then thins away to nothing mid-air. Min()
                // keeps it monotonic; lifetime ends exactly as the size reaches 0.
                float flight = Mathf.Clamp01((age - delay) / FlightSeconds);
                float sizeCap = _cubeSize * (1f - Mathf.Clamp01((flight - ShrinkStart) / (1f - ShrinkStart)));
                if (voxel.startSize > sizeCap)
                    voxel.startSize = sizeCap;

                _buffer[i] = voxel;
            }

            _system.SetParticles(_buffer, count);
        }

        // ------------------------------------------------------------- sampling

        /// <summary>
        /// Uniform samples across the mesh surface, snapped to the voxel grid. One
        /// sample per spacing² of area covers the shell without holes regardless of
        /// how the model happens to be triangulated — big lowpoly triangles get
        /// many samples, dense areas dedupe through the grid.
        ///
        /// The COLOUR is read from the material's texture at the sample point's
        /// interpolated UV. In this art style that is where the colour actually is:
        /// the materials tint with plain white and point their UVs into a palette
        /// texture, so reading _BaseColor gives a white statue. Per SUBMESH, because
        /// skin and clothes are different materials on the same mesh.
        /// </summary>
        private static void SampleSurface(SamplePart part, float spacing,
            Dictionary<Vector3Int, Color> grid)
        {
            float cellArea = spacing * spacing;
            bool hasUvs = part.Uvs != null && part.Uvs.Length == part.Vertices.Length;

            for (int submesh = 0; submesh < part.Mesh.subMeshCount; submesh++)
            {
                Material material = part.Materials == null || part.Materials.Length == 0
                    ? null
                    : part.Materials[Mathf.Min(submesh, part.Materials.Length - 1)];
                if (material != null && material.name.Contains("Outline"))
                    continue; // outline shell as a submesh rather than a renderer

                Color tint = TintOf(material);
                Texture2D palette = hasUvs ? ReadableTextureOf(material) : null;

                int[] triangles = part.Mesh.GetTriangles(submesh);
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int i0 = triangles[t];
                    int i1 = triangles[t + 1];
                    int i2 = triangles[t + 2];
                    Vector3 a = part.ToWorld.MultiplyPoint3x4(part.Vertices[i0]);
                    Vector3 b = part.ToWorld.MultiplyPoint3x4(part.Vertices[i1]);
                    Vector3 c = part.ToWorld.MultiplyPoint3x4(part.Vertices[i2]);

                    float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                    int samples = Mathf.Clamp(Mathf.CeilToInt(area / cellArea) * 2, 1, 96);

                    for (int s = 0; s < samples; s++)
                    {
                        // Uniform point on the triangle (square-root barycentrics).
                        float r1 = Mathf.Sqrt(Random.value);
                        float r2 = Random.value;
                        float w0 = 1f - r1;
                        float w1 = r1 * (1f - r2);
                        float w2 = r1 * r2;
                        Vector3 point = a * w0 + b * w1 + c * w2;

                        Vector3Int key = CellOf(point, spacing);
                        if (grid.ContainsKey(key))
                            continue;

                        Color colour = tint;
                        if (palette != null)
                        {
                            Vector2 uv = part.Uvs[i0] * w0 + part.Uvs[i1] * w1 + part.Uvs[i2] * w2;
                            Color texel = palette.GetPixelBilinear(uv.x, uv.y);
                            colour = new Color(texel.r * tint.r, texel.g * tint.g, texel.b * tint.b, 1f);
                        }

                        // The palette stores sRGB values, and the character's own
                        // shader gets them hardware-converted to linear before
                        // rendering. Vertex colours get no such conversion — feeding
                        // the raw texel in re-encodes it on output and the cubes come
                        // out pale. One explicit conversion puts them back on the
                        // character's exact colours.
                        if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                            colour = colour.linear;

                        grid[key] = colour;
                    }
                }
            }
        }

        /// <summary>Fallback for meshes without read/write access in a build: fill
        /// the renderer's bounds. Blockier than the surface, but never broken.</summary>
        private static void SampleBounds(Bounds bounds, Color color, float spacing,
            Dictionary<Vector3Int, Color> grid)
        {
            for (float x = bounds.min.x + spacing * 0.5f; x < bounds.max.x; x += spacing)
            for (float y = bounds.min.y + spacing * 0.5f; y < bounds.max.y; y += spacing)
            for (float z = bounds.min.z + spacing * 0.5f; z < bounds.max.z; z += spacing)
            {
                Vector3Int key = CellOf(new Vector3(x, y, z), spacing);
                if (!grid.ContainsKey(key))
                    grid[key] = color;
            }
        }

        private static float SurfaceArea(SamplePart part)
        {
            int[] triangles = part.Mesh.triangles;
            float area = 0f;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = part.ToWorld.MultiplyPoint3x4(part.Vertices[triangles[t]]);
                Vector3 b = part.ToWorld.MultiplyPoint3x4(part.Vertices[triangles[t + 1]]);
                Vector3 c = part.ToWorld.MultiplyPoint3x4(part.Vertices[triangles[t + 2]]);
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return area;
        }

        private static float BoundsArea(Bounds bounds)
        {
            Vector3 s = bounds.size;
            return 2f * (s.x * s.y + s.y * s.z + s.x * s.z);
        }

        private static Vector3Int CellOf(Vector3 point, float spacing) => new Vector3Int(
            Mathf.FloorToInt(point.x / spacing),
            Mathf.FloorToInt(point.y / spacing),
            Mathf.FloorToInt(point.z / spacing));

        private static Vector3 CellCentre(Vector3Int cell, float spacing) => new Vector3(
            (cell.x + 0.5f) * spacing,
            (cell.y + 0.5f) * spacing,
            (cell.z + 0.5f) * spacing);

        // ------------------------------------------------------------ resources

        private static Material FirstMaterial(SamplePart part) =>
            part.Materials != null && part.Materials.Length > 0 ? part.Materials[0] : null;

        private static Color TintOf(Material material)
        {
            if (material == null)
                return Color.white;
            if (material.HasProperty("_BaseColor"))
                return material.GetColor("_BaseColor");
            if (material.HasProperty("_Color"))
                return material.color;
            return Color.white;
        }

        /// <summary>Readable CPU copies of the palette textures, one per source,
        /// made by blitting through a temporary RenderTexture — GetPixelBilinear
        /// needs CPU access, and imported textures rarely have read/write enabled.
        /// The palettes are tiny and the copies are cached for the session.</summary>
        private static readonly Dictionary<Texture, Texture2D> _readableTextures =
            new Dictionary<Texture, Texture2D>();

        private static Texture2D ReadableTextureOf(Material material)
        {
            if (material == null)
                return null;
            Texture source = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
            if (source == null && material.HasProperty("_MainTex"))
                source = material.GetTexture("_MainTex");
            if (source == null)
                source = material.mainTexture;
            if (source == null)
                return null;

            if (_readableTextures.TryGetValue(source, out Texture2D cached) && cached != null)
                return cached;

            RenderTexture scratch = RenderTexture.GetTemporary(
                source.width, source.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, scratch);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = scratch;
            Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(scratch);

            _readableTextures[source] = copy;
            return copy;
        }

        private static Material VoxelMaterial()
        {
            if (_material == null)
            {
                // A real asset in Resources, so the shader survives build stripping —
                // Shader.Find on a shader nothing references returns null in a
                // player, and the effect would silently vanish on standalone.
                _material = Resources.Load<Material>("Mat_VoxelDeath");
                if (_material == null)
                {
                    Shader shader = Shader.Find("Custom/VoxelDeath");
                    _material = new Material(shader != null
                        ? shader
                        : Shader.Find("Universal Render Pipeline/Unlit"));
                }
            }
            return _material;
        }

        private static Mesh CubeMesh()
        {
            if (_cubeMesh == null)
            {
                GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cubeMesh = temp.GetComponent<MeshFilter>().sharedMesh;
                Object.Destroy(temp);
            }
            return _cubeMesh;
        }
    }
}

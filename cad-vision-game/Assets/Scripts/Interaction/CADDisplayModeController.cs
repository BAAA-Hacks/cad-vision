using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Renders the global display mode (CADUISettings.DisplayMode) on every registered CAD object:
///
/// - Shaded: the imported materials, untouched. Edge overlays are inactive.
/// - Edges: imported materials plus a dark feature-edge overlay.
/// - Wireframe: surfaces swapped to one shared see-through material (originals kept and
///   restored exactly), strong edges, and the edges hidden behind surfaces drawn faintly.
///   The see-through surfaces still write depth (so the nearest face hides the faces behind
///   it); they, the wireframe edges and — while Wireframe is on — the selection outline hull
///   draw after the sky and before UI canvases, so the outline keeps its rim and menus are
///   never painted over. Colliders and renderers stay enabled, so interaction is unchanged.
///
/// Edge overlays: one child "__CADEdges" per MeshRenderer (CADVisualOverlay, no collider),
/// drawing a MeshTopology.Lines mesh of the source mesh's feature edges: boundary edges,
/// non-manifold edges and creases whose face angle exceeds creaseAngle; coplanar triangulation
/// diagonals and smooth tessellation are dropped. Edge meshes are built once per source mesh and
/// shared by every renderer using it (static cache, released when the source mesh is gone), and
/// only when a mode first needs them. Being children of the CAD renderers, overlays follow
/// moves, detach, group and model transforms, hide/isolate (SetActive) and are destroyed with a
/// replaced model; the controller drops those dead entries and applies the current mode to the
/// new model (CADVisionManipulationService.ModelReplaced).
///
/// Focus (CADVisionManipulationService.Focus): renderers outside the focus targets become
/// ghost/reference geometry in every mode (a very faint see-through surface from the Wireframe
/// surface shader at lower opacity, plus faint edges from the same overlays) while focus
/// targets render in the current mode. Nothing is hidden, so assembly context stays readable.
///
/// Event-driven: nothing runs per frame. Imported materials are never modified; the only
/// runtime materials are four shared ones owned by this component.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(110)] // After the service registers scene CAD objects (Start).
[RequireComponent(typeof(CADVisionManipulationService))]
[RequireComponent(typeof(CADUISettings))]
public class CADDisplayModeController : MonoBehaviour
{
    public const string OverlayName = "__CADEdges";
    private const string EdgeShaderResource = "CADVision/CADEdgeLines";
    private const string SurfaceShaderResource = "CADVision/CADWireframeSurface";

    [Header("Edges")]
    [Tooltip("Faces meeting at more than this angle form a visible edge (degrees).")]
    [SerializeField, Range(1f, 89f)] private float creaseAngle = 30f;
    [SerializeField] private Color edgeColor = new Color(0.08f, 0.09f, 0.11f, 1f);

    [Header("Wireframe")]
    [SerializeField] private Color wireEdgeColor = new Color(0.10f, 0.16f, 0.24f, 1f);
    [Tooltip("Edges behind surfaces (alpha = strength).")]
    [SerializeField] private Color hiddenEdgeColor = new Color(0.10f, 0.16f, 0.24f, 0.25f);
    [SerializeField] private Color wireSurfaceColor = new Color(0.86f, 0.89f, 0.93f, 1f);
    [Tooltip("Opacity of Wireframe faces (0 = invisible, 1 = solid).")]
    [SerializeField, Range(0f, 1f)] private float wireSurfaceOpacity = 0.15f;

    [Header("Focus ghost")]
    [SerializeField] private Color ghostSurfaceColor = new Color(0.78f, 0.82f, 0.88f, 1f);
    [Tooltip("Opacity of ghosted (non-focus) faces.")]
    [SerializeField, Range(0f, 1f)] private float ghostSurfaceOpacity = 0.08f;
    [Tooltip("Ghost edges (alpha = strength).")]
    [SerializeField] private Color ghostEdgeColor = new Color(0.45f, 0.5f, 0.58f, 0.35f);

    // Wireframe draw order: surfaces 2980 (shader), outline hull, edges, hidden edges; UI is 3000.
    private const int WireOutlineQueue = (int)RenderQueue.Transparent - 17;
    private const int WireEdgeQueue = (int)RenderQueue.Transparent - 15;
    private const int HiddenEdgeQueue = (int)RenderQueue.Transparent - 14;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int ZTestId = Shader.PropertyToID("_ZTest");
    private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
    private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");

    // Edge meshes per (source mesh, crease angle); shared by every renderer using the mesh.
    private static readonly Dictionary<Mesh, (float angle, Mesh edges)> EdgeMeshes = new();

    private sealed class Entry
    {
        public MeshRenderer Renderer;
        public GameObject Overlay;              // Null: no edges (unreadable/empty mesh).
        public MeshRenderer OverlayRenderer;
        public int Segments;                    // Edge line segments in the overlay.
        public string OwnerId;                  // Nearest CADObject's ID (focus test).
        public Material[] OriginalMaterials;    // Set while the wireframe surface is applied.
    }

    private CADVisionManipulationService manipulationService;
    private CADUISettings settings;
    private CADSelectionOutline selectionOutline;
    private Material edgeMaterial, wireEdgeMaterial, hiddenEdgeMaterial, surfaceMaterial;
    private Material ghostSurfaceMaterial, ghostEdgeMaterial;
    private Material[] edgeMaterials, wireMaterials, ghostEdgeMaterials;
    private readonly Dictionary<MeshRenderer, Entry> entries = new();
    private readonly Dictionary<int, Material[]> surfaceArrays = new();
    private readonly Dictionary<int, Material[]> ghostArrays = new();
    private CADDisplayMode applied = CADDisplayMode.Shaded;
    private bool ready;

    public CADDisplayMode AppliedMode => applied;
    /// <summary>Renderers currently drawn as focus ghosts.</summary>
    public int GhostedRendererCount { get; private set; }
    /// <summary>Renderers this controller manages (have or had overlays / surface swaps).</summary>
    public int ManagedRendererCount => entries.Count;
    public int OverlayCount
    {
        get
        {
            int count = 0;
            foreach (Entry entry in entries.Values)
            {
                if (entry.Overlay != null)
                    count++;
            }
            return count;
        }
    }

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        settings = GetComponent<CADUISettings>();
        selectionOutline = GetComponent<CADSelectionOutline>();

        Shader edgeShader = Resources.Load<Shader>(EdgeShaderResource);
        Shader surfaceShader = Resources.Load<Shader>(SurfaceShaderResource);
        if (edgeShader == null || !edgeShader.isSupported || surfaceShader == null || !surfaceShader.isSupported)
        {
            Debug.LogWarning("[CADDisplayMode] Display shaders unavailable; display modes stay Shaded.");
            enabled = false;
            return;
        }

        edgeMaterial = new Material(edgeShader) { name = "CAD Edges", enableInstancing = true };
        wireEdgeMaterial = new Material(edgeShader) { name = "CAD Wireframe Edges", enableInstancing = true };
        hiddenEdgeMaterial = new Material(edgeShader) { name = "CAD Hidden Edges", enableInstancing = true };
        hiddenEdgeMaterial.SetFloat(ZTestId, (float)CompareFunction.Greater);
        hiddenEdgeMaterial.SetFloat(SrcBlendId, (float)BlendMode.SrcAlpha);
        hiddenEdgeMaterial.SetFloat(DstBlendId, (float)BlendMode.OneMinusSrcAlpha);
        wireEdgeMaterial.renderQueue = WireEdgeQueue;
        hiddenEdgeMaterial.renderQueue = HiddenEdgeQueue;
        surfaceMaterial = new Material(surfaceShader) { name = "CAD Wireframe Surface", enableInstancing = true };
        ghostSurfaceMaterial = new Material(surfaceShader) { name = "CAD Ghost Surface", enableInstancing = true };
        ghostEdgeMaterial = new Material(edgeShader) { name = "CAD Ghost Edges", enableInstancing = true };
        ghostEdgeMaterial.SetFloat(SrcBlendId, (float)BlendMode.SrcAlpha);
        ghostEdgeMaterial.SetFloat(DstBlendId, (float)BlendMode.OneMinusSrcAlpha);
        ghostEdgeMaterial.renderQueue = WireEdgeQueue;
        ApplyColors();

        edgeMaterials = new[] { edgeMaterial };
        ghostEdgeMaterials = new[] { ghostEdgeMaterial };
        // A second material on a single-submesh renderer draws the same lines again.
        wireMaterials = new[] { wireEdgeMaterial, hiddenEdgeMaterial };
        ready = true;
    }

    private void OnEnable()
    {
        if (!ready)
            return;
        settings.DisplayModeChanged += OnDisplayModeChanged;
        manipulationService.ModelReplaced += OnModelReplaced;
        manipulationService.FocusChanged += OnFocusChanged;
    }

    private void OnDisable()
    {
        if (!ready)
            return;
        settings.DisplayModeChanged -= OnDisplayModeChanged;
        manipulationService.ModelReplaced -= OnModelReplaced;
        manipulationService.FocusChanged -= OnFocusChanged;
        Apply(CADDisplayMode.Shaded, ignoreFocus: true); // Leave the model exactly as imported.
    }

    private void Start()
    {
        if (ready)
            Apply(settings.DisplayMode);
    }

    private void OnDestroy()
    {
        RemoveAllOverlays();
        foreach (Material material in new[] { edgeMaterial, wireEdgeMaterial, hiddenEdgeMaterial, surfaceMaterial,
                     ghostSurfaceMaterial, ghostEdgeMaterial })
        {
            if (material != null)
                DestroySafe(material);
        }
    }

    private void OnValidate()
    {
        if (ready)
            ApplyColors();
    }

    private void OnDisplayModeChanged(CADDisplayMode mode) => Apply(mode);

    private void OnFocusChanged() => Apply(settings.DisplayMode);

    // The old model's renderers (and their overlays) were destroyed with it; forget them and
    // give the new model the current mode.
    private void OnModelReplaced()
    {
        PruneDead();
        Apply(settings.DisplayMode);
    }

    private void ApplyColors()
    {
        edgeMaterial.SetColor(ColorId, edgeColor);
        wireEdgeMaterial.SetColor(ColorId, wireEdgeColor);
        hiddenEdgeMaterial.SetColor(ColorId, hiddenEdgeColor);
        Color surface = wireSurfaceColor;
        surface.a = wireSurfaceOpacity;
        surfaceMaterial.SetColor(ColorId, surface);
        Color ghost = ghostSurfaceColor;
        ghost.a = ghostSurfaceOpacity;
        ghostSurfaceMaterial.SetColor(ColorId, ghost);
        ghostEdgeMaterial.SetColor(ColorId, ghostEdgeColor);
    }

    // ---------------- Applying a mode ----------------

    /// <summary>
    /// Applies a mode (and the service's current focus) to every registered CAD renderer
    /// (idempotent).
    /// </summary>
    public void Apply(CADDisplayMode mode) => Apply(mode, ignoreFocus: false);

    private void Apply(CADDisplayMode mode, bool ignoreFocus)
    {
        if (!ready)
            return;

        PruneDead();
        bool focus = !ignoreFocus && manipulationService.IsFocusActive;
        if (mode != CADDisplayMode.Shaded || focus)
            DiscoverRenderers();

        int segments = 0;
        int ghosted = 0;
        foreach (Entry entry in entries.Values)
        {
            bool ghost = focus && !manipulationService.IsInFocus(entry.OwnerId);
            if (ghost)
                ghosted++;

            // Surfaces: ghost material outside the focus, the see-through surface in Wireframe,
            // the exact originals otherwise (saved once, restored as they were).
            Material[] surfaces = ghost ? GhostArray(MaterialCount(entry))
                : mode == CADDisplayMode.Wireframe ? SurfaceArray(MaterialCount(entry))
                : null;
            if (surfaces != null)
            {
                entry.OriginalMaterials ??= entry.Renderer.sharedMaterials;
                entry.Renderer.sharedMaterials = surfaces;
            }
            else if (entry.OriginalMaterials != null)
            {
                entry.Renderer.sharedMaterials = entry.OriginalMaterials;
                entry.OriginalMaterials = null;
            }

            // Edges: overlay on for ghosts and in Edges / Wireframe, with the matching materials.
            if (entry.Overlay == null)
                continue;
            bool show = ghost || mode != CADDisplayMode.Shaded;
            if (show)
            {
                entry.OverlayRenderer.sharedMaterials = ghost ? ghostEdgeMaterials
                    : mode == CADDisplayMode.Wireframe ? wireMaterials : edgeMaterials;
                entry.OverlayRenderer.enabled = entry.Renderer.enabled;
                segments += entry.Segments;
            }
            if (entry.Overlay.activeSelf != show)
                entry.Overlay.SetActive(show);
        }
        GhostedRendererCount = ghosted;

        // See-through surfaces (Wireframe, ghosts) draw after opaque geometry: the outline hull
        // must follow them to find their depth.
        if (selectionOutline != null)
            selectionOutline.RenderQueue = mode == CADDisplayMode.Wireframe || ghosted > 0 ? WireOutlineQueue : -1;

        if (mode != applied || mode != CADDisplayMode.Shaded)
            Debug.Log($"[CADDisplayMode] {mode}: {entries.Count} renderers, {OverlayCount} edge overlays, " +
                $"{segments} edge segments, {EdgeMeshes.Count} cached edge meshes.");
        applied = mode;
    }

    private static int MaterialCount(Entry entry) =>
        entry.OriginalMaterials?.Length ?? entry.Renderer.sharedMaterials.Length;

    private Material[] GhostArray(int length)
    {
        if (!ghostArrays.TryGetValue(length, out Material[] array))
        {
            array = new Material[length];
            for (int i = 0; i < length; i++)
                array[i] = ghostSurfaceMaterial;
            ghostArrays[length] = array;
        }
        return array;
    }

    private Material[] SurfaceArray(int length)
    {
        if (!surfaceArrays.TryGetValue(length, out Material[] array))
        {
            array = new Material[length];
            for (int i = 0; i < length; i++)
                array[i] = surfaceMaterial;
            surfaceArrays[length] = array;
        }
        return array;
    }

    // Every MeshRenderer under every registered CAD object, once (nested objects share
    // renderers), including inactive (hidden) ones so they are ready when shown.
    private void DiscoverRenderers()
    {
        foreach (CADObject cadObject in manipulationService.GetRegisteredObjects())
        {
            foreach (MeshRenderer renderer in cadObject.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (entries.ContainsKey(renderer) || renderer.TryGetComponent(out CADVisualOverlay _))
                    continue;

                CADObject owner = renderer.GetComponentInParent<CADObject>(true);
                var entry = new Entry { Renderer = renderer, OwnerId = owner != null ? owner.id : null };
                if (renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
                {
                    Mesh edges = GetEdgeMesh(filter.sharedMesh, creaseAngle);
                    if (edges != null)
                        CreateOverlay(entry, edges);
                }
                entries.Add(renderer, entry);
            }
        }
    }

    private static void CreateOverlay(Entry entry, Mesh edges)
    {
        var overlay = new GameObject(OverlayName);
        overlay.SetActive(false);
        overlay.layer = entry.Renderer.gameObject.layer;
        overlay.transform.SetParent(entry.Renderer.transform, false);
        overlay.AddComponent<CADVisualOverlay>();
        overlay.AddComponent<MeshFilter>().sharedMesh = edges;
        var renderer = overlay.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        entry.Overlay = overlay;
        entry.OverlayRenderer = renderer;
        entry.Segments = (int)edges.GetIndexCount(0) / 2;
    }

    private void PruneDead()
    {
        List<MeshRenderer> dead = null;
        foreach (KeyValuePair<MeshRenderer, Entry> pair in entries)
        {
            if (pair.Key == null)
                (dead ??= new List<MeshRenderer>()).Add(pair.Key);
        }

        if (dead != null)
        {
            foreach (MeshRenderer renderer in dead)
                entries.Remove(renderer);
        }

        ReleaseDeadEdgeMeshes();
    }

    private void RemoveAllOverlays()
    {
        foreach (Entry entry in entries.Values)
        {
            if (entry.Renderer != null && entry.OriginalMaterials != null)
                entry.Renderer.sharedMaterials = entry.OriginalMaterials;
            if (entry.Overlay != null)
                DestroySafe(entry.Overlay);
        }
        entries.Clear();
    }

    // ---------------- Edge extraction ----------------

    private static Mesh GetEdgeMesh(Mesh source, float creaseAngle)
    {
        if (EdgeMeshes.TryGetValue(source, out (float angle, Mesh edges) cached) &&
            cached.edges != null && Mathf.Approximately(cached.angle, creaseAngle))
        {
            return cached.edges;
        }

        if (!source.isReadable)
        {
            Debug.LogWarning($"[CADDisplayMode] Mesh '{source.name}' is not readable; no edges for it.");
            return null;
        }

        ExtractFeatureEdges(source, creaseAngle, out Vector3[] positions, out int[] lines);
        if (lines.Length == 0)
            return null;

        var mesh = new Mesh
        {
            name = source.name + " (edges)",
            indexFormat = positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
        };
        mesh.vertices = positions;
        mesh.SetIndices(lines, MeshTopology.Lines, 0);
        mesh.bounds = source.bounds;
        mesh.UploadMeshData(true); // GPU-only from here on.

        if (cached.edges != null)
            DestroySafe(cached.edges);
        EdgeMeshes[source] = (creaseAngle, mesh);
        return mesh;
    }

    /// <summary>
    /// Feature edges of a triangle mesh (all triangle submeshes): vertices are welded by
    /// position (0.01 mm in mesh units), then an edge is kept if it borders one triangle
    /// (boundary), more than two (non-manifold), or two whose normals differ by more than
    /// creaseAngle. Degenerate triangles are ignored. Output: welded positions used by kept
    /// edges, and line index pairs into them. The source mesh is only read.
    /// </summary>
    public static void ExtractFeatureEdges(Mesh source, float creaseAngle, out Vector3[] positions, out int[] lines)
    {
        Vector3[] vertices = source.vertices;
        var weldIndex = new Dictionary<Vector3Int, int>();
        var welded = new int[vertices.Length];
        var weldedPositions = new List<Vector3>();
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i] * 100000f;
            var key = new Vector3Int(Mathf.RoundToInt(v.x), Mathf.RoundToInt(v.y), Mathf.RoundToInt(v.z));
            if (!weldIndex.TryGetValue(key, out int index))
            {
                index = weldedPositions.Count;
                weldIndex.Add(key, index);
                weldedPositions.Add(vertices[i]);
            }
            welded[i] = index;
        }

        float cosCrease = Mathf.Cos(creaseAngle * Mathf.Deg2Rad);
        var edges = new Dictionary<long, (Vector3 normal, int faces, bool sharp)>();
        for (int s = 0; s < source.subMeshCount; s++)
        {
            if (source.GetTopology(s) != MeshTopology.Triangles)
                continue;

            int[] triangles = source.GetTriangles(s);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = welded[triangles[t]], b = welded[triangles[t + 1]], c = welded[triangles[t + 2]];
                if (a == b || b == c || a == c)
                    continue;

                Vector3 face = Vector3.Cross(weldedPositions[b] - weldedPositions[a], weldedPositions[c] - weldedPositions[a]);
                if (face.sqrMagnitude < 1e-20f)
                    continue;
                face.Normalize();

                AddEdge(edges, a, b, face, cosCrease);
                AddEdge(edges, b, c, face, cosCrease);
                AddEdge(edges, c, a, face, cosCrease);
            }
        }

        var remap = new Dictionary<int, int>();
        var outPositions = new List<Vector3>();
        var outLines = new List<int>();
        foreach (KeyValuePair<long, (Vector3 normal, int faces, bool sharp)> edge in edges)
        {
            if (edge.Value.faces != 1 && !edge.Value.sharp)
                continue;

            outLines.Add(Remap((int)(edge.Key >> 32)));
            outLines.Add(Remap((int)(edge.Key & 0xffffffff)));
        }

        positions = outPositions.ToArray();
        lines = outLines.ToArray();

        int Remap(int weldedIndex)
        {
            if (!remap.TryGetValue(weldedIndex, out int index))
            {
                index = outPositions.Count;
                remap.Add(weldedIndex, index);
                outPositions.Add(weldedPositions[weldedIndex]);
            }
            return index;
        }
    }

    private static void AddEdge(Dictionary<long, (Vector3 normal, int faces, bool sharp)> edges, int a, int b,
        Vector3 faceNormal, float cosCrease)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!edges.TryGetValue(key, out (Vector3 normal, int faces, bool sharp) edge))
        {
            edges.Add(key, (faceNormal, 1, false));
            return;
        }

        edge.faces++;
        // Second face: a crease if the normals differ enough. More than two: non-manifold.
        if (edge.faces == 2)
            edge.sharp = Vector3.Dot(edge.normal, faceNormal) < cosCrease;
        else
            edge.sharp = true;
        edges[key] = edge;
    }

    private static void ReleaseDeadEdgeMeshes()
    {
        List<Mesh> dead = null;
        foreach (KeyValuePair<Mesh, (float angle, Mesh edges)> entry in EdgeMeshes)
        {
            if (entry.Key == null || entry.Value.edges == null)
                (dead ??= new List<Mesh>()).Add(entry.Key);
        }

        if (dead == null)
            return;

        foreach (Mesh key in dead)
        {
            if (EdgeMeshes.TryGetValue(key, out (float angle, Mesh edges) cached) && cached.edges != null)
                DestroySafe(cached.edges);
            EdgeMeshes.Remove(key);
        }
    }

    /// <summary>Test hook: the cached edge mesh for a source mesh, if built.</summary>
    public static Mesh CachedEdgeMesh(Mesh source) =>
        EdgeMeshes.TryGetValue(source, out (float angle, Mesh edges) cached) ? cached.edges : null;

    // Destroy is not allowed outside Play mode (EditMode tests); DestroyImmediate is.
    private static void DestroySafe(Object obj)
    {
        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }
}

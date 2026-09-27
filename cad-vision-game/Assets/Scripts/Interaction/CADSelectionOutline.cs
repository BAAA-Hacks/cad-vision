using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Neon outline for selected CAD objects instead of the whole-object color tint. Imported
/// materials are never touched: each selected renderer gets a child "shell" (same mesh, normals
/// averaged across split vertices) drawn with an inverted-hull shader, so only a rim around the
/// silhouette shows. One extra unlit draw per selected renderer; no extra cameras or passes.
///
/// Presentation only: reads the service's selection each frame, owns nothing else. Shells are
/// children of the CAD renderers, so they follow moves, hide/isolate and model replacement.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADSelectionOutline : MonoBehaviour
{
    private const string ShaderResource = "CADVision/CADOutlineHull";
    private const string ShellName = "__CADOutline";

    [SerializeField] private Color outlineColor = new Color(0f, 0.9f, 1f, 1f);
    [Tooltip("Outline thickness as a fraction of the view height (constant on screen at any distance).")]
    [SerializeField, Range(0.0005f, 0.02f)] private float outlineWidth = 0.004f;

    private static readonly int ColorId = Shader.PropertyToID("_OutlineColor");
    private static readonly int WidthId = Shader.PropertyToID("_OutlineWidth");

    // Shell meshes are shared per source mesh; entries whose source was destroyed (model
    // replaced) are released so repeated uploads don't leak GPU memory.
    private static readonly Dictionary<Mesh, Mesh> ShellMeshes = new();

    private CADVisionManipulationService manipulationService;
    private Material material;
    private readonly Dictionary<string, List<GameObject>> shellsById = new();
    private readonly Dictionary<string, CADObject> outlinedObjects = new();
    private readonly HashSet<string> selectedNow = new();
    private readonly List<string> scratch = new();

    /// <summary>
    /// Display toggle (Settings → Outline). Off hides every outline without touching the
    /// selection and without falling back to the color tint; On outlines the current
    /// selection again on the next frame.
    /// </summary>
    public bool ShowOutlines
    {
        get => showOutlines;
        set
        {
            if (showOutlines == value)
                return;
            showOutlines = value;
            if (!value)
                RemoveAllShells();
        }
    }

    private bool showOutlines = true;

    /// <summary>
    /// Draw order of the outline hull (-1 = the shader's Geometry+10). The hull needs the
    /// selected surfaces' depth before it; CADDisplayModeController moves it after its
    /// see-through Wireframe surfaces.
    /// </summary>
    public int RenderQueue
    {
        get => material != null ? material.renderQueue : -1;
        set
        {
            if (material != null)
                material.renderQueue = value;
        }
    }

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();

        Shader shader = Resources.Load<Shader>(ShaderResource);
        if (shader == null || !shader.isSupported)
        {
            Debug.LogWarning($"[CADSelectionOutline] Outline shader '{ShaderResource}' unavailable; keeping color tint.");
            enabled = false;
            return;
        }

        material = new Material(shader) { name = "CAD Selection Outline", enableInstancing = true };
    }

    private void OnEnable()
    {
        if (material != null)
            manipulationService.UseSelectionTint = false;
    }

    private void OnDisable()
    {
        RemoveAllShells();
        if (manipulationService != null)
            manipulationService.UseSelectionTint = true;
    }

    private void OnDestroy()
    {
        if (material != null)
            DestroySafe(material);
    }

    // LateUpdate: after this frame's selection changes (pointer, menu, service calls).
    private void LateUpdate()
    {
        if (!showOutlines)
            return;

        material.SetColor(ColorId, outlineColor);
        material.SetFloat(WidthId, outlineWidth);

        selectedNow.Clear();
        foreach (CADObject cadObject in manipulationService.GetSelectedObjects())
        {
            selectedNow.Add(cadObject.id);
            // New selection, or the same ID now points at a different object (model replaced).
            if (!outlinedObjects.TryGetValue(cadObject.id, out CADObject outlined) || outlined != cadObject)
            {
                RemoveShells(cadObject.id);
                AddShells(cadObject);
            }
        }

        scratch.Clear();
        foreach (string id in shellsById.Keys)
        {
            if (!selectedNow.Contains(id))
                scratch.Add(id);
        }

        foreach (string id in scratch)
            RemoveShells(id);
    }

    private void RemoveAllShells()
    {
        foreach (string id in new List<string>(shellsById.Keys))
            RemoveShells(id);
    }

    private void AddShells(CADObject cadObject)
    {
        ReleaseDeadShellMeshes();

        var shells = new List<GameObject>();
        // Include inactive children: hidden parts get a shell that appears when shown again.
        foreach (MeshFilter filter in cadObject.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.GetComponent<CADVisualOverlay>() != null ||
                !filter.TryGetComponent(out MeshRenderer sourceRenderer) ||
                filter.sharedMesh == null)
            {
                continue;
            }

            Mesh shellMesh = GetShellMesh(filter.sharedMesh);
            if (shellMesh == null)
                continue;

            var shell = new GameObject(ShellName);
            shell.layer = filter.gameObject.layer;
            shell.transform.SetParent(filter.transform, false);
            shell.AddComponent<CADVisualOverlay>();
            shell.AddComponent<MeshFilter>().sharedMesh = shellMesh;
            var renderer = shell.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.enabled = sourceRenderer.enabled;
            shells.Add(shell);
        }

        shellsById[cadObject.id] = shells;
        outlinedObjects[cadObject.id] = cadObject;
    }

    private void RemoveShells(string id)
    {
        if (shellsById.TryGetValue(id, out List<GameObject> shells))
        {
            foreach (GameObject shell in shells)
            {
                if (shell != null)
                    DestroySafe(shell);
            }
        }

        shellsById.Remove(id);
        outlinedObjects.Remove(id);
    }

    // Same positions and triangles, normals averaged over vertices that share a position.
    // SolidWorks exports split vertices at hard edges (≈2× duplicates); extruding along the split
    // normals would crack the hull open at every edge.
    private static Mesh GetShellMesh(Mesh source)
    {
        if (ShellMeshes.TryGetValue(source, out Mesh cached) && cached != null)
            return cached;

        if (!source.isReadable)
        {
            Debug.LogWarning($"[CADSelectionOutline] Mesh '{source.name}' is not readable; no outline for it.");
            return null;
        }

        Vector3[] vertices = source.vertices;
        Vector3[] normals = source.normals;

        // All submeshes as one (the shell uses a single material).
        var triangles = new List<int>();
        for (int s = 0; s < source.subMeshCount; s++)
        {
            if (source.GetTopology(s) == MeshTopology.Triangles)
                triangles.AddRange(source.GetTriangles(s));
        }

        // Missing normals: derive from faces locally (never modify the imported mesh).
        if (normals == null || normals.Length != vertices.Length)
        {
            normals = new Vector3[vertices.Length];
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                Vector3 face = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                normals[a] += face;
                normals[b] += face;
                normals[c] += face;
            }
        }

        // Weld by quantized position (0.01 mm in mesh units is far below tessellation spacing).
        var sums = new Dictionary<Vector3Int, Vector3>();
        var keys = new Vector3Int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i] * 100000f;
            var key = new Vector3Int(Mathf.RoundToInt(v.x), Mathf.RoundToInt(v.y), Mathf.RoundToInt(v.z));
            keys[i] = key;
            sums[key] = sums.TryGetValue(key, out Vector3 sum) ? sum + normals[i] : normals[i];
        }

        var smoothed = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 n = sums[keys[i]];
            smoothed[i] = n.sqrMagnitude > 1e-12f ? n.normalized : normals[i];
        }

        var shell = new Mesh
        {
            name = source.name + " (outline)",
            indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
        };
        shell.vertices = vertices;
        shell.normals = smoothed;
        shell.SetTriangles(triangles, 0);
        shell.bounds = source.bounds;
        shell.UploadMeshData(true); // GPU-only from here on; saves CPU memory.

        ShellMeshes[source] = shell;
        return shell;
    }

    // Destroy is not allowed outside Play mode (EditMode tests); DestroyImmediate is.
    private static void DestroySafe(Object obj)
    {
        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }

    private static void ReleaseDeadShellMeshes()
    {
        List<Mesh> dead = null;
        foreach (KeyValuePair<Mesh, Mesh> entry in ShellMeshes)
        {
            if (entry.Key == null || entry.Value == null)
                (dead ??= new List<Mesh>()).Add(entry.Key);
        }

        if (dead == null)
            return;

        foreach (Mesh key in dead)
        {
            if (ShellMeshes.TryGetValue(key, out Mesh shell) && shell != null)
                DestroySafe(shell);
            ShellMeshes.Remove(key);
        }
    }
}

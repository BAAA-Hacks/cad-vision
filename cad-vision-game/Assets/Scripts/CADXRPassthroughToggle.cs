using UnityEngine;

/// <summary>
/// TEMP: toggles the scene's existing Meta passthrough layer at runtime.
/// Left controller X is test input only; UI/menus should call TogglePassthrough or
/// SetPassthroughEnabled. Virtual mode shows a white floor and blue sky;
/// passthrough mode restores the original camera backgrounds and hides the floor.
/// </summary>
[DisallowMultipleComponent]
public class CADXRPassthroughToggle : MonoBehaviour
{
    [Tooltip("Existing passthrough layer. Defaults to the one in the scene.")]
    [SerializeField] private OVRPassthroughLayer passthroughLayer;

    [Tooltip("Listen for the left controller X button. Disable when driven only by UI.")]
    [SerializeField] private bool useControllerInput = true;

    [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.LTouch;

    private GameObject virtualFloor;
    private CADVirtualLocomotion locomotion;
    private Material floorMaterial;
    private Material skyMaterial;
    private Material originalSkybox;
    private ShadowQuality originalShadows;
    private float originalShadowDistance;
    private UnityEngine.ShadowResolution originalShadowResolution;
    private int originalShadowCascades;
    private float originalCascadeSplit;
    private int originalPixelLights;
    private bool environmentCaptured;
    private Mesh floorMesh;
    private Camera[] environmentCameras;
    private CameraClearFlags[] originalClearFlags;
    private Color[] originalBackgrounds;

    public bool IsPassthroughEnabled { get; private set; }
    public Transform VirtualFloor => virtualFloor != null ? virtualFloor.transform : null;

    private void Start()
    {
        if (passthroughLayer == null)
            passthroughLayer = FindAnyObjectByType<OVRPassthroughLayer>();

        if (passthroughLayer == null)
        {
            Debug.LogWarning("[CADXRPassthroughToggle] No OVRPassthroughLayer in the scene; toggle disabled.");
            return;
        }

        CreateVirtualEnvironment();

        // Read the real state instead of assuming it.
        IsPassthroughEnabled = passthroughLayer.isActiveAndEnabled && !passthroughLayer.hidden;
        if (virtualFloor != null)
        {
            locomotion = GetComponent<CADVirtualLocomotion>();
            if (locomotion == null) locomotion = gameObject.AddComponent<CADVirtualLocomotion>();
            locomotion.Initialize(FindAnyObjectByType<OVRCameraRig>(), virtualFloor.transform);
        }
        ApplyEnvironment();
        Debug.Log($"[CADXRPassthroughToggle] Started; passthrough {(IsPassthroughEnabled ? "on" : "off")} " +
            $"(layer '{passthroughLayer.name}').");
    }

    private void Update()
    {
        // With an LTouch-only mask, OVRInput maps X to Button.One (Button.Three is X only
        // for the combined Touch mask).
        if (useControllerInput && OVRInput.GetDown(OVRInput.Button.One, controller))
            TogglePassthrough();
    }

    public void TogglePassthrough() => SetPassthroughEnabled(!IsPassthroughEnabled);

    public void SetPassthroughEnabled(bool enabled)
    {
        CADMultiplayerCoordinator room = GetComponent<CADMultiplayerCoordinator>();
        if (room != null && room.IsInRoom &&
            enabled != (room.Mode == CADMultiplayerCoordinator.RoomMode.Passthrough))
            return;
        if (passthroughLayer == null)
        {
            Debug.LogWarning("[CADXRPassthroughToggle] No passthrough layer to toggle.");
            return;
        }

        // Also enable the component in case it started disabled; hidden is the actual switch.
        if (enabled && !passthroughLayer.enabled)
            passthroughLayer.enabled = true;

        passthroughLayer.hidden = !enabled;
        IsPassthroughEnabled = enabled;
        ApplyEnvironment();
        Debug.Log($"[CADXRPassthroughToggle] Passthrough {(enabled ? "on" : "off")}.");
    }

    private void CreateVirtualEnvironment()
    {
        OVRCameraRig rig = FindAnyObjectByType<OVRCameraRig>();
        environmentCameras = rig != null
            ? rig.GetComponentsInChildren<Camera>(true)
            : (Camera.main != null ? new[] { Camera.main } : new Camera[0]);
        originalClearFlags = new CameraClearFlags[environmentCameras.Length];
        originalBackgrounds = new Color[environmentCameras.Length];
        float extent = 1000f;
        for (int i = 0; i < environmentCameras.Length; i++)
        {
            originalClearFlags[i] = environmentCameras[i].clearFlags;
            originalBackgrounds[i] = environmentCameras[i].backgroundColor;
            extent = Mathf.Max(extent, environmentCameras[i].farClipPlane * 2f);
        }

        // Resources references ensure these shaders are packaged into the Quest build.
        originalSkybox = RenderSettings.skybox;
        originalShadows = QualitySettings.shadows;
        originalShadowDistance = QualitySettings.shadowDistance;
        originalShadowResolution = QualitySettings.shadowResolution;
        originalShadowCascades = QualitySettings.shadowCascades;
        originalCascadeSplit = QualitySettings.shadowCascade2Split;
        originalPixelLights = QualitySettings.pixelLightCount;
        environmentCaptured = true;
        Shader skyShader = Resources.Load<Shader>("CADVision/CADGradientSky");
        if (skyShader != null)
            skyMaterial = new Material(skyShader) { name = "Virtual Pale Blue Sky" };
        else
            Debug.LogError("[CADXRPassthroughToggle] Gradient sky shader is unavailable.");
        Shader shader = Resources.Load<Shader>("CADVision/CADHazyFloor");
        if (shader == null)
        {
            Debug.LogError("[CADXRPassthroughToggle] White floor shader is unavailable.");
            return;
        }
        floorMaterial = new Material(shader) { name = "Virtual Floor White" };
        floorMaterial.color = Color.white;
        floorMesh = new Mesh { name = "Virtual Floor Mesh" };
        floorMesh.vertices = new[] {
            new Vector3(-extent, 0, -extent), new Vector3(-extent, 0, extent),
            new Vector3(extent, 0, extent), new Vector3(extent, 0, -extent) };
        floorMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        floorMesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        floorMesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
        floorMesh.RecalculateNormals();
        floorMesh.RecalculateBounds();
        virtualFloor = new GameObject("Virtual Environment - White Floor");
        // Floor-level tracking is enabled in this scene. Keep the floor in that space.
        if (rig != null)
        {
            virtualFloor.transform.SetParent(rig.trackingSpace, false);
            // The floor is world geometry: it must not move or turn with locomotion.
            virtualFloor.transform.SetParent(null, true);
        }
        virtualFloor.layer = 2; // Ignore Raycast; decoration must not intercept CAD selection.
        virtualFloor.AddComponent<MeshFilter>().sharedMesh = floorMesh;
        MeshRenderer renderer = virtualFloor.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = floorMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = true;
    }

    private void ApplyEnvironment()
    {
        if (locomotion != null) locomotion.SetVirtualMode(!IsPassthroughEnabled);
        if (virtualFloor != null) virtualFloor.SetActive(!IsPassthroughEnabled);
        if (environmentCaptured)
        {
            RenderSettings.skybox = IsPassthroughEnabled ? originalSkybox : skyMaterial;
            QualitySettings.shadows = IsPassthroughEnabled ? ShadowQuality.Disable : ShadowQuality.All;
            QualitySettings.shadowDistance = IsPassthroughEnabled
                ? originalShadowDistance : 12f;
            QualitySettings.shadowResolution = IsPassthroughEnabled
                ? originalShadowResolution : UnityEngine.ShadowResolution.VeryHigh;
            QualitySettings.shadowCascades = IsPassthroughEnabled ? originalShadowCascades : 2;
            QualitySettings.shadowCascade2Split = IsPassthroughEnabled ? originalCascadeSplit : 0.3f;
            QualitySettings.pixelLightCount = IsPassthroughEnabled
                ? originalPixelLights : Mathf.Max(1, originalPixelLights);
        }
        if (environmentCameras == null) return;
        for (int i = 0; i < environmentCameras.Length; i++)
        {
            Camera camera = environmentCameras[i];
            if (camera == null) continue;
            camera.clearFlags = IsPassthroughEnabled ? originalClearFlags[i] : (skyMaterial != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor);
            camera.backgroundColor = IsPassthroughEnabled
                ? originalBackgrounds[i] : new Color(0.91f, 0.95f, 0.98f, 1f);
        }
    }

    private void OnDestroy()
    {
        if (locomotion != null) { locomotion.SetVirtualMode(false); Destroy(locomotion); }
        if (environmentCaptured)
        {
            RenderSettings.skybox = originalSkybox;
            QualitySettings.shadows = originalShadows;
            QualitySettings.shadowDistance = originalShadowDistance;
            QualitySettings.shadowResolution = originalShadowResolution;
            QualitySettings.shadowCascades = originalShadowCascades;
            QualitySettings.shadowCascade2Split = originalCascadeSplit;
            QualitySettings.pixelLightCount = originalPixelLights;
        }
        if (skyMaterial != null) Destroy(skyMaterial);
        if (environmentCameras != null)
            for (int i = 0; i < environmentCameras.Length; i++)
                if (environmentCameras[i] != null)
                {
                    environmentCameras[i].clearFlags = originalClearFlags[i];
                    environmentCameras[i].backgroundColor = originalBackgrounds[i];
                }
        if (virtualFloor != null) Destroy(virtualFloor);
        if (floorMaterial != null) Destroy(floorMaterial);
        if (floorMesh != null) Destroy(floorMesh);
    }
}

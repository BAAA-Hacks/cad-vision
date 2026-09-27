using UnityEngine;
using UnityEngine.Rendering;
using Oculus.Interaction;
using Oculus.Interaction.Locomotion;

/// <summary>Controller locomotion on the virtual floor, independent of CAD transforms.</summary>
[DisallowMultipleComponent]
public sealed class CADVirtualLocomotion : MonoBehaviour
{
    [SerializeField, Min(0f)] private float walkSpeed = 1.5f;
    [SerializeField, Range(15f, 90f)] private float turnDegrees = 15f;
    [SerializeField, Min(1f)] private float teleportRange = 10f;
    private OVRCameraRig rig;
    private Transform floor;
    private Vector3 roomPosition;
    private Quaternion roomRotation;
    private bool virtualMode;
    private bool inputArmed;
    private bool turnArmed = true;
    private float nextTurnTime;
    private bool aiming;
    private bool validDestination;
    private Vector3 destination;
    private LineRenderer arc;
    private LineRenderer ring;
    private Material pointerMaterial;
    private readonly Vector3[] arcPoints = new Vector3[65];
    private readonly Vector3[] ringPoints = new Vector3[40];

    public void Initialize(OVRCameraRig cameraRig, Transform floorTransform)
    {
        rig = cameraRig;
        floor = floorTransform;
        DisableDuplicateLocomotion();
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) return;
        pointerMaterial = new Material(shader) { name = "Teleport Indicator" };
        arc = CreateLine("Teleport Arc", 0.012f, false);
        ring = CreateLine("Teleport Destination", 0.015f, true);
        HidePointer();
    }

    // The comprehensive Meta rig includes another locomotion event pipeline.
    // This app owns movement through this component, so keep SDK movement and its
    // vignette from responding to the same sticks, including while in passthrough.
    private void DisableDuplicateLocomotion()
    {
        if (rig == null) return;
        foreach (var connection in FindObjectsByType<LocomotionEventsConnection>(
            FindObjectsInactive.Include))
            if (connection.gameObject.scene == rig.gameObject.scene) connection.enabled = false;
        foreach (var mover in FindObjectsByType<PlayerLocomotor>(
            FindObjectsInactive.Include))
            if (mover.gameObject.scene == rig.gameObject.scene) mover.enabled = false;
        foreach (var mover in FindObjectsByType<FirstPersonLocomotor>(
            FindObjectsInactive.Include))
            if (mover.gameObject.scene == rig.gameObject.scene) mover.enabled = false;
        foreach (var tunneling in FindObjectsByType<LocomotionTunneling>(
            FindObjectsInactive.Include))
            if (tunneling.gameObject.scene == rig.gameObject.scene) tunneling.enabled = false;
        foreach (var effect in FindObjectsByType<TunnelingEffect>(
            FindObjectsInactive.Include))
            if (effect.gameObject.scene == rig.gameObject.scene)
            {
                effect.AlphaStrength = 0f;
                effect.UserFOV = 360f;
                effect.enabled = false;
            }
    }

    private LineRenderer CreateLine(string name, float width, bool loop)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(transform, false);
        obj.layer = 2; // No colliders, no CAD identity, and ignored by selection rays.
        var line = obj.AddComponent<LineRenderer>();
        line.sharedMaterial = pointerMaterial;
        line.useWorldSpace = true;
        line.widthMultiplier = width;
        line.loop = loop;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.numCapVertices = 4;
        return line;
    }

    public void SetVirtualMode(bool enabled)
    {
        if (enabled == virtualMode || rig == null) return;
        if (enabled)
        {
            roomPosition = rig.transform.localPosition;
            roomRotation = rig.transform.localRotation;
        }
        else
        {
            // Remove only artificial locomotion. The headset's physical tracked pose remains.
            rig.transform.localPosition = roomPosition;
            rig.transform.localRotation = roomRotation;
        }
        virtualMode = enabled;
        inputArmed = false;
        turnArmed = true;
        CancelTeleport();
    }

    private void LateUpdate()
    {
        if (!virtualMode || rig == null || floor == null) return;
        if (!OVRManager.hasInputFocus || !OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch))
        {
            inputArmed = false;
            CancelTeleport();
            return;
        }
        Vector2 left = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 right = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        if (!inputArmed)
        {
            inputArmed = left.magnitude < 0.2f && right.magnitude < 0.2f;
            return;
        }
        if (aiming)
        {
            UpdateArc();
            if (right.y < -0.5f || Mathf.Abs(right.x) > 0.7f)
            {
                CancelTeleport();
                inputArmed = false;
            }
            else if (right.magnitude < 0.25f)
            {
                if (validDestination)
                    rig.transform.position += Vector3.ProjectOnPlane(
                        destination - rig.centerEyeAnchor.position, floor.up);
                CancelTeleport();
            }
            return;
        }
        if (right.y > 0.7f && Mathf.Abs(right.x) < 0.4f && arc != null)
        {
            aiming = true;
            UpdateArc();
            return;
        }
        if (Mathf.Abs(right.x) < 0.25f) turnArmed = true;
        if (turnArmed && Time.unscaledTime >= nextTurnTime &&
            Mathf.Abs(right.x) > 0.7f && Mathf.Abs(right.y) < 0.5f)
        {
            rig.transform.RotateAround(rig.centerEyeAnchor.position, floor.up,
                Mathf.Sign(right.x) * turnDegrees);
            turnArmed = false;
            nextTurnTime = Time.unscaledTime + 0.35f;
        }
        float magnitude = Mathf.Clamp01(left.magnitude);
        if (magnitude <= 0.2f || !OVRInput.GetControllerPositionTracked(OVRInput.Controller.LTouch)) return;
        Vector3 forward = Vector3.ProjectOnPlane(rig.centerEyeAnchor.forward, floor.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(rig.transform.forward, floor.up);
        forward.Normalize();
        Vector3 rightDirection = Vector3.Cross(floor.up, forward).normalized;
        Vector3 direction = (forward * left.y + rightDirection * left.x).normalized;
        Vector3 delta = direction * ((magnitude - 0.2f) / 0.8f) * walkSpeed * Mathf.Min(Time.deltaTime, 0.05f);
        // Keep the player's body from walking through solid CAD geometry.
        GetBody(rig.centerEyeAnchor.position, out Vector3 bottom, out Vector3 top);
        if (Physics.CapsuleCast(bottom, top, 0.2f, direction, out RaycastHit hit,
            delta.magnitude + 0.03f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            delta = direction * Mathf.Max(0f, hit.distance - 0.03f);
        rig.transform.position += delta;
    }

    private void UpdateArc()
    {
        validDestination = false;
        Transform hand = rig.rightControllerAnchor;
        if (hand == null || arc == null) { CancelTeleport(); return; }
        Vector3 origin = hand.position;
        Vector3 velocity = hand.forward * 7f;
        Vector3 gravity = -floor.up * 9.81f;
        Plane ground = new Plane(floor.up, floor.position);
        Vector3 previous = origin;
        arcPoints[0] = origin;
        int count = 1;
        for (int i = 1; i < arcPoints.Length; i++)
        {
            float time = i * 0.05f;
            Vector3 point = origin + velocity * time + 0.5f * gravity * time * time;
            Vector3 segment = point - previous;
            float length = segment.magnitude;
            Ray ray = new Ray(previous, segment.normalized);
            bool hitsFloor = ground.Raycast(ray, out float floorDistance) && floorDistance <= length;
            float travel = hitsFloor ? floorDistance : length;
            if (Physics.Raycast(ray, out RaycastHit obstruction, travel,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                arcPoints[count++] = obstruction.point;
                break;
            }
            if (hitsFloor)
            {
                destination = ray.GetPoint(floorDistance);
                arcPoints[count++] = destination;
                Vector3 offset = Vector3.ProjectOnPlane(destination - rig.centerEyeAnchor.position, floor.up);
                GetBody(rig.centerEyeAnchor.position + offset, out Vector3 bottom, out Vector3 top);
                validDestination = offset.magnitude <= teleportRange && !Physics.CheckCapsule(
                    bottom, top, 0.2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                break;
            }
            arcPoints[count++] = point;
            previous = point;
        }
        Color color = validDestination ? new Color(0.05f, 0.65f, 1f) : new Color(1f, 0.2f, 0.15f);
        arc.startColor = arc.endColor = color;
        arc.positionCount = count;
        for (int i = 0; i < count; i++) arc.SetPosition(i, arcPoints[i]);
        arc.enabled = true;
        ring.enabled = validDestination;
        if (!validDestination) return;
        ring.startColor = ring.endColor = color;
        ring.positionCount = ringPoints.Length;
        for (int i = 0; i < ringPoints.Length; i++)
        {
            float angle = i * Mathf.PI * 2f / ringPoints.Length;
            ringPoints[i] = destination + floor.up * 0.015f +
                (floor.right * Mathf.Cos(angle) + floor.forward * Mathf.Sin(angle)) * 0.25f;
        }
        ring.SetPositions(ringPoints);
    }

    private void GetBody(Vector3 head, out Vector3 bottom, out Vector3 top)
    {
        Plane ground = new Plane(floor.up, floor.position);
        Vector3 feet = ground.ClosestPointOnPlane(head);
        float height = Mathf.Max(0.5f, Vector3.Dot(head - feet, floor.up));
        bottom = feet + floor.up * 0.23f;
        top = feet + floor.up * Mathf.Max(0.23f, height - 0.2f);
    }

    private void HidePointer()
    {
        if (arc != null) arc.enabled = false;
        if (ring != null) ring.enabled = false;
    }
    private void CancelTeleport() { aiming = false; validDestination = false; HidePointer(); }
    private void OnDisable() { SetVirtualMode(false); CancelTeleport(); }
    private void OnDestroy()
    {
        if (arc != null) Destroy(arc.gameObject);
        if (ring != null) Destroy(ring.gameObject);
        if (pointerMaterial != null) Destroy(pointerMaterial);
    }
}

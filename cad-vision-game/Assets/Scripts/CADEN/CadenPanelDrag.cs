using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Background controller grabs use CAD's exact state machine and CADGrabSession.</summary>
public sealed class CadenPanelDrag : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler
{
    public Transform Panel;
    private readonly CADPointerStateMachine machine = new CADPointerStateMachine();
    private readonly CADGrabSession session = new CADGrabSession();
    private RayInteractor ray;
    private CadenPanel panel;
    private CADPointerInteraction cadPointer;
    private int pointer;
    private bool pressed;

    public void OnPointerDown(PointerEventData data)
    {
        if (pressed || Panel == null || data.button != PointerEventData.InputButton.Left) return;
        foreach (var candidate in FindObjectsByType<RayInteractor>(FindObjectsInactive.Exclude))
        {
            if (candidate.Identifier == data.pointerId && candidate.isActiveAndEnabled &&
                candidate.TryGetComponent(out ControllerRef controller))
            { ray = candidate; break; }
        }
        if (ray == null) return;
        panel = Panel.GetComponent<CadenPanel>();
        if (panel == null || !panel.BeginPanelDrag(data.pointerId)) { ray = null; return; }
        pointer = data.pointerId; pressed = true;
        cadPointer = FindAnyObjectByType<CADPointerInteraction>();
        cadPointer?.ConfigureMovement(machine, session);
        var pose = new Pose(ray.Origin, ray.Rotation);
        Vector3 hit = ray.CollisionInfo.HasValue ? ray.CollisionInfo.Value.Point : Panel.position;
        // Local draggable classification only; panel remains UI and never changes CAD selection.
        machine.Down(CADPointerTargetKind.Cad, null, hit, pose, Time.unscaledTime);
    }

    private void LateUpdate()
    {
        if (!pressed) return;
        if (ray == null || !ray.isActiveAndEnabled || ray.State != InteractorState.Select || Panel == null)
        { Release(); return; }
        cadPointer?.ConfigureMovement(machine, session);
        var pose = new Pose(ray.Origin, ray.Rotation);
        if (machine.Move(pose, Time.unscaledTime) == CADPointerStateMachine.Intent.BeginDrag)
            session.BeginStandalone(Panel, pose, machine.PressHitPoint);
        if (session.IsActive && !session.Update(pose)) Release();
    }

    // Consume uGUI dragging; spatial motion is driven by the real controller pose above.
    public void OnBeginDrag(PointerEventData data) { }
    public void OnDrag(PointerEventData data) { }
    public void OnEndDrag(PointerEventData data) { if (data.pointerId == pointer) Release(); }
    public void OnPointerUp(PointerEventData data) { if (data.pointerId == pointer) Release(); }
    private void OnDisable() => Release();
    private void Release()
    {
        if (pressed && panel != null) panel.EndPanelDrag(pointer);
        pressed = false; ray = null;
        machine.Cancel(); session.End();
    }
}

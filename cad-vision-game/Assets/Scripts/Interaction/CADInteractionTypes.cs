using UnityEngine;

/// <summary>What a pointer is over when it activates.</summary>
public enum CADPointerTargetKind
{
    None, // Empty world: nothing interactive under the pointer.
    Cad,  // A registered CAD object's collider (via its RayInteractable).
    Ui,   // Any other interactive target (menus, panels): never clears selection.
}

/// <summary>Raised when the already-selected CAD object is activated again.</summary>
public readonly struct CADContextMenuRequest
{
    public readonly string TargetId;      // Exact CAD ID the menu acts on (the resolved selection).
    public readonly Vector3 AnchorPoint;  // World point on/near the visible geometry.
    public readonly bool AnchorIsHitPoint; // False: AnchorPoint is a fallback (bounds center).

    public CADContextMenuRequest(string targetId, Vector3 anchorPoint, bool anchorIsHitPoint)
    {
        TargetId = targetId;
        AnchorPoint = anchorPoint;
        AnchorIsHitPoint = anchorIsHitPoint;
    }
}

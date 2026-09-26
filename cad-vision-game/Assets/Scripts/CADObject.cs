using UnityEngine;

public class CADObject : MonoBehaviour
{
    public string id;

    public Vector3 OriginalPosition { get; private set; }
    public Quaternion OriginalRotation { get; private set; }
    public Vector3 OriginalScale { get; private set; }

    // Imported/scene Transform parent and sibling slot. Stays fixed while the object is
    // temporarily detached, so reset/reattach can restore the original hierarchy.
    public Transform OriginalParent { get; private set; }
    public int OriginalSiblingIndex { get; private set; }

    private bool originalCaptured;

    void Awake()
    {
        // Scene-placed objects capture here; runtime imports capture explicitly once final.
        if (!originalCaptured)
            CaptureOriginalTransform();
    }

    // Records the current parent and local transform as the reset/reattach target.
    public void CaptureOriginalTransform()
    {
        OriginalParent = transform.parent;
        OriginalSiblingIndex = transform.GetSiblingIndex();
        OriginalPosition = transform.localPosition;
        OriginalRotation = transform.localRotation;
        OriginalScale = transform.localScale;
        originalCaptured = true;
    }

    public void ResetTransform()
    {
        transform.localPosition = OriginalPosition;
        transform.localRotation = OriginalRotation;
        transform.localScale = OriginalScale;
    }
}

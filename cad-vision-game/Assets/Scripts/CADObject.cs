using UnityEngine;

public class CADObject : MonoBehaviour
{
    public string id;

    public Vector3 OriginalPosition { get; private set; }
    public Quaternion OriginalRotation { get; private set; }
    public Vector3 OriginalScale { get; private set; }

    private bool originalCaptured;

    void Awake()
    {
        // Scene-placed objects capture here; runtime imports capture explicitly once final.
        if (!originalCaptured)
            CaptureOriginalTransform();
    }

    // Records the current local transform as the ResetTransform target.
    public void CaptureOriginalTransform()
    {
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

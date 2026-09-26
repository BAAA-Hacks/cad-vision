using UnityEngine;

public class CADObject : MonoBehaviour
{
    public string id;

    public Vector3 OriginalPosition { get; private set; }
    public Quaternion OriginalRotation { get; private set; }
    public Vector3 OriginalScale { get; private set; }

    void Awake()
    {
        OriginalPosition = transform.localPosition;
        OriginalRotation = transform.localRotation;
        OriginalScale = transform.localScale;
    }

    public void ResetTransform()
    {
        transform.localPosition = OriginalPosition;
        transform.localRotation = OriginalRotation;
        transform.localScale = OriginalScale;
    }
}
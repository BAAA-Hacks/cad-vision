using UnityEngine;

public partial class CADVisionManipulationService
{
    /// <summary>Applies a host-accepted local CAD pose without changing local selection.</summary>
    public bool ApplyAcceptedPartState(string id, bool detached, Vector3 position,
        Quaternion rotation, Vector3 scale)
    {
        if (!TryGetLiveObject(id, out CADObject cadObject)) return false;
        if (detached != IsDetached(id))
        {
            if (detached) Detach(id);
            else Reattach(id);
        }
        Transform target = cadObject.transform;
        target.localPosition = position;
        target.localRotation = rotation;
        target.localScale = scale;
        return true;
    }

    /// <summary>Applies the accepted model pose in this headset's aligned world frame.</summary>
    public bool ApplyAcceptedModelState(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (modelRoot == null) return false;
        modelRoot.SetPositionAndRotation(position, rotation);
        modelRoot.localScale = scale;
        return true;
    }
}

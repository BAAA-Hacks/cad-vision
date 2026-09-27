using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>One physical reference frame shared through Meta group-based spatial anchors.</summary>
public sealed class CADSharedAnchor : MonoBehaviour
{
    private OVRSpatialAnchor anchor;
    private GameObject anchorObject;
    private int generation;

    public Guid GroupId { get; private set; }
    public Guid AnchorId => anchor != null && anchor.Created ? anchor.Uuid : Guid.Empty;
    public bool IsReady => anchor != null && anchor.Localized && anchor.IsTracked;
    public Pose WorldPose => anchor != null
        ? new Pose(anchor.transform.position, anchor.transform.rotation) : Pose.identity;
    public string Status { get; private set; } = "Not aligned";

    public async Task<bool> CreateAndShareAsync(Pose desiredPose)
    {
        ResetAnchor();
        int current = generation;
        GroupId = Guid.NewGuid();
        Status = "Creating room anchor";
        anchorObject = new GameObject("CAD Shared Room Anchor");
        anchorObject.transform.SetPositionAndRotation(desiredPose.position, desiredPose.rotation);
        anchor = anchorObject.AddComponent<OVRSpatialAnchor>();
        if (!await anchor.WhenCreatedAsync() || current != generation) return Fail("Could not create room anchor");
        if (!await anchor.WhenLocalizedAsync() || current != generation) return Fail("Could not localize room anchor");
        Status = "Sharing room anchor";
        var saved = await anchor.SaveAnchorAsync();
        if (current != generation || !saved.Success) return Fail("Could not save room anchor: " + saved.Status);
        var shared = await OVRSpatialAnchor.ShareAsync(new[] { anchor }, GroupId);
        if (current != generation || !shared.Success) return Fail("Could not share room anchor: " + shared.Status);
        Status = "Room aligned";
        return true;
    }

    public async Task<bool> LoadSharedAsync(Guid groupId, Guid anchorId)
    {
        ResetAnchor();
        int current = generation;
        GroupId = groupId;
        Status = "Finding shared room anchor";
        var found = new List<OVRSpatialAnchor.UnboundAnchor>();
        // The group share may arrive shortly after the session connection.
        for (int attempt = 0; attempt < 15 && current == generation; attempt++)
        {
            var result = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupId, found);
            if (current != generation) return false;
            if (result.Success)
            {
                foreach (var unbound in found)
                {
                    if (unbound.Uuid != anchorId) continue;
                    Status = "Localizing shared room";
                    if (!unbound.Localized && !await unbound.LocalizeAsync(10))
                        return Fail("Could not localize shared room anchor");
                    if (!unbound.TryGetPose(out Pose pose))
                        return Fail("Shared room anchor has no tracked pose");
                    anchorObject = new GameObject("CAD Shared Room Anchor");
                    anchorObject.transform.SetPositionAndRotation(pose.position, pose.rotation);
                    anchor = anchorObject.AddComponent<OVRSpatialAnchor>();
                    unbound.BindTo(anchor);
                    Status = "Room aligned";
                    return true;
                }
            }
            await Task.Delay(1000);
        }
        return Fail("Shared room anchor was not found. Check Enhanced Spatial Services and the room scan.");
    }

    private bool Fail(string message)
    {
        Status = message;
        Debug.LogWarning("[CADSharedAnchor] " + message);
        return false;
    }

    public void ResetAnchor()
    {
        generation++;
        anchor = null;
        if (anchorObject != null) Destroy(anchorObject);
        anchorObject = null;
        GroupId = Guid.Empty;
        Status = "Not aligned";
    }

    private void OnDestroy() => ResetAnchor();
}

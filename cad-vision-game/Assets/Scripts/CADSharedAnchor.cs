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
        // Group sharing uploads the anchor itself, so try sharing straight away (one server round
        // trip); only if that fails, save it first and share again.
        var shared = await OVRSpatialAnchor.ShareAsync(new[] { anchor }, GroupId);
        if (current != generation) return false;
        if (!shared.Success)
        {
            Debug.Log($"[CADSharedAnchor] Direct share failed ({shared.Status}); saving the anchor first.");
            var saved = await anchor.SaveAnchorAsync();
            if (current != generation || !saved.Success) return Fail(Explain("save the room anchor", saved.Status));
            shared = await OVRSpatialAnchor.ShareAsync(new[] { anchor }, GroupId);
            if (current != generation || !shared.Success) return Fail(Explain("share the room anchor", shared.Status));
        }
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
        object lastFailure = null;
        for (int attempt = 0; attempt < LoadAttempts && current == generation; attempt++)
        {
            var result = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupId, found);
            if (current != generation) return false;
            if (!result.Success) lastFailure = result.Status;
            if (result.Success)
            {
                foreach (var unbound in found)
                {
                    if (unbound.Uuid != anchorId) continue;
                    Status = "Localizing shared room";
                    if (!unbound.Localized && !await unbound.LocalizeAsync(LocalizeTimeoutSeconds))
                        return Fail("Could not find the host's room here. Stand where the host is, look around the room, " +
                            "and make sure both headsets have Enhanced Spatial Services on and the same space set up.");
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
        return Fail(lastFailure != null
            ? Explain("load the host's room anchor", lastFailure)
            : "The host's room anchor never arrived. Both headsets need internet and Enhanced Spatial Services " +
              "(headset Settings → Privacy) turned on.");
    }

    private const int LoadAttempts = 30;          // About 30 s of retries for the share to arrive.
    private const double LocalizeTimeoutSeconds = 20;

    /// <summary>A readable reason for a failed anchor save/share/load (Meta result codes).</summary>
    public static string Explain(string action, object status)
    {
        int code;
        try { code = Convert.ToInt32(status); } catch { code = 0; }
        switch (code)
        {
            case -2000: // Failure_SpaceCloudStorageDisabled
                return $"Could not {action}: Enhanced Spatial Services is off. Turn it on in the headset's Settings → Privacy.";
            case -2001: // Failure_SpaceMappingInsufficient
                return $"Could not {action}: the headset doesn't know this room well enough. Look around the room for a few seconds and try again.";
            case -2002: // Failure_SpaceLocalizationFailed
                return $"Could not {action}: the headset couldn't find its place in the room. Make sure the space is set up, then try again.";
            case -2003: // Failure_SpaceNetworkTimeout
            case -2004: // Failure_SpaceNetworkRequestFailed
                return $"Could not {action}: Meta's anchor service can't be reached. Check the headset's internet connection.";
            default:
                return $"Could not {action} ({status}).";
        }
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

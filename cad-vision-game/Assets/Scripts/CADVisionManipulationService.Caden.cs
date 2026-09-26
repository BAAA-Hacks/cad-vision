using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

public partial class CADVisionManipulationService
{
    // The integration adapter passes IDs. Unity state stays inside this service.
    public bool HasCadenMapping(IReadOnlyDictionary<string, GameObject> registry) => registry.Count > 0 &&
        registry.All(pair => importedIds.Contains(pair.Key) && TryGetLiveObject(pair.Key, out var obj) && obj.gameObject == pair.Value);

    public sealed class CadenViewBackup
    {
        internal sealed class Pose
        {
            internal Transform Target, Parent;
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal int Sibling;
            internal bool Active;
            internal Pose(Transform target)
            {
                Target = target; Parent = target.parent; Position = target.localPosition;
                Rotation = target.localRotation; Scale = target.localScale;
                Sibling = target.GetSiblingIndex(); Active = target.gameObject.activeSelf;
            }
            internal void Restore()
            {
                if (Target == null) throw new InvalidOperationException("Object disappeared during view rollback.");
                Target.localPosition = Position; Target.localRotation = Rotation; Target.localScale = Scale;
                Target.SetSiblingIndex(Sibling); Target.gameObject.SetActive(Active);
            }
            internal void Write(BinaryWriter writer)
            {
                writer.Write(Target.GetEntityId().ToString()); writer.Write(Parent == null ? "" : Parent.GetEntityId().ToString());
                writer.Write(Position.x); writer.Write(Position.y); writer.Write(Position.z);
                writer.Write(Rotation.x); writer.Write(Rotation.y); writer.Write(Rotation.z); writer.Write(Rotation.w);
                writer.Write(Scale.x); writer.Write(Scale.y); writer.Write(Scale.z); writer.Write(Sibling); writer.Write(Active);
            }
        }
        internal Pose Root;
        internal readonly List<Pose> Poses = new List<Pose>();
        internal string[] Selected, Detached;
        internal string Scope;
        internal bool Multi, Follows;
        public string Fingerprint { get; internal set; }
    }

    public CadenViewBackup CaptureCadenView(IEnumerable<string> cadIds)
    {
        var ids = cadIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var state = new CadenViewBackup {
            Root = modelRoot == null ? null : new CadenViewBackup.Pose(modelRoot),
            Selected = GetSelectedIds().OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Detached = detachedIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Scope = CurrentScopeId, Multi = IsMultiSelectActive, Follows = viewFollowsScope
        };
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            writer.Write(state.Scope ?? ""); writer.Write(state.Multi); writer.Write(state.Follows);
            writer.Write(state.Selected.Length); foreach (var id in state.Selected) writer.Write(id);
            writer.Write(state.Detached.Length); foreach (var id in state.Detached) writer.Write(id);
            state.Root?.Write(writer);
            foreach (var id in ids)
            {
                if (!TryGetLiveObject(id, out var obj)) throw new InvalidOperationException("CAD mapping disappeared: " + id);
                var pose = new CadenViewBackup.Pose(obj.transform); state.Poses.Add(pose);
                writer.Write(id); pose.Write(writer);
            }
        }
        using var hash = SHA256.Create(); state.Fingerprint = Convert.ToBase64String(hash.ComputeHash(bytes.ToArray()));
        return state;
    }

    public void RestoreCadenView(CadenViewBackup state)
    {
        foreach (var pose in state.Poses) pose.Target.SetParent(pose.Parent, false);
        state.Root?.Restore();
        foreach (var pose in state.Poses) pose.Restore();
        detachedIds.Clear(); foreach (var id in state.Detached) detachedIds.Add(id);
        SetScope(state.Scope); ClearSelection();
        foreach (var id in state.Selected) AddToSelection(id);
        if (state.Multi) BeginMultiSelect(); else EndMultiSelect();
        viewFollowsScope = state.Follows;
    }

    public void SetCadenIsolation(IEnumerable<string> visibleIds, IEnumerable<string> cadIds)
    {
        var visible = new HashSet<string>(visibleIds, StringComparer.Ordinal);
        // Preserve required physical ancestors without exposing sibling branches.
        foreach (var id in visible.ToArray())
            if (TryGetLiveObject(id, out var obj))
                for (var parent = obj.transform.parent; parent != null; parent = parent.parent)
                    if (parent.TryGetComponent<CADObject>(out var ancestor) && importedIds.Contains(ancestor.id)) visible.Add(ancestor.id);
        viewFollowsScope = false;
        foreach (var id in cadIds)
            if (TryGetLiveObject(id, out var obj)) obj.gameObject.SetActive(visible.Contains(id));
    }

    public void ShowCadenObjects(IEnumerable<string> ids)
    {
        viewFollowsScope = false;
        foreach (var id in ids)
        {
            if (!TryGetLiveObject(id, out var obj)) throw new InvalidOperationException("CAD mapping disappeared: " + id);
            obj.gameObject.SetActive(true);
            for (var parent = obj.transform.parent; parent != null; parent = parent.parent)
            {
                if (parent.TryGetComponent<CADObject>(out var ancestor) && importedIds.Contains(ancestor.id)) parent.gameObject.SetActive(true);
                if (parent == modelRoot) { parent.gameObject.SetActive(true); break; }
            }
        }
    }

    public void DetachCadenForInspection(string id, Vector3 viewerRight)
    {
        if (IsDetached(id)) return; // Retries cannot keep pushing the object further away.
        if (!TryGetLiveObject(id, out var obj) || GetParentId(id) == null)
            throw new InvalidOperationException("Only a mapped object with a CAD parent can be detached.");
        if (!TryGetLiveObject(GetParentId(id), out var parent)) throw new InvalidOperationException("CAD parent is unavailable.");
        Bounds BoundsOf(CADObject target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>(true).Where(r => r.GetComponent<CADVisualOverlay>() == null).ToArray();
            if (renderers.Length == 0) throw new InvalidOperationException("Inspection placement requires geometry bounds.");
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }
        var partBounds = BoundsOf(obj); var parentBounds = BoundsOf(parent);
        var right = viewerRight.normalized;
        if (!float.IsFinite(right.x) || !float.IsFinite(right.y) || !float.IsFinite(right.z) || right.sqrMagnitude < 0.5f)
            throw new InvalidOperationException("A valid headset right direction is required.");
        float Extent(Bounds b) => Vector3.Dot(b.extents, new Vector3(Mathf.Abs(right.x), Mathf.Abs(right.y), Mathf.Abs(right.z)));
        float distance = Mathf.Max(0.1f, Vector3.Dot(parentBounds.center - partBounds.center, right) + Extent(parentBounds) + Extent(partBounds) + 0.1f);
        if (!float.IsFinite(distance) || distance > 2f)
            throw new InvalidOperationException("Inspection offset would exceed two world metres; reduce model review scale first.");
        if (!Detach(id)) throw new InvalidOperationException("Detach failed.");
        SetObjectWorldPose(id, obj.transform.position + right * distance, obj.transform.rotation);
    }

    public void ResetCadenObjects(IEnumerable<string> ids)
    {
        // Caller supplies metadata order, parents first, and explicitly expanded logical descendants.
        foreach (var id in ids)
            if (!TryGetLiveObject(id, out var obj) || !RestoreOriginal(id, obj))
                throw new InvalidOperationException("Could not restore imported pose for " + id);
    }
}

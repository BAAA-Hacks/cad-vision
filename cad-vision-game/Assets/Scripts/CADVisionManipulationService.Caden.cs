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
        internal string[] Selected, Detached, Focused;
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
            Focused = FocusIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Scope = CurrentScopeId, Multi = IsMultiSelectActive, Follows = viewFollowsScope
        };
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            writer.Write(state.Scope ?? ""); writer.Write(state.Multi); writer.Write(state.Follows);
            writer.Write(state.Selected.Length); foreach (var id in state.Selected) writer.Write(id);
            writer.Write(state.Detached.Length); foreach (var id in state.Detached) writer.Write(id);
            writer.Write(state.Focused.Length); foreach (var id in state.Focused) writer.Write(id);
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
        Focus(state.Focused);
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

    /// <summary>
    /// Detaches targets and moves them together to the viewer's right, clear of their assemblies,
    /// keeping their layout relative to each other. One target behaves as a single inspection detach.
    /// </summary>
    public void DetachCadenGroup(IEnumerable<string> ids, Vector3 viewerRight)
    {
        // Already detached is a no-op, so retries cannot keep pushing objects further away.
        var targets = CadenTopmost(ids).Where(id => !IsDetached(id)).ToArray();
        if (targets.Length == 0) return;
        var right = viewerRight.normalized;
        if (!float.IsFinite(right.x) || !float.IsFinite(right.y) || !float.IsFinite(right.z) || right.sqrMagnitude < 0.5f)
            throw new InvalidOperationException("A valid headset right direction is required.");
        Bounds? group = null, parents = null;
        foreach (var id in targets)
        {
            var (part, parent) = CadenDetachable(id);
            group = Encapsulate(group, CadenBounds(part)); parents = Encapsulate(parents, CadenBounds(parent));
        }
        float Extent(Bounds b) => Vector3.Dot(b.extents, new Vector3(Mathf.Abs(right.x), Mathf.Abs(right.y), Mathf.Abs(right.z)));
        float distance = Mathf.Max(0.1f, Vector3.Dot(parents.Value.center - group.Value.center, right) + Extent(parents.Value) + Extent(group.Value) + 0.1f);
        if (!float.IsFinite(distance) || distance > 2f)
            throw new InvalidOperationException("Inspection offset would exceed two world metres; reduce model review scale first.");
        DetachAndOffset(targets.ToDictionary(id => id, _ => right * distance));
    }

    /// <summary>
    /// Explodes targets apart: each moves away from their shared barycenter (mean geometry centre), its
    /// distance from it scaled by 1 + spread. A target sitting on the barycenter is detached but stays put.
    /// </summary>
    public void ExplodeCaden(IEnumerable<string> ids, float spread)
    {
        if (!float.IsFinite(spread) || spread <= 0f) throw new InvalidOperationException("Explode spread must be positive.");
        // Already detached objects keep their pose; with fewer than two left there is nothing to separate.
        var targets = CadenTopmost(ids).Where(id => !IsDetached(id)).ToArray();
        if (targets.Length < 2) return;
        var centers = targets.ToDictionary(id => id, id => CadenBounds(CadenDetachable(id).Part).center);
        var barycenter = centers.Values.Aggregate(Vector3.zero, (sum, center) => sum + center) / centers.Count;
        var offsets = centers.ToDictionary(pair => pair.Key, pair => (pair.Value - barycenter) * spread);
        foreach (var offset in offsets.Values)
            if (!float.IsFinite(offset.x) || !float.IsFinite(offset.y) || !float.IsFinite(offset.z) || offset.magnitude > 2f)
                throw new InvalidOperationException("Explode offset would exceed two world metres; lower spread or reduce model review scale first.");
        DetachAndOffset(offsets);
    }

    private (CADObject Part, CADObject Parent) CadenDetachable(string id)
    {
        if (!TryGetLiveObject(id, out var part) || GetParentId(id) == null)
            throw new InvalidOperationException("Only a mapped object with a CAD parent can be detached: " + id);
        if (!TryGetLiveObject(GetParentId(id), out var parent)) throw new InvalidOperationException("CAD parent is unavailable: " + id);
        return (part, parent);
    }

    // Drops targets whose logical ancestor is also a target: moving the ancestor already carries them.
    private string[] CadenTopmost(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        bool HasTargetAncestor(string id)
        {
            for (var parent = GetParentId(id); parent != null; parent = GetParentId(parent)) if (set.Contains(parent)) return true;
            return false;
        }
        return set.Where(id => !HasTargetAncestor(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private void DetachAndOffset(IReadOnlyDictionary<string, Vector3> offsets)
    {
        foreach (var pair in offsets)
        {
            if (!TryGetLiveObject(pair.Key, out var obj) || !Detach(pair.Key)) throw new InvalidOperationException("Detach failed: " + pair.Key);
            SetObjectWorldPose(pair.Key, obj.transform.position + pair.Value, obj.transform.rotation);
        }
    }

    private static Bounds CadenBounds(CADObject target)
    {
        var renderers = target.GetComponentsInChildren<Renderer>(true).Where(r => r.GetComponent<CADVisualOverlay>() == null).ToArray();
        if (renderers.Length == 0) throw new InvalidOperationException("Inspection placement requires geometry bounds.");
        var bounds = renderers[0].bounds;
        foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }
    private static Bounds Encapsulate(Bounds? total, Bounds next)
    {
        if (total == null) return next;
        var bounds = total.Value; bounds.Encapsulate(next); return bounds;
    }

    public void ResetCadenObjects(IEnumerable<string> ids)
    {
        // Caller supplies metadata order, parents first, and explicitly expanded logical descendants.
        foreach (var id in ids)
            if (!TryGetLiveObject(id, out var obj) || !RestoreOriginal(id, obj))
                throw new InvalidOperationException("Could not restore imported pose for " + id);
    }
}

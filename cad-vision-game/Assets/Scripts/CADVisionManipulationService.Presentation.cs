using System;
using System.Collections.Generic;
using UnityEngine;

// Reset-scale, focus (presentation state) and visible-bounds queries. Everything stays ID
// based; nothing here changes selection, scope, visibility, detach state or hierarchy.
public partial class CADVisionManipulationService
{
    // -------------------------
    // Reset scale
    // -------------------------

    /// <summary>
    /// Restores the object's original scale around its visible center: position (of what the
    /// user sees) and rotation stay. Detached objects get the scale they would have under
    /// their original parent. Assemblies scale with their children.
    /// </summary>
    public bool ResetObjectScale(string id)
    {
        if (!TryGetLiveObject(id, out CADObject cadObject))
            return false;

        Transform target = cadObject.transform;
        Vector3 scale = cadObject.OriginalScale;
        Transform original = cadObject.OriginalParent;
        Transform current = target.parent;
        if (original != null && current != null && original != current &&
            !Mathf.Approximately(current.lossyScale.x, 0f))
        {
            // Uniform CAD scales: same world scale as under the original parent.
            scale *= original.lossyScale.x / current.lossyScale.x;
        }

        Vector3 pivotWorld = TryGetObjectBounds(id, out Bounds bounds) ? bounds.center : target.position;
        SetObjectScaleAroundPoint(id, scale, target.InverseTransformPoint(pivotWorld), pivotWorld);
        return true;
    }

    /// <summary>Reset scale for every selected transform root (children follow their root).</summary>
    public void ResetSelectedScale()
    {
        foreach (string id in GetSelectedTransformRoots())
            ResetObjectScale(id);
    }

    /// <summary>The model root back to its adopted review scale, around the model's visible center.</summary>
    public bool ResetModelScale()
    {
        if (modelRoot == null)
            return false;

        Vector3 pivotWorld = TryGetModelBounds(out Bounds bounds) ? bounds.center : modelRoot.position;
        SetModelScaleAroundPoint(1f, modelRoot.InverseTransformPoint(pivotWorld), pivotWorld);
        return true;
    }

    // -------------------------
    // Focus (presentation only)
    // -------------------------

    private readonly HashSet<string> focusIds = new();

    /// <summary>Raised when the focus set changes (including cleared by model replacement).</summary>
    public event Action FocusChanged;

    public bool IsFocusActive => focusIds.Count > 0;
    public IReadOnlyCollection<string> FocusIds => focusIds;

    /// <summary>
    /// Emphasizes the given objects (and their logical descendants); everything else is shown
    /// as ghost/reference geometry by the display layer. Visibility, selection, scope and
    /// detach state are unchanged.
    /// </summary>
    public void Focus(IEnumerable<string> ids)
    {
        var live = new HashSet<string>();
        foreach (string id in ids)
        {
            if (TryGetLiveObject(id, out _))
                live.Add(id);
        }

        if (live.Count == 0)
        {
            ClearFocus();
            return;
        }

        if (live.SetEquals(focusIds))
            return;

        focusIds.Clear();
        focusIds.UnionWith(live);
        Debug.Log($"Focus: {string.Join(", ", focusIds)}.");
        FocusChanged?.Invoke();
    }

    public void Focus(string id) => Focus(new[] { id });

    /// <summary>Focus on the selection (logical roots, so nested members aren't listed twice).</summary>
    public void FocusSelected() => Focus(GetSelectedLogicalRoots());

    public void ClearFocus()
    {
        if (focusIds.Count == 0)
            return;

        focusIds.Clear();
        Debug.Log("Focus cleared.");
        FocusChanged?.Invoke();
    }

    /// <summary>True if id is a focus target or a logical descendant of one.</summary>
    public bool IsInFocus(string id)
    {
        for (string current = id; current != null; current = GetParentId(current))
        {
            if (focusIds.Contains(current))
                return true;
        }
        return false;
    }

    /// <summary>True if every one of ids is a focus target (the focus is exactly "on" them).</summary>
    public bool IsFocusedOn(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids);
        return set.Count > 0 && set.SetEquals(focusIds);
    }

    // -------------------------
    // Visible bounds
    // -------------------------

    /// <summary>World bounds of the object's visible geometry (enabled renderers, overlays excluded).</summary>
    public bool TryGetObjectBounds(string id, out Bounds bounds)
    {
        bounds = default;
        return TryGetLiveObject(id, out CADObject cadObject) && EncapsulateVisible(cadObject.transform, ref bounds, false);
    }

    /// <summary>Combined world bounds of the selected objects' visible geometry.</summary>
    public bool TryGetSelectionBounds(out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        foreach (CADObject cadObject in GetSelectedObjects())
            any |= EncapsulateVisible(cadObject.transform, ref bounds, any);
        return any;
    }

    private static bool EncapsulateVisible(Transform root, ref Bounds bounds, bool hasBounds)
    {
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
        {
            if (!renderer.enabled || renderer.TryGetComponent(out CADVisualOverlay _))
                continue;

            if (hasBounds)
                bounds.Encapsulate(renderer.bounds);
            else
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
        }
        return hasBounds;
    }
}

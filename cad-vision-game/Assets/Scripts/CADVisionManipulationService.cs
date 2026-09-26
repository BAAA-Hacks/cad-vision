using System;
using System.Collections.Generic;
using UnityEngine;

public class CADVisionManipulationService : MonoBehaviour
{
    [Header("Model")]
    [SerializeField] private Transform modelRoot;

    [Header("Highlighting")]
    [SerializeField] private Color highlightColor = Color.yellow;

    private readonly Dictionary<string, CADObject> objects = new();
    private readonly HashSet<string> selectedIds = new();
    private readonly HashSet<string> highlightedIds = new();

    // Where the current selection was hit, in the selected object's local space, so it
    // follows the object. Cleared with the selection.
    private string selectionPointId;
    private Vector3 selectionPointLocal;

    // Nearest registered CAD ancestor per ID (null for top-level), rebuilt with registration.
    // Uses the same nearest-CADObject rule as collider ownership.
    private readonly Dictionary<string, string> parentIds = new();
    private readonly HashSet<string> idsWithCadChildren = new();

    // Interaction scope: hits resolve to direct CAD children of this ID (null = model level).
    public string CurrentScopeId { get; private set; }
    public event Action ScopeChanged;

    // True when visibility was set by Isolate(id)/scope exit, so ExitScope/ResetScope restore
    // the matching view. Explicit visibility calls (ShowAll, IsolateMany) clear it.
    private bool viewFollowsScope;

    // IDs registered from a Task 2 runtime import (via ReplaceImportedModel). All other
    // registered IDs are hand-placed scene CADObjects discovered at Start.
    private readonly HashSet<string> importedIds = new();

    // Task 3 interaction state only: objects currently reparented away from their original
    // (imported) parent. Their logical CAD parent (parentIds) is unchanged.
    private readonly HashSet<string> detachedIds = new();

    // Runtime adapters use the same accepted objects as desktop manipulation.
    internal IReadOnlyCollection<CADObject> RegisteredObjects => objects.Values;

    // The Inspector root is the scene model root; an imported model's root replaces it while
    // that model is loaded.
    private Transform sceneModelRoot;

    private Vector3 originalModelPosition;
    private Quaternion originalModelRotation;
    private Vector3 originalModelScale;

    private void Awake()
    {
        sceneModelRoot = modelRoot;
    }

    private void Start()
    {
        RegisterObjects();

        if (importedIds.Count > 0)
            return; // An import already adopted its own root.

        if (modelRoot != null)
            CaptureModelRootPose();
        else
            Debug.LogWarning("Manipulation Service has no model root.");
    }

    private void CaptureModelRootPose()
    {
        originalModelPosition = modelRoot.localPosition;
        originalModelRotation = modelRoot.localRotation;
        originalModelScale = modelRoot.localScale;
    }

    // -------------------------
    // Object Registry
    // -------------------------

    // Scene discovery for hand-placed CADObjects. Runtime imports are registered only through
    // ReplaceImportedModel and are kept as they are here.
    private void RegisterObjects()
    {
        HashSet<CADObject> importedObjects = new();
        foreach (string id in importedIds)
        {
            if (objects.TryGetValue(id, out CADObject imported) && imported != null)
                importedObjects.Add(imported);
        }

        List<string> sceneIds = new();
        foreach (KeyValuePair<string, CADObject> entry in objects)
        {
            if (!importedIds.Contains(entry.Key))
                sceneIds.Add(entry.Key);
        }

        foreach (string id in sceneIds)
            objects.Remove(id);

        CADObject[] cadObjects = FindObjectsByType<CADObject>(
            FindObjectsInactive.Include
        );

        foreach (CADObject cadObject in cadObjects)
        {
            if (importedObjects.Contains(cadObject))
                continue;

            if (string.IsNullOrWhiteSpace(cadObject.id))
            {
                Debug.LogWarning(
                    $"CAD object {cadObject.name} has no ID."
                );

                continue;
            }

            if (!objects.TryAdd(cadObject.id, cadObject))
            {
                Debug.LogWarning(
                    $"Duplicate CAD ID found: {cadObject.id}"
                );
            }
        }

        BuildHierarchy();
        CurrentScopeId = null;
        viewFollowsScope = false;

        Debug.Log($"Registered {objects.Count} CAD objects.");
    }

    private void BuildHierarchy()
    {
        parentIds.Clear();
        idsWithCadChildren.Clear();

        foreach (KeyValuePair<string, CADObject> entry in objects)
        {
            if (entry.Value == null)
                continue;

            string parentId = null;

            // Logical hierarchy: a detached object's current parent is temporary, so walk up
            // from its original parent. Rebuilding while things are detached stays correct.
            Transform start = detachedIds.Contains(entry.Key)
                ? entry.Value.OriginalParent
                : entry.Value.transform.parent;

            for (Transform current = start;
                current != null;
                current = current.parent)
            {
                // Skip unregistered (blank/duplicate ID) CADObjects.
                if (current.TryGetComponent(out CADObject ancestor) &&
                    ancestor.id != null &&
                    objects.TryGetValue(ancestor.id, out CADObject registered) &&
                    registered == ancestor)
                {
                    parentId = ancestor.id;
                    break;
                }
            }

            parentIds[entry.Key] = parentId;
            if (parentId != null)
                idsWithCadChildren.Add(parentId);
        }
    }

    private string GetParentId(string id) =>
        parentIds.TryGetValue(id, out string parentId) ? parentId : null;

    // True when id has registered CAD children (i.e. EnterScope(id) would succeed).
    public bool HasCadChildren(string id) => id != null && idsWithCadChildren.Contains(id);

    // Logical (imported) CAD parent, unaffected by detach; null for top-level objects.
    public string GetLogicalParentId(string id) => id != null ? GetParentId(id) : null;

    public bool IsDetached(string id) => id != null && detachedIds.Contains(id);

    // Destroyed CADObjects (e.g. from a replaced import) count as missing.
    private bool TryGetLiveObject(string id, out CADObject cadObject)
    {
        if (id != null && objects.TryGetValue(id, out cadObject) && cadObject != null)
            return true;

        cadObject = null;
        return false;
    }

    private bool TryGetObject(string id, out CADObject cadObject)
    {
        if (TryGetLiveObject(id, out cadObject))
            return true;

        Debug.LogWarning($"CAD object not found: {id}");
        return false;
    }

    // -------------------------
    // Runtime Import (Task 2)
    // -------------------------

    /// <summary>
    /// Replaces all previously imported CAD objects with a Task 2 runtime model.
    /// Pass a null root or empty objects when the runtime model was cleared.
    /// Scene-placed CADObjects are kept; an imported ID that duplicates one is skipped.
    /// </summary>
    public void ReplaceImportedModel(Transform root, IReadOnlyDictionary<string, GameObject> importedObjects)
    {
        // Selection, highlights and scope may reference the outgoing model; this also
        // ends any active grab and multi-select.
        ClearSelection();
        EndMultiSelect();

        foreach (string id in importedIds)
        {
            objects.Remove(id);
            detachedIds.Remove(id); // Destroyed with the old model root.
        }
        importedIds.Clear();

        // After removing the outgoing IDs, so restoring the view touches only live objects.
        ResetScope();
        viewFollowsScope = false;

        if (root != null && importedObjects != null)
        {
            foreach (KeyValuePair<string, GameObject> entry in importedObjects)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null)
                    continue;

                if (objects.ContainsKey(entry.Key))
                {
                    Debug.LogWarning($"Imported CAD ID {entry.Key} duplicates a scene CAD object; skipped.");
                    continue;
                }

                CADObject cadObject = entry.Value.GetComponent<CADObject>();
                if (cadObject == null)
                    cadObject = entry.Value.AddComponent<CADObject>();

                cadObject.id = entry.Key;
                // Task 2 has finished import and placement (on the root only), so this
                // local transform is the reset target.
                cadObject.CaptureOriginalTransform();

                objects.Add(entry.Key, cadObject);
                importedIds.Add(entry.Key);
            }
        }

        BuildHierarchy();

        modelRoot = importedIds.Count > 0 ? root : sceneModelRoot;
        if (modelRoot != null)
            CaptureModelRootPose();

        Debug.Log(importedIds.Count > 0
            ? $"Imported {importedIds.Count} runtime CAD objects under {root.name}."
            : "No runtime CAD model; imported objects cleared.");
    }

    // -------------------------
    // Selection
    // -------------------------

    public void Select(string id)
    {
        ClearSelection();

        if (!TryGetObject(id, out _))
            return;

        selectedIds.Add(id);
        Highlight(new[] { id });

        Debug.Log($"Selected: {id}");
    }

    // Scope-aware selection for raw collider hits (hitId = the collider's nearest CADObject).
    // Selects the hit's ancestor at the current scope level. A hit outside the current scope
    // moves the scope up to the nearest level that contains it.
    // hitPoint (world space, optional) is stored relative to the resolved selected object,
    // which may be an ancestor assembly of the hit part; see TryGetSelectionPoint.
    public void SelectFromHit(string hitId, Vector3? hitPoint = null)
    {
        if (!TryGetObject(hitId, out _))
            return;

        // Geometry owned directly by the scope object is not at the selectable level.
        if (hitId == CurrentScopeId)
        {
            ClearSelection();
            return;
        }

        string resolved = ResolveSelectable(hitId);
        if (resolved == null)
        {
            string scope = CurrentScopeId;
            while (scope != null && ResolveAtScope(hitId, scope) == null)
                scope = GetParentId(scope);

            viewFollowsScope = false;
            SetScope(scope);
            resolved = ResolveAtScope(hitId, scope);
        }

        Select(resolved);

        if (hitPoint.HasValue && TryGetLiveObject(resolved, out CADObject selected))
        {
            selectionPointId = resolved;
            selectionPointLocal = selected.transform.InverseTransformPoint(hitPoint.Value);
        }
    }

    // World position of the point the current selection was picked at; false when the
    // selection didn't come from a hit (e.g. Select(id) from CADEN) or was cleared.
    public bool TryGetSelectionPoint(out Vector3 worldPoint)
    {
        if (selectionPointId != null && selectedIds.Contains(selectionPointId) &&
            TryGetLiveObject(selectionPointId, out CADObject selected))
        {
            worldPoint = selected.transform.TransformPoint(selectionPointLocal);
            return true;
        }

        worldPoint = default;
        return false;
    }

    // The ancestor-or-self of id that is selectable at the current scope; null if id is
    // outside the current scope (or is the scope object itself).
    public string ResolveSelectable(string id) => ResolveAtScope(id, CurrentScopeId);

    private string ResolveAtScope(string id, string scopeId)
    {
        for (string current = id; current != null; current = GetParentId(current))
        {
            if (current == scopeId)
                return null;

            if (GetParentId(current) == scopeId)
                return current;

            // Interaction only: a detached object is its own selectable unit, so hits on it (or
            // inside a detached subassembly) stop here instead of resolving up to the original
            // assembly. The logical hierarchy (parentIds) is unchanged.
            if (detachedIds.Contains(current))
                return current;
        }

        return null;
    }

    public void ClearSelection()
    {
        selectedIds.Clear();
        selectionPointId = null;
        ClearHighlights();
    }

    public IEnumerable<CADObject> GetSelectedObjects()
    {
        foreach (string id in selectedIds)
        {
            if (TryGetLiveObject(id, out CADObject cadObject))
                yield return cadObject;
        }
    }

    public bool IsSelected(string id) => id != null && selectedIds.Contains(id) && TryGetLiveObject(id, out _);

    // Live selected IDs (a snapshot; the service's set is the only selection store).
    public List<string> GetSelectedIds()
    {
        var ids = new List<string>();
        foreach (CADObject cadObject in GetSelectedObjects())
            ids.Add(cadObject.id);
        return ids;
    }

    // -------------------------
    // Multi-Selection
    // -------------------------

    // While active, pointer clicks toggle objects in/out of the selection instead of replacing
    // it. The selection itself is the same selectedIds set used for single selection.
    public bool IsMultiSelectActive { get; private set; }

    public void BeginMultiSelect()
    {
        if (IsMultiSelectActive)
            return;

        IsMultiSelectActive = true;
        Debug.Log($"Multi-select started ({selectedIds.Count} selected).");
    }

    // Leaves multi-select mode. The selection is kept (still outlined) unless clearSelection.
    public void EndMultiSelect(bool clearSelection = false)
    {
        if (clearSelection)
            ClearSelection();

        if (!IsMultiSelectActive)
            return;

        IsMultiSelectActive = false;
        Debug.Log($"Multi-select ended ({selectedIds.Count} selected).");
    }

    public bool AddToSelection(string id)
    {
        if (!TryGetObject(id, out _) || selectedIds.Contains(id))
            return false;

        selectedIds.Add(id);
        Highlight(new[] { id });
        Debug.Log($"Added to selection: {id} ({selectedIds.Count} selected)");
        return true;
    }

    public bool RemoveFromSelection(string id)
    {
        if (id == null || !selectedIds.Remove(id))
            return false;

        if (highlightedIds.Remove(id) && TryGetLiveObject(id, out CADObject cadObject))
            SetHighlight(cadObject, false);
        if (selectionPointId == id)
            selectionPointId = null;

        Debug.Log($"Removed from selection: {id} ({selectedIds.Count} selected)");
        return true;
    }

    public bool ToggleSelection(string id) =>
        selectedIds.Contains(id) ? RemoveFromSelection(id) : AddToSelection(id);

    /// <summary>
    /// The object a raw collider hit selects, with the same rules as SelectFromHit (scope,
    /// detached units) but without changing any state: a hit outside the current scope
    /// resolves at the nearest enclosing level instead of moving the scope there.
    /// </summary>
    public string ResolveHitTarget(string hitId)
    {
        if (!TryGetLiveObject(hitId, out _) || hitId == CurrentScopeId)
            return null;

        string resolved = ResolveSelectable(hitId);
        if (resolved != null)
            return resolved;

        string scope = CurrentScopeId;
        while (scope != null && ResolveAtScope(hitId, scope) == null)
            scope = GetParentId(scope);
        return ResolveAtScope(hitId, scope);
    }

    // Multi-select click: adds or removes the hit's resolved object. Never changes scope.
    public string ToggleFromHit(string hitId, Vector3? hitPoint = null)
    {
        string resolved = ResolveHitTarget(hitId);
        if (resolved == null)
            return null;

        if (selectedIds.Contains(resolved))
        {
            RemoveFromSelection(resolved);
        }
        else if (AddToSelection(resolved) && hitPoint.HasValue &&
                 TryGetLiveObject(resolved, out CADObject added))
        {
            selectionPointId = resolved;
            selectionPointLocal = added.transform.InverseTransformPoint(hitPoint.Value);
        }

        return resolved;
    }

    /// <summary>
    /// Selected objects that are not moved by another selected object: drops any selected
    /// object whose current Transform ancestor is also selected (so a group transform is never
    /// applied twice). Uses Transform ancestry, not the logical tree: a detached child of a
    /// selected assembly no longer moves with it and stays a root.
    /// </summary>
    public List<string> GetSelectedTransformRoots()
    {
        List<CADObject> selected = new List<CADObject>(GetSelectedObjects());
        var roots = new List<string>();
        foreach (CADObject candidate in selected)
        {
            bool coveredByAnother = false;
            foreach (CADObject other in selected)
            {
                if (other != candidate && candidate.transform.IsChildOf(other.transform))
                {
                    coveredByAnother = true;
                    break;
                }
            }

            if (!coveredByAnother)
                roots.Add(candidate.id);
        }

        return roots;
    }

    /// <summary>
    /// Selected objects with no selected logical (imported) CAD ancestor. Used for hierarchy
    /// operations such as Reset Selected, where an assembly's reset already covers its subtree
    /// (including detached descendants).
    /// </summary>
    public List<string> GetSelectedLogicalRoots()
    {
        var roots = new List<string>();
        foreach (string id in GetSelectedIds())
        {
            bool coveredByAncestor = false;
            for (string parent = GetParentId(id); parent != null; parent = GetParentId(parent))
            {
                if (selectedIds.Contains(parent))
                {
                    coveredByAncestor = true;
                    break;
                }
            }

            if (!coveredByAncestor)
                roots.Add(id);
        }

        return roots;
    }

    // Each logical root once: assemblies with their whole subtree, leaves individually.
    // Detached members are reattached by ResetAssembly/ResetObject.
    public void ResetSelected()
    {
        List<string> roots = GetSelectedLogicalRoots();
        foreach (string id in roots)
        {
            if (HasCadChildren(id))
                ResetAssembly(id);
            else
                ResetObject(id);
        }

        Debug.Log($"Reset selected: {roots.Count} root(s).");
    }

    // Visibility only (IsolateMany): selected objects stay visible, as do the CAD ancestors
    // needed to keep them active. Does not change scope or selection.
    public void IsolateSelected() => IsolateMany(GetSelectedIds());

    // -------------------------
    // Highlighting
    // -------------------------

    // Whole-object color tint on highlight. A visual layer (e.g. CADSelectionOutline) turns this
    // off and draws its own indication; highlight state and ids are tracked either way.
    public bool UseSelectionTint
    {
        get => useSelectionTint;
        set
        {
            if (useSelectionTint == value)
                return;

            useSelectionTint = value;
            foreach (string id in highlightedIds)
            {
                if (TryGetLiveObject(id, out CADObject cadObject))
                    SetHighlight(cadObject, value);
            }
        }
    }

    private bool useSelectionTint = true;

    public void Highlight(IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            if (!TryGetObject(id, out CADObject cadObject))
                continue;

            SetHighlight(cadObject, true);
            highlightedIds.Add(id);
        }
    }

    public void ClearHighlights()
    {
        foreach (string id in highlightedIds)
        {
            if (TryGetLiveObject(id, out CADObject cadObject))
            {
                SetHighlight(cadObject, false);
            }
        }

        highlightedIds.Clear();
    }

    private void SetHighlight(CADObject cadObject, bool highlighted)
    {
        Renderer[] renderers =
            cadObject.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer renderer in renderers)
        {
            // Outline shells / edge lines are presentation, not CAD geometry.
            if (renderer.TryGetComponent(out CADVisualOverlay _))
                continue;

            MaterialPropertyBlock block = new();
            renderer.GetPropertyBlock(block);

            if (highlighted && useSelectionTint)
            {
                // Tint every color property the renderer's materials use:
                // URP _BaseColor, glTFast Built-in baseColorFactor, Built-in _Color.
                bool anySet = false;
                foreach (int property in HighlightColorProperties)
                {
                    if (AnyMaterialHasProperty(renderer, property))
                    {
                        block.SetColor(property, highlightColor);
                        anySet = true;
                    }
                }

                if (!anySet)
                    block.SetColor(ColorProperty, highlightColor);
            }
            else
            {
                block.Clear();
            }

            renderer.SetPropertyBlock(block);
        }
    }

    private static readonly int BaseColorProperty = Shader.PropertyToID("_BaseColor");
    private static readonly int GltfBaseColorProperty = Shader.PropertyToID("baseColorFactor");
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");
    private static readonly int[] HighlightColorProperties =
        { BaseColorProperty, GltfBaseColorProperty, ColorProperty };

    private static bool AnyMaterialHasProperty(Renderer renderer, int property)
    {
        foreach (Material material in renderer.sharedMaterials)
        {
            if (material != null && material.HasProperty(property))
                return true;
        }

        return false;
    }

    // -------------------------
    // Visibility
    // -------------------------

    public void Hide(IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            if (TryGetObject(id, out CADObject cadObject))
                cadObject.gameObject.SetActive(false);
        }
    }

    public void Show(IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            if (TryGetObject(id, out CADObject cadObject))
                cadObject.gameObject.SetActive(true);
        }
    }

    public void ShowAll()
    {
        viewFollowsScope = false;
        SetAllVisible();
    }

    private void SetAllVisible()
    {
        foreach (CADObject cadObject in objects.Values)
        {
            if (cadObject != null)
                cadObject.gameObject.SetActive(true);
        }
    }

    // Visibility only: shows the given objects and their descendants. Never changes scope.
    // Deliberately not an Isolate overload, so Isolate(new[] { id }) can't be confused with
    // the scope-entering Isolate(string).
    public void IsolateMany(IEnumerable<string> ids)
    {
        viewFollowsScope = false;
        SetIsolated(ids);
    }

    private void SetIsolated(IEnumerable<string> ids)
    {
        HashSet<string> isolatedIds = new(ids);

        foreach (CADObject cadObject in objects.Values)
        {
            if (cadObject == null)
                continue;

            bool shouldBeVisible = false;

            foreach (string isolatedId in isolatedIds)
            {
                if (!TryGetLiveObject(isolatedId, out CADObject isolatedObject))
                    continue;

                // Self or a Transform descendant of an isolated object...
                Transform current = cadObject.transform;

                while (current != null)
                {
                    if (current == isolatedObject.transform)
                    {
                        shouldBeVisible = true;
                        break;
                    }

                    current = current.parent;
                }

                // ...or a Transform ancestor of one: deactivating it would hide the isolated
                // object too. Its other CAD children are still hidden individually.
                if (!shouldBeVisible && isolatedObject.transform.IsChildOf(cadObject.transform))
                    shouldBeVisible = true;

                if (shouldBeVisible)
                    break;
            }

            cadObject.gameObject.SetActive(shouldBeVisible);
        }
    }

    // Convenience overloads

    public void Hide(string id)
    {
        Hide(new[] { id });
    }

    public void Show(string id)
    {
        Show(new[] { id });
    }

    // Isolates id and makes it workable: an assembly becomes the interaction scope; a leaf
    // part is selected with its parent as scope (model scope if top-level).
    public void Isolate(string id)
    {
        if (!TryGetObject(id, out _))
            return;

        SetIsolated(new[] { id });
        viewFollowsScope = true;

        if (idsWithCadChildren.Contains(id))
        {
            SetScope(id);
        }
        else
        {
            SetScope(GetParentId(id));
            Select(id);
        }
    }

    // -------------------------
    // Interaction Scope
    // -------------------------

    // Makes id's direct CAD children the selectable level. Does not change visibility.
    public void EnterScope(string id)
    {
        if (!TryGetObject(id, out _))
            return;

        if (!idsWithCadChildren.Contains(id))
        {
            Debug.LogWarning($"Cannot enter scope of {id}: it has no CAD children.");
            return;
        }

        SetScope(id);
    }

    // Moves up one level and selects the assembly that was exited.
    public void ExitScope()
    {
        string exitedId = CurrentScopeId;
        if (exitedId == null)
            return;

        string parentId = GetParentId(exitedId);
        SetScope(parentId);

        if (viewFollowsScope)
        {
            if (parentId != null)
            {
                SetIsolated(new[] { parentId });
            }
            else
            {
                SetAllVisible();
                viewFollowsScope = false;
            }
        }

        Select(exitedId);
    }

    public void ResetScope()
    {
        SetScope(null);

        if (viewFollowsScope)
        {
            SetAllVisible();
            viewFollowsScope = false;
        }
    }

    // Every scope change clears selection/highlights, which also ends any active grab.
    private void SetScope(string scopeId)
    {
        ClearSelection();

        if (CurrentScopeId == scopeId)
            return;

        CurrentScopeId = scopeId;
        EndMultiSelect();
        Debug.Log($"Interaction scope: {scopeId ?? "<model>"}");
        ScopeChanged?.Invoke();
    }

    // -------------------------
    // Individual Object Movement
    // -------------------------

    public void MoveObject(string id, Vector3 offset)
    {
        if (TryGetObject(id, out CADObject cadObject))
            cadObject.transform.localPosition += offset;
    }

    public void RotateObject(string id, Vector3 rotation)
    {
        if (TryGetObject(id, out CADObject cadObject))
        {
            cadObject.transform.Rotate(
                rotation,
                Space.Self
            );
        }
    }

    // World-space so input adapters need not know the CAD hierarchy.
    // Children follow; ResetObject still restores the original local pose.
    public void SetObjectWorldPose(string id, Vector3 position, Quaternion rotation)
    {
        if (TryGetObject(id, out CADObject cadObject))
            cadObject.transform.SetPositionAndRotation(position, rotation);
    }

    // Restores original parent (if detached) and original local pose.
    public void ResetObject(string id)
    {
        if (TryGetObject(id, out CADObject cadObject))
            RestoreOriginal(id, cadObject);
    }

    // Every CAD object back to its original parent and local pose.
    public void ResetAllObjects()
    {
        foreach (KeyValuePair<string, CADObject> entry in new List<KeyValuePair<string, CADObject>>(objects))
        {
            if (entry.Value != null)
                RestoreOriginal(entry.Key, entry.Value);
        }
    }

    // -------------------------
    // Detach / Reattach (Task 3 interaction state)
    // -------------------------

    /// <summary>
    /// Splits a part or subassembly from its assembly: reparents it (world pose kept) under the
    /// model-level parent of its top-level assembly, so moving the assembly no longer moves it.
    /// Its logical CAD parent, scope membership and descendants are unchanged.
    /// </summary>
    public bool Detach(string id)
    {
        if (!TryGetObject(id, out CADObject cadObject))
            return false;

        if (detachedIds.Contains(id))
        {
            Debug.LogWarning($"Cannot detach {id}: already detached.");
            return false;
        }

        if (GetParentId(id) == null)
        {
            Debug.LogWarning($"Cannot detach {id}: it has no CAD parent assembly.");
            return false;
        }

        // Top-level CAD ancestor via the logical hierarchy; its parent is the model level.
        string topId = id;
        for (string parent = GetParentId(topId); parent != null; parent = GetParentId(topId))
            topId = parent;

        Transform detachParent = TryGetLiveObject(topId, out CADObject top) ? top.transform.parent : null;
        cadObject.transform.SetParent(detachParent, worldPositionStays: true);
        detachedIds.Add(id);

        Debug.Log($"Detached {id} (logical parent {GetParentId(id)}).");
        return true;
    }

    /// <summary>
    /// Returns a detached object to its original parent AND original local pose (it snaps back
    /// into place). Same as ResetObject for a detached object.
    /// </summary>
    public bool Reattach(string id)
    {
        if (!TryGetObject(id, out CADObject cadObject))
            return false;

        if (!detachedIds.Contains(id))
        {
            Debug.LogWarning($"Cannot reattach {id}: it is not detached.");
            return false;
        }

        return RestoreOriginal(id, cadObject);
    }

    /// <summary>
    /// Restores an assembly and all its logical CAD descendants to their original parents and
    /// local poses, reattaching any detached ones. Parents are restored before children.
    /// </summary>
    public void ResetAssembly(string id)
    {
        if (!TryGetObject(id, out CADObject root))
            return;

        // Breadth-first over the logical hierarchy (not Transform.parent, which detach changes).
        var queue = new Queue<string>();
        queue.Enqueue(id);
        int restored = 0;

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (TryGetLiveObject(current, out CADObject cadObject) && RestoreOriginal(current, cadObject))
                restored++;

            foreach (KeyValuePair<string, string> entry in parentIds)
            {
                if (entry.Value == current)
                    queue.Enqueue(entry.Key);
            }
        }

        Debug.Log($"Reset assembly {id}: {restored} object(s) restored.");
    }

    // Original parent (if detached) + original local transform. False if the original parent
    // no longer exists (the object then stays detached rather than being lost).
    private bool RestoreOriginal(string id, CADObject cadObject)
    {
        if (detachedIds.Contains(id))
        {
            Transform originalParent = cadObject.OriginalParent;
            bool hadParent = originalParent != null;
            if (!hadParent && !IsSceneRootOriginal(cadObject))
            {
                Debug.LogWarning($"Cannot reattach {id}: its original parent no longer exists.");
                return false;
            }

            cadObject.transform.SetParent(originalParent, worldPositionStays: false);
            cadObject.transform.SetSiblingIndex(cadObject.OriginalSiblingIndex);
            detachedIds.Remove(id);
        }

        cadObject.ResetTransform();
        return true;
    }

    // A detached object whose original parent was the scene root (null) can still reattach.
    private static bool IsSceneRootOriginal(CADObject cadObject) =>
        ReferenceEquals(cadObject.OriginalParent, null);

    // -------------------------
    // Whole Model Movement
    // -------------------------

    public void MoveModel(Vector3 offset)
    {
        if (modelRoot != null)
            modelRoot.localPosition += offset;
    }

    public void RotateModel(Vector3 rotation)
    {
        if (modelRoot != null)
            modelRoot.Rotate(rotation, Space.Self);
    }

    public void ScaleModel(float scaleFactor)
    {
        if (modelRoot != null)
            modelRoot.localScale *= scaleFactor;
    }

    // Every CAD object's original local transform plus the model root's original pose.
    // Selection, scope and visibility are left as they are.
    public void ResetModel()
    {
        ResetAllObjects();
        ResetModelTransform();
    }

    public void ResetModelTransform()
    {
        if (modelRoot == null)
            return;

        modelRoot.localPosition = originalModelPosition;
        modelRoot.localRotation = originalModelRotation;
        modelRoot.localScale = originalModelScale;
    }
}

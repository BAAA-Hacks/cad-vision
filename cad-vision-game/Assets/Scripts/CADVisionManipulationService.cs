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

            for (Transform current = entry.Value.transform.parent;
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
        // ends any active grab.
        ClearSelection();

        foreach (string id in importedIds)
            objects.Remove(id);
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

    // -------------------------
    // Highlighting
    // -------------------------

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
            MaterialPropertyBlock block = new();
            renderer.GetPropertyBlock(block);

            if (highlighted)
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

    public void ResetObject(string id)
    {
        if (TryGetObject(id, out CADObject cadObject))
            cadObject.ResetTransform();
    }

    public void ResetAllObjects()
    {
        foreach (CADObject cadObject in objects.Values)
        {
            if (cadObject != null)
                cadObject.ResetTransform();
        }
    }

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

    public void ResetModelTransform()
    {
        if (modelRoot == null)
            return;

        modelRoot.localPosition = originalModelPosition;
        modelRoot.localRotation = originalModelRotation;
        modelRoot.localScale = originalModelScale;
    }
}

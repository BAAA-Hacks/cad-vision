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

    // Runtime adapters use the same accepted objects as desktop manipulation.
    internal IReadOnlyCollection<CADObject> RegisteredObjects => objects.Values;

    private Vector3 originalModelPosition;
    private Quaternion originalModelRotation;
    private Vector3 originalModelScale;

    private void Start()
    {
        RegisterObjects();

        if (modelRoot != null)
        {
            originalModelPosition = modelRoot.localPosition;
            originalModelRotation = modelRoot.localRotation;
            originalModelScale = modelRoot.localScale;
        }
        else
        {
            Debug.LogWarning("Manipulation Service has no model root.");
        }
    }

    // -------------------------
    // Object Registry
    // -------------------------

    private void RegisterObjects()
    {
        objects.Clear();

        CADObject[] cadObjects = FindObjectsByType<CADObject>(
            FindObjectsInactive.Include
        );

        foreach (CADObject cadObject in cadObjects)
        {
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

    private bool TryGetObject(string id, out CADObject cadObject)
    {
        if (objects.TryGetValue(id, out cadObject))
            return true;

        Debug.LogWarning($"CAD object not found: {id}");
        return false;
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
    public void SelectFromHit(string hitId)
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
        ClearHighlights();
    }

    public IEnumerable<CADObject> GetSelectedObjects()
    {
        foreach (string id in selectedIds)
        {
            if (objects.TryGetValue(id, out CADObject cadObject))
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
            if (objects.TryGetValue(id, out CADObject cadObject))
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
                // URP uses _BaseColor.
                // Built-In commonly uses _Color.
                if (renderer.sharedMaterial != null &&
                    renderer.sharedMaterial.HasProperty("_BaseColor"))
                {
                    block.SetColor("_BaseColor", highlightColor);
                }
                else
                {
                    block.SetColor("_Color", highlightColor);
                }
            }
            else
            {
                block.Clear();
            }

            renderer.SetPropertyBlock(block);
        }
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
            bool shouldBeVisible = false;

            foreach (string isolatedId in isolatedIds)
            {
                if (!objects.TryGetValue(
                    isolatedId,
                    out CADObject isolatedObject))
                {
                    continue;
                }

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
            cadObject.ResetTransform();
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

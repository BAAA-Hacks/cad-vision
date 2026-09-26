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

        Debug.Log($"Registered {objects.Count} CAD objects.");
    }

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
        foreach (CADObject cadObject in objects.Values)
        {
            cadObject.gameObject.SetActive(true);
        }
    }

    public void Isolate(IEnumerable<string> ids)
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

    public void Isolate(string id)
    {
        Isolate(new[] { id });
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

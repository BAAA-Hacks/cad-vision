using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>CADRuntimeBridge.PlaceInFront: a new model's visible geometry lands in front of the user.</summary>
public class CADModelPlacementTests
{
    private readonly List<Object> created = new();

    [TearDown]
    public void TearDown()
    {
        foreach (Object o in created)
        {
            if (o != null)
                Object.DestroyImmediate(o);
        }
        created.Clear();
    }

    [Test]
    public void VisibleGeometryIsCenteredInFrontOfTheHead()
    {
        // Geometry far from the root origin (CAD origins often are), user looking along +X.
        Transform root = Model(new Vector3(-7f, 3f, 12f), new Vector3(5f, 1f, -2f), new Vector3(0.4f, 0.2f, 0.6f));
        Transform head = Head(new Vector3(1f, 1.6f, -2f), Quaternion.Euler(20f, 90f, 0f));
        Quaternion rotation = root.rotation;
        Vector3 scale = root.localScale;

        Assert.That(CADRuntimeBridge.PlaceInFront(root, head, 0.8f, 0.2f), Is.True);

        Bounds bounds = VisibleBounds(root);
        Assert.That(bounds.center.z, Is.EqualTo(head.position.z).Within(1e-3f), "centered on the view line");
        Assert.That(bounds.center.y, Is.EqualTo(head.position.y - 0.2f).Within(1e-3f), "slightly below eye level");
        Assert.That(bounds.min.x - head.position.x, Is.EqualTo(0.8f).Within(1e-3f), "near side 0.8 m ahead (pitch ignored)");
        Assert.That(root.rotation, Is.EqualTo(rotation), "rotation untouched");
        Assert.That(root.localScale, Is.EqualTo(scale), "scale untouched");
    }

    [Test]
    public void ModelWithoutVisibleGeometryIsLeftAlone()
    {
        Transform root = Track(new GameObject("Empty Root")).transform;
        root.position = new Vector3(1f, 2f, 3f);
        Transform head = Head(Vector3.zero, Quaternion.identity);

        Assert.That(CADRuntimeBridge.PlaceInFront(root, head, 0.8f, 0.2f), Is.False);
        Assert.That(root.position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
    }

    [Test]
    public void LookingStraightDownStillPlacesItAhead()
    {
        Transform root = Model(Vector3.zero, Vector3.zero, Vector3.one * 0.2f);
        Transform head = Head(new Vector3(0f, 1.6f, 0f), Quaternion.Euler(90f, 0f, 0f));

        Assert.That(CADRuntimeBridge.PlaceInFront(root, head, 0.8f, 0.2f), Is.True);
        Bounds bounds = VisibleBounds(root);
        Assert.That(new Vector2(bounds.center.x, bounds.center.z).magnitude, Is.GreaterThan(0.8f), "in front, not underfoot");
        Assert.That(bounds.center.y, Is.EqualTo(1.4f).Within(1e-3f));
    }

    private Transform Model(Vector3 rootPosition, Vector3 partOffset, Vector3 partSize)
    {
        Transform root = Track(new GameObject("CADVisionModelRoot")).transform;
        root.position = rootPosition;
        GameObject part = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
        part.transform.SetParent(root, false);
        part.transform.localPosition = partOffset;
        part.transform.localScale = partSize;
        return root;
    }

    private Transform Head(Vector3 position, Quaternion rotation)
    {
        Transform head = Track(new GameObject("Head")).transform;
        head.SetPositionAndRotation(position, rotation);
        return head;
    }

    private static Bounds VisibleBounds(Transform root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers)
            bounds.Encapsulate(renderer.bounds);
        return bounds;
    }

    private T Track<T>(T obj) where T : Object
    {
        created.Add(obj);
        return obj;
    }
}

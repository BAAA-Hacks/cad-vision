using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Detached objects are their own interaction unit for hit resolution (click, drag start,
/// context menu), while the logical CAD hierarchy still drives Reset Assembly / Reattach.
/// Model: Root(scaled) / A / { P1, S / { S1, S2 } } registered like a Task 2 import.
/// </summary>
public class CADDetachInteractionTests
{
    private readonly List<Object> created = new();
    private CADVisionManipulationService svc;
    private Transform root;
    private Dictionary<string, Transform> t;

    [SetUp]
    public void SetUp()
    {
        var host = Track(new GameObject("ManipulationManager"));
        svc = host.AddComponent<CADVisionManipulationService>();

        root = Track(new GameObject("CADVisionModelRoot")).transform;
        root.localScale = Vector3.one * 10f;
        root.position = new Vector3(4f, -2f, 7f); // Origin far from geometry, like SolidWorks.

        t = new Dictionary<string, Transform>();
        t["A"] = Node("A", root, new Vector3(-0.3f, 0.35f, -0.5f), false);
        t["P1"] = Node("P1", t["A"], new Vector3(0.02f, 0f, 0f), true);
        t["S"] = Node("S", t["A"], new Vector3(-0.02f, 0.01f, 0f), false);
        t["S1"] = Node("S1", t["S"], new Vector3(0f, 0.02f, 0f), true);
        t["S2"] = Node("S2", t["S"], new Vector3(0.01f, -0.01f, 0.005f), true);

        svc.ReplaceImportedModel(root, t.ToDictionary(kv => kv.Key, kv => kv.Value.gameObject));
    }

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
    public void DetachedLeafClickedAtModelScopeSelectsItself()
    {
        Assert.That(svc.Detach("P1"), Is.True);
        Assert.That(svc.CurrentScopeId, Is.Null);

        svc.SelectFromHit("P1", t["P1"].position);

        Assert.That(SelectedIds(), Is.EqualTo(new[] { "P1" }));
        Assert.That(svc.ResolveSelectable("P1"), Is.EqualTo("P1"), "context-menu target is the detached part");
    }

    [Test]
    public void DetachedLeafDraggedAtModelScopeMovesItselfNotTheAssembly()
    {
        svc.Detach("P1");
        Vector3 assemblyBefore = t["A"].position;
        Vector3 partBefore = t["P1"].position;

        // Same path as CADPointerInteraction.BeginManipulation: select at the press point, grab the selection.
        svc.SelectFromHit("P1", t["P1"].position);
        var session = new CADGrabSession();
        session.Begin(svc, svc.GetSelectedObjects().Single(), new Pose(Vector3.zero, Quaternion.identity));
        session.Update(new Pose(new Vector3(0.1f, 0.05f, 0f), Quaternion.identity));
        session.End();

        Assert.That(Vector3.Distance(t["P1"].position, partBefore), Is.GreaterThan(1e-3f), "detached part moved");
        Assert.That(Vector3.Distance(t["A"].position, assemblyBefore), Is.LessThan(1e-6f), "original assembly did not move");
    }

    [Test]
    public void DetachedSubassemblyClickedAtModelScopeSelectsItself()
    {
        svc.Detach("S");

        // Hitting one of its parts resolves to the detached subassembly, not to A.
        svc.SelectFromHit("S1", t["S1"].position);

        Assert.That(SelectedIds(), Is.EqualTo(new[] { "S" }));
    }

    [Test]
    public void NonDetachedChildAtModelScopeStillResolvesToItsAssembly()
    {
        svc.Detach("S");

        svc.SelectFromHit("P1", t["P1"].position);

        Assert.That(SelectedIds(), Is.EqualTo(new[] { "A" }));
    }

    [Test]
    public void DetachedObjectResetAssemblyStillTargetsOriginalLogicalAssembly()
    {
        Vector3 localBefore = t["P1"].localPosition;
        svc.Detach("P1");
        svc.SelectFromHit("P1", t["P1"].position);
        svc.SetObjectWorldPose("P1", t["P1"].position + Vector3.one * 0.2f, t["P1"].rotation);

        Assert.That(svc.GetLogicalParentId("P1"), Is.EqualTo("A"));
        Assert.That(MenuResetAssemblyTarget("P1"), Is.EqualTo("A"), "menu's Reset Assembly acts on A");

        svc.ResetAssembly("A");

        Assert.That(svc.IsDetached("P1"), Is.False);
        Assert.That(t["P1"].parent, Is.SameAs(t["A"]));
        Assert.That(Vector3.Distance(t["P1"].localPosition, localBefore), Is.LessThan(1e-6f));
    }

    [Test]
    public void ReattachRestoresNormalScopeResolution()
    {
        svc.Detach("P1");
        svc.SelectFromHit("P1", t["P1"].position);
        Assert.That(SelectedIds(), Is.EqualTo(new[] { "P1" }));

        Assert.That(svc.Reattach("P1"), Is.True);
        svc.SelectFromHit("P1", t["P1"].position);

        Assert.That(SelectedIds(), Is.EqualTo(new[] { "A" }));
    }

    [Test]
    public void InsideOriginalAssemblyScopeDetachedPartIsStillSelectable()
    {
        svc.Detach("P1");
        svc.EnterScope("A");

        svc.SelectFromHit("P1", t["P1"].position);

        Assert.That(SelectedIds(), Is.EqualTo(new[] { "P1" }));
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"));
    }

    // ---- helpers ----

    private string[] SelectedIds() => svc.GetSelectedObjects().Select(o => o.id).ToArray();

    // CADContextMenu's own rule (target if assembly, else its logical parent), read via reflection
    // so the test tracks the menu's real implementation.
    private string MenuResetAssemblyTarget(string id)
    {
        var menu = svc.gameObject.AddComponent<CADContextMenu>();
        typeof(CADContextMenu).GetField("manipulationService", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(menu, svc);
        typeof(CADContextMenu).GetField("targetId", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(menu, id);
        return (string)typeof(CADContextMenu).GetMethod("ResetAssemblyTarget", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(menu, null);
    }

    private Transform Node(string name, Transform parent, Vector3 local, bool mesh)
    {
        GameObject go = mesh ? GameObject.CreatePrimitive(PrimitiveType.Cube) : new GameObject();
        Track(go);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        go.transform.localRotation = Quaternion.Euler(local * 30f);
        if (mesh)
            go.transform.localScale = Vector3.one * 0.01f;
        return go.transform;
    }

    private T Track<T>(T obj) where T : Object
    {
        created.Add(obj);
        return obj;
    }
}

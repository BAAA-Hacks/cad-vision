using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// CADMainMenuInput logic with the hardware read replaced (ButtonProvider /
/// ControllerInUseProvider). These cannot prove the physical Quest button mapping.
/// </summary>
public class CADMainMenuInputTests
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

    private readonly List<Object> created = new();
    private CADMainMenu menu;
    private CADMainMenuInput input;
    private bool held;
    private bool controllerInUse;

    [SetUp]
    public void SetUp()
    {
        var host = new GameObject("ManipulationManager");
        created.Add(host);
        host.AddComponent<CADVisionManipulationService>();
        host.AddComponent<CADUISettings>();
        menu = host.AddComponent<CADMainMenu>();
        input = host.AddComponent<CADMainMenuInput>();
        Call(menu, "Awake");
        Call(input, "Awake");

        held = false;
        controllerInUse = true;
        input.ButtonProvider = () => held;
        input.ControllerInUseProvider = () => controllerInUse;
    }

    [TearDown]
    public void TearDown()
    {
        if (menu != null && Get(menu, "panelRoot") is GameObject panel)
            Object.DestroyImmediate(panel);
        foreach (EventSystem es in Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include))
        {
            if (es.name == "CAD Vision EventSystem")
                Object.DestroyImmediate(es.gameObject);
        }
        foreach (Object o in created)
        {
            if (o != null)
                Object.DestroyImmediate(o);
        }
        created.Clear();
    }

    // 1, 2, 3
    [Test]
    public void PressTogglesHiddenToVisibleAndBack()
    {
        Assert.That(menu.IsOpen, Is.False);

        Press();
        Assert.That(menu.IsOpen, Is.True, "1, 2: a press calls ToggleMainMenu (hidden → visible)");

        Press();
        Assert.That(menu.IsOpen, Is.False, "3: second press, visible → hidden");
    }

    // 4
    [Test]
    public void HoldingTogglesOnlyOnce()
    {
        held = true;
        for (int i = 0; i < 30; i++)
            Frame();
        Assert.That(menu.IsOpen, Is.True, "one toggle for the whole hold");

        held = false;
        Frame();
        Assert.That(menu.IsOpen, Is.True, "release does not toggle");
    }

    // 5
    [Test]
    public void NothingElseToggles()
    {
        for (int i = 0; i < 10; i++)
            Frame(); // Button never pressed (other controller inputs are not read at all).
        Assert.That(menu.IsOpen, Is.False);

        controllerInUse = false; // The same menu input coming from the hand (gesture / pinch).
        Press();
        Assert.That(menu.IsOpen, Is.False, "hand menu input ignored while leftHandMenuGesture is off");
    }

    [Test]
    public void HandGestureOptionAllowsHandPresses()
    {
        typeof(CADMainMenuInput).GetField("leftHandMenuGesture", Any).SetValue(input, true);
        controllerInUse = false;
        Press();
        Assert.That(menu.IsOpen, Is.True);
    }

    [Test]
    public void ClosesTheSameInstanceOpenedElsewhere()
    {
        menu.ShowMainMenu(); // e.g. from the context menu's "Main Menu" entry.
        GameObject panel = menu.PanelTransform.gameObject;
        Press();
        Assert.That(menu.IsOpen, Is.False);
        Press();
        Assert.That(menu.PanelTransform.gameObject, Is.SameAs(panel), "same menu instance");
    }

    private void Press()
    {
        held = true;
        Frame();
        held = false;
        Frame();
    }

    private void Frame() => Call(input, "Update");

    private static void Call(object o, string name) => o.GetType().GetMethod(name, Any).Invoke(o, null);

    private static object Get(object o, string name) => o.GetType().GetField(name, Any).GetValue(o);
}

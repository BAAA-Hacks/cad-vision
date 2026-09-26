using UnityEngine;

public enum CADPointerSourceKind { Controller, Hand, Other }

public enum CADPointerHandedness { Left, Right }

/// <summary>
/// One semantic pointer for CADPointerInteraction: a pose, a hovered target and a single
/// "select" signal (controller trigger, hand pinch, mouse button...). The interaction logic
/// never sees which physical input produced it.
/// </summary>
public interface ICADPointerSource
{
    /// <summary>Stable identity for logs and arbitration, e.g. "Right Hand".</summary>
    string SourceId { get; }
    CADPointerSourceKind Kind { get; }
    CADPointerHandedness Handedness { get; }

    /// <summary>
    /// Tracked and usable this frame. When it turns false mid-press the press is cancelled
    /// (never treated as a release, so tracking loss can't click).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Ray origin and orientation (forward = pointing direction).</summary>
    Pose Pose { get; }

    /// <summary>The select signal is held.</summary>
    bool IsSelecting { get; }

    /// <summary>
    /// What the pointer is over: CAD (with the owning CAD ID), UI, or nothing, and the ray
    /// hit point when there is one.
    /// </summary>
    CADPointerTargetKind Classify(out string cadId, out Vector3? hitPoint);
}

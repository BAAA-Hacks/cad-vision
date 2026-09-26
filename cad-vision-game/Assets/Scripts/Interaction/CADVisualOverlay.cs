using UnityEngine;

/// <summary>
/// Marks a renderer as a visual overlay (selection outline shell, future edge lines), not CAD
/// geometry. Highlighting, bounds and grab-point calculations skip overlays.
/// </summary>
[DisallowMultipleComponent]
public sealed class CADVisualOverlay : MonoBehaviour { }

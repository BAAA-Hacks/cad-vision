using UnityEngine;

/// <summary>
/// Marks a pointer target as interactive UI. Optional: any non-CAD interactable is already
/// treated as UI, but future menus should carry this so intent is explicit.
/// </summary>
[DisallowMultipleComponent]
public sealed class CADUIPointerTarget : MonoBehaviour { }

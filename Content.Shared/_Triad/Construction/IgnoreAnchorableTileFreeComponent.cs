using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Construction;

/// <summary>
///     Anchorable entities will not check entities with this component, allowing you to ignore bounding boxes.
///     Useful for entities that only take up a half-tile, such as directional windows.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class IgnoreAnchorableTileFreeComponent : Component;

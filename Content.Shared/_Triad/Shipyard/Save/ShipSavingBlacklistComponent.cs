using Robust.Shared.GameStates;
namespace Content.Shared._Triad.Shipyard.Save;

/// <summary>
/// Entities with this component will be unable to save or load ships from the shipyard console.
/// You can also add this to a grid to prevent it from being saved.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ShipSavingBlacklistComponent : Component;

namespace Content.Shared._Triad.Shipyard.Save;

/// <summary>
/// Entities with this component will be unable to save or load ships from the shipyard console.
/// </summary>
[RegisterComponent]
public sealed partial class ShipSavingBlacklistComponent : Component;

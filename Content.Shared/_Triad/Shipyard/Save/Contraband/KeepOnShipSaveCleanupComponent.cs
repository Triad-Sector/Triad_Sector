namespace Content.Shared._Triad.Shipyard.Save.Contraband;

/// <summary>
/// Entities with this component will stay during drydock ship cleanup operations, even if they are unanchored or outside containers
/// </summary>
[RegisterComponent]
public sealed partial class KeepOnShipSaveCleanupComponent : Component;

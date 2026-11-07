namespace Content.Shared._Crescent.ShipShields;

/// <summary>
/// The shield entity's back-references to the grid it covers and the emitter that raised it.
/// Runtime linkage only, like the entity itself, which is never saved.
/// </summary>
[RegisterComponent, UnsavedComponent] // Triad: UnsavedComponent, see ShipShieldedComponent.
public sealed partial class ShipShieldComponent : Component
{
    public EntityUid? Source;
    public EntityUid Shielded;
}

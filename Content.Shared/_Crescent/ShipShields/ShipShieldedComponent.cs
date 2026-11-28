namespace Content.Shared._Crescent.ShipShields;

/// <summary>
/// Marks a grid as currently shielded and points at its shield. Runtime linkage only: the
/// emitter rebuilds it whenever it raises a shield, so it must never ride a saved document.
/// </summary>
[RegisterComponent, UnsavedComponent] // Triad: UnsavedComponent. A saved marker reloads with an invalid shield and stops the emitter ever raising another.
public sealed partial class ShipShieldedComponent : Component
{
    public EntityUid Shield;
    public EntityUid? Source;
}

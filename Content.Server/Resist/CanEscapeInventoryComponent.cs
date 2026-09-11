using Content.Shared.DoAfter;

namespace Content.Server.Resist;

[RegisterComponent]
public sealed partial class CanEscapeInventoryComponent : Component
{
    /// <summary>
    /// Base doafter length for uncontested breakouts.
    /// </summary>
    [DataField("baseResistTime")]
    public float BaseResistTime = 5f;

    public bool IsEscaping => DoAfter != null;

    // Triad: DoAfterId has no type serializer; persisting it breaks ship-grid saves. Runtime-only state.
    /*
    [DataField("doAfter")]
    */
    [ViewVariables]
    public DoAfterId? DoAfter;
    // End Triad

    // Frontier
    [DataField]
    public EntityUid? EscapeCancelAction;
}

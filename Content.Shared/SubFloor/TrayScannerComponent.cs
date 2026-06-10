using Content.Shared.Whitelist;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.SubFloor;

[RegisterComponent, NetworkedComponent]
public sealed partial class TrayScannerComponent : Component
{
    /// <summary>
    ///     Whether the scanner is currently on.
    /// </summary>
    [DataField]
    public bool Enabled;

    // Triad: when false the scanner is always-on and ignores the activate key, so a tool that embeds it (the RPD)
    // keeps activate bound to its own UI instead of toggling the scan. The stock t-ray leaves this true.
    [DataField]
    public bool CanToggle = true;

    /// <summary>
    ///     Radius in which the scanner will reveal entities. Centered on the <see cref="LastLocation"/>.
    /// </summary>
    [DataField]
    public float Range = 4f;

    // Triad: optional reveal filter. When set, the scanner only reveals subfloor entities the whitelist admits (the
    // RPD uses this to surface atmos pipe infrastructure but not power cables). Null = reveal everything, so the
    // stock t-ray is unchanged. Static DataField read client-side from the prototype; not part of the networked state.
    [DataField]
    public EntityWhitelist? RevealWhitelist;
    // End Triad
}

[Serializable, NetSerializable]
public sealed class TrayScannerState : ComponentState
{
    public bool Enabled;
    public float Range;

    public TrayScannerState(bool enabled, float range)
    {
        Enabled = enabled;
        Range = range;
    }
}

using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Atmos.Components;

/// <summary>
/// Marker for gas vessels that have fire suppression.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SafeGasCanComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Enabled = true;

    [DataField, AutoNetworkedField]
    public LocId EnabledLabel = "gas-vessel-suppression-examine-enabled";

    [DataField, AutoNetworkedField]
    public LocId DisabledLabel = "gas-vessel-suppression-examine-disabled";

    [DataField, AutoNetworkedField]
    public int ExaminePriority = 0;
}

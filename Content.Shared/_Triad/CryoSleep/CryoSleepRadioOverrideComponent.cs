using Content.Shared.Radio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.CryoSleep;

/// <summary>
/// Overrides the radio channel sent by cryosleep pods whenever this mob cryos.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CryoSleepRadioOverrideComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public List<ProtoId<RadioChannelPrototype>> Overrides;
}

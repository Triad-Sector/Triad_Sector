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
    // Triad: initialized so the generated client-side HandleState (which Clears in place
    // as of engine v283) doesn't NRE on runtime-added instances that never saw YAML.
    [DataField(required: true), AutoNetworkedField]
    public List<ProtoId<RadioChannelPrototype>> Overrides = new();
}

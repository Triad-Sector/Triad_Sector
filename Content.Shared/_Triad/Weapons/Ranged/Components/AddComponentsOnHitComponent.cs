using Content.Shared.Whitelist;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class AddComponentsOnHitComponent : Component
{
    [DataField]
    public ComponentRegistry AddComponents = new();

    [DataField, AutoNetworkedField]
    public bool OverrideExisting = false;

    [DataField, AutoNetworkedField]
    public EntityWhitelist? Whitelist = null;

    [DataField, AutoNetworkedField]
    public EntityWhitelist? Blacklist = null;
}

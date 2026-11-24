using Content.Shared.Whitelist;
using Content.Shared.Damage;
using Robust.Shared.GameStates;
using Robust.Shared.Audio;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class AddPlasmaBurningOnHitComponent : Component
{
    [DataField, AutoNetworkedField]
    public DamageSpecifier Damage = new();

    [DataField, AutoNetworkedField]
    public float ArmorPenetration = 0;

    [DataField, AutoNetworkedField]
    public TimeSpan Duration = TimeSpan.FromSeconds(5);

    [DataField, AutoNetworkedField]
    public Color Color;

    [DataField, AutoNetworkedField]
    public int AddStacks = 1;

    [DataField, AutoNetworkedField]
    public EntityWhitelist? Whitelist = null;

    [DataField, AutoNetworkedField]
    public EntityWhitelist? Blacklist = null;

    [DataField, AutoNetworkedField]
    public LocId SelfPopup = "plasma-burn-target-self";
}

using Robust.Shared.Audio;
using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// <summary>
/// Changes the gunshot noise when it is toggled via <see cref="ItemToggleSystem"/>.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class GunChangeShotSoundOnToggledComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public SoundSpecifier? Sound;
}

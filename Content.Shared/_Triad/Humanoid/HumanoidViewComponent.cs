using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Humanoid;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HumanoidViewComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntityUid? ViewEntity;
}

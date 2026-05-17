using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Eye.Blinding.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SLNightVisionComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Active = true;

    [DataField]
    public EntProtoId EffectPrototype = "EffectSLNightVision";

    public bool Clothes;
}

[RegisterComponent]
public sealed partial class ClothesSLNightVisionComponent : Component
{ }
using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Audio;

namespace Content.Shared._NF.M_Emp;

public abstract partial class SharedM_EmpGeneratorComponent : Component
{
    /// <summary>
    /// The machine part that affects the attaching and cooldown times
    /// </summary>
    [DataField("machinePartDelay"), ViewVariables(VVAccess.ReadWrite)]
    public ProtoId<MachinePartPrototype> MachinePartDelay = "Capacitor";

    /// <summary>
    /// A multiplier applied to the attaching and cooldown times for each level of <see cref="MachinePartDelay"/>
    /// </summary>
    [DataField("partRatingDelay"), ViewVariables(VVAccess.ReadWrite)]
    public float PartRatingDelay = 0.75f;

    [DataField("activatedSound")]
    public SoundSpecifier ActivatedSound =
        new SoundPathSpecifier("/Audio/Effects/countdown.ogg");
}

[Serializable, NetSerializable]
public enum M_EmpGeneratorVisuals : byte
{
    ChargeState,
    Ready,
    ReadyBlinking,
    Unready,
    UnreadyBlinking
}

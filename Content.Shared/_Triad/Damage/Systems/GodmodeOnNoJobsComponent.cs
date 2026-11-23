using Content.Shared.Roles;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Damage;

[RegisterComponent, NetworkedComponent, Access(typeof(GodmodeOnNoJobsSystem))]
public sealed partial class GodmodeOnNoJobsComponent : Component
{
    [DataField(required: true)]
    public ProtoId<GodmodeOnNoJobsListPrototype> JobListProto;
}

[Prototype]
public sealed partial class GodmodeOnNoJobsListPrototype : IPrototype
{
    /// <summary>
    /// Prototype ID of the species.
    /// </summary>
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public Dictionary<ProtoId<JobPrototype>, int?> JobList = new();
}

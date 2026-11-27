using Content.Shared.Roles;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Damage;

[RegisterComponent, NetworkedComponent]
public sealed partial class GodmodeOnJobsComponent : Component
{
    [DataField]
    public ProtoId<GodmodeOnJobsListPrototype> JobListProto;
}

[Prototype]
public sealed partial class GodmodeOnJobsListPrototype : IPrototype
{
    /// <summary>
    /// Prototype ID of the species.
    /// </summary>
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public Dictionary<ProtoId<JobPrototype>, int?> JobList = new();

    [DataField]
    public GodmodeOnJobsMode Mode = GodmodeOnJobsMode.All;
}

public enum GodmodeOnJobsMode : byte
{
    /// <summary>
    /// This mode requires ALL jobs in the list to have the correct player amount.
    /// </summary>
    All = 0,

    /// <summary>
    /// This mode only requires one job in the list to have the correct player amount.
    /// </summary>
    Any = 1,

    /// <summary>
    /// This mode only requires none of the jobs in the list to have players above or equal to the given amount in order to be given god mode.
    /// </summary>
    None = 2
}

using Content.Shared.EntityEffects;
using Content.Shared.Polymorph;
using Robust.Shared.Prototypes;

namespace Content.Server.EntityEffects.Effects;

public sealed partial class RevertPolymorph : EventEntityEffect<RevertPolymorph>
{
    /// <summary>
    ///     What polymorph prototype is used on effect
    /// </summary>
    [DataField("prototype")]
    public ProtoId<PolymorphPrototype> PolymorphPrototype { get; set; }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
        => Loc.GetString("reagent-effect-guidebook-revert-polymorph",
            ("chance", Probability), ("entityname",
                prototype.Index<EntityPrototype>(prototype.Index(PolymorphPrototype).Configuration.Entity).Name));
}

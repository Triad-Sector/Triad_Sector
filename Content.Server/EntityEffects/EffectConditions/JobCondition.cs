using System.Linq;
using Content.Shared.EntityEffects;
using Content.Shared.Localizations;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Prototypes;

namespace Content.Server.EntityEffects.EffectConditions;

public sealed partial class JobCondition : EntityEffectCondition
{
    [DataField(required: true)] public List<ProtoId<JobPrototype>> Job;

    // Resolved lazily, not in a field initializer: SerializationManager.Initialize instantiates
    // data definitions from a Parallel.ForEach, and IoC has no context on those worker threads.
    private ISawmill? _sawmill;
    private ISawmill Sawmill => _sawmill ??= IoCManager.Resolve<ILogManager>().RootSawmill;

    public override bool Condition(EntityEffectBaseArgs args)
    {
        args.EntityManager.TryGetComponent<MindContainerComponent>(args.TargetEntity, out var mindContainer);

        if ( mindContainer is null
             || !args.EntityManager.TryGetComponent<MindComponent>(mindContainer.Mind, out var mind))
            return false;

        foreach (var roleId in mind.MindRoles)
        {
            if(!args.EntityManager.HasComponent<JobRoleComponent>(roleId))
                continue;

            if (!args.EntityManager.TryGetComponent<MindRoleComponent>(roleId, out var mindRole))
            {
                Sawmill.Error($"Encountered job mind role entity {roleId} without a {nameof(MindRoleComponent)}");
                continue;
            }

            if (mindRole.JobPrototype == null)
            {
                Sawmill.Error($"Encountered job mind role entity {roleId} without a {nameof(JobPrototype)}");
                continue;
            }

            if (Job.Contains(mindRole.JobPrototype.Value))
                return true;
        }

        return false;
    }

    public override string GuidebookExplanation(IPrototypeManager prototype)
    {
        var localizedNames = Job.Select(jobId => prototype.Index(jobId).LocalizedName).ToList();
        return Loc.GetString("reagent-effect-condition-guidebook-job-condition", ("job", ContentLocalizationManager.FormatListToOr(localizedNames)));
    }
}

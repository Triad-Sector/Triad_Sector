// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: MPL-2.0

using System.Linq;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Artifact.Prototypes;

namespace Content.Shared.Xenoarchaeology.Artifact;

/// <summary>
/// Triad: the artifact economy. Everything that turns "how hard was this to solve" and "how much of
/// it did you solve" into research points and credits lives here, so balance work is a number edit
/// on one panel rather than a formula hunt across the upstream files.
///
/// Per node: trigger difficulty (three authored axes on the trigger prototype) compounds with effect
/// danger (authored on the effect prototype) into a scale on the node's research value.
/// Per artifact: a completion multiplier ramps gently while solving and snaps hard on the final
/// node, and applies to credits and research points alike.
///
/// Seeds below are analytical. WS8 of the rework plan fits them against sampled generations so an
/// easy full solve lands near 150k credits and a nasty one near 500k.
/// </summary>
public abstract partial class SharedXenoArtifactSystem
{
    #region Trigger difficulty

    /// <summary>
    /// Blend weights for the three trigger axes. They sum to 1 so the blend stays on the 1..5 scale.
    /// Schedulability is weighted highest because it is the axis that actually gates deep chains:
    /// a sustained state is free once staged, an instantaneous act costs window seconds, a long act
    /// eats the window.
    /// </summary>
    public const float TriggerSourcingWeight = 0.25f;
    public const float TriggerEffortWeight = 0.35f;
    public const float TriggerScheduleWeight = 0.40f;

    /// <summary>
    /// Collapses a trigger's three authored axes into one 1..5 difficulty.
    /// </summary>
    public static float GetTriggerDifficulty(XenoArchTriggerPrototype trigger)
    {
        return TriggerSourcingWeight * trigger.Sourcing
               + TriggerEffortWeight * trigger.Effort
               + TriggerScheduleWeight * trigger.Schedulability;
    }

    #endregion

    #region Node difficulty

    /// <summary>
    /// Weight on the trigger-times-danger interference term. Effects you already unlocked fire
    /// while you chain the next trigger, so danger is an active impediment to solving, not a risk
    /// you accept afterwards. The term normalises to [0.04, 1.0] and adds almost nothing when
    /// either side is trivial.
    /// </summary>
    public const float NodeInterferenceWeight = 1.0f;

    /// <summary>
    /// Additive floor on the difficulty scale. Directly caps the achievable spread between an
    /// all-easy and an all-hard artifact: the ratio tends to 6x as this approaches 0 and falls to
    /// 2x when it equals <see cref="NodeDifficultyWeight"/>. Seeded at 0 so the spread is wide open
    /// for WS8 to narrow rather than the other way round.
    /// </summary>
    public const float NodeDifficultyFloor = 0.0f;

    /// <summary>
    /// Multiplier on compounded node difficulty. With the floor at 0 this is the whole scale.
    /// </summary>
    public const float NodeDifficultyWeight = 0.35f;

    /// <summary>
    /// Exponent on compounded node difficulty before the weight. Above 1 it widens the gap between an
    /// all-easy and an all-hard artifact, which the flat blend left narrower than the 150k-to-500k target
    /// (sampled 2.5x between p10 and p90 at exponent 1).
    /// </summary>
    public const float NodeDifficultyExponent = 1.3f;

    /// <summary>
    /// Compounds trigger difficulty and effect danger into one number, roughly [1.04, 6.0].
    /// The linear mean is the base so a single tier-1 axis cannot collapse the node to nothing;
    /// the product term pays for the interference between them.
    /// </summary>
    public static float GetNodeDifficulty(XenoArtifactNodeComponent node)
    {
        var t = node.TriggerDifficulty;
        var d = node.Danger;
        return 0.5f * (t + d) + NodeInterferenceWeight * (t * d / 25f);
    }

    /// <summary>
    /// The scale applied to a node's research value for how hard it was to solve.
    /// </summary>
    public static float GetNodeDifficultyScale(XenoArtifactNodeComponent node)
    {
        return NodeDifficultyFloor + NodeDifficultyWeight * MathF.Pow(GetNodeDifficulty(node), NodeDifficultyExponent);
    }

    #endregion

    #region Durability floor

    /// <summary>
    /// Floor on the active-node durability multiplier. Upstream's curve is 1 - (dur/max)^2, which is
    /// exactly 0 for a freshly solved terminal node, so a full-solve bonus multiplied an empty last
    /// layer at the point it should peak.
    /// </summary>
    public const float MinActiveDurabilityMultiplier = 0.25f;

    #endregion

    #region Completion multiplier

    /// <summary>
    /// Payout multiplier on a full solve. The payday, not a rounding bonus.
    /// </summary>
    public const float FullSolveMultiplier = 5.0f;

    /// <summary>
    /// Exponent on the partial-progress ramp. 3 keeps partial progress cheap and back-loads the
    /// reward: f=0.5 gives 1.13x, f=0.9 gives 1.73x, f->1 gives 2.0x, then the final node snaps to
    /// <see cref="FullSolveMultiplier"/>. That last node is worth about 2.5x everything before it.
    /// </summary>
    public const float CompletionCurvePower = 3.0f;

    /// <summary>
    /// Whole-artifact payout multiplier from how much of the graph is unlocked. Shared between the
    /// price handler and the analyzer extract path so credits and research points cannot drift apart.
    /// </summary>
    public float GetCompletionMultiplier(Entity<XenoArtifactComponent> ent)
    {
        var all = GetAllNodes(ent).ToList();
        if (all.Count == 0)
            return 1f;

        var unlocked = all.Count(n => !n.Comp.Locked);
        if (unlocked >= all.Count)
            return FullSolveMultiplier;

        var f = (float)unlocked / all.Count;
        return 1f + MathF.Pow(f, CompletionCurvePower);
    }

    /// <summary>
    /// Everything that scales the whole artifact's payout in one place: completion and form.
    /// </summary>
    public float GetArtifactPayoutMultiplier(Entity<XenoArtifactComponent> ent)
    {
        return GetCompletionMultiplier(ent) * ent.Comp.FormValueMultiplier;
    }

    #endregion
}

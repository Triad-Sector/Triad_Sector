// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: MPL-2.0

using System.Linq;
using Content.Shared.EntityTable.EntitySelectors;
using Content.Shared.Random.Helpers;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Artifact.Prototypes;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Xenoarchaeology.Artifact;

/// <summary>
/// Triad: the artifact economy. Everything that turns "how hard was this to solve" and "how much of
/// it did you solve" into research points and credits lives here, so balance work is a number edit
/// on one panel rather than a formula hunt across the upstream files.
///
/// Per node: trigger difficulty (three authored axes on the trigger prototype) compounds with effect
/// danger (authored on the effect prototype) into a scale on the node's research value.
/// Per artifact: a severity profile (shape and cap) decides how danger climbs with depth, and a
/// completion multiplier compounds exponentially with every unlock toward the full-solve payout,
/// applying to credits and research points alike.
///
/// Seeds below are analytical. WS8 of the rework plan fits them against sampled generations so an
/// easy full solve lands near 150k credits and a nasty one near 500k.
/// </summary>
public abstract partial class SharedXenoArtifactSystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    #region Grid locality

    /// <summary>
    /// Triad: artifact effects are grid-local. An artifact belongs to the ship carrying it, so every
    /// area effect filters its candidates through this before touching them. Docking two hulls no
    /// longer lets a node reach across the airlock, and nothing an artifact does can land on another
    /// crew's ship.
    ///
    /// An artifact with no grid under it (EVA, drifting in a debris field) is local to nothing, so it
    /// reaches only other gridless entities on the same map rather than reaching into every hull
    /// within range.
    /// </summary>
    /// <param name="artifact">The artifact the effect is firing from. The node entity works too: it
    /// lives in the artifact's container and inherits its grid.</param>
    /// <param name="target">Candidate the effect wants to touch.</param>
    public bool IsGridLocal(EntityUid artifact, EntityUid target)
    {
        return IsGridLocal(Transform(artifact), target);
    }

    /// <inheritdoc cref="IsGridLocal(EntityUid,EntityUid)"/>
    /// <remarks>
    /// Overload for effect loops, which resolve the artifact's transform once and then test many
    /// candidates against it.
    /// </remarks>
    public bool IsGridLocal(TransformComponent artifactXform, EntityUid target)
    {
        var targetXform = Transform(target);
        return artifactXform.MapUid == targetXform.MapUid
               && artifactXform.GridUid == targetXform.GridUid;
    }

    /// <summary>
    /// Triad: as <see cref="IsGridLocal(EntityUid,EntityUid)"/>, but for the effects that land on a
    /// clicked location rather than on an entity. Stops an artifact held on a docked shuttle from
    /// dropping gas, foam or an EMP onto the grid next door.
    /// </summary>
    public bool IsGridLocal(EntityUid artifact, EntityCoordinates coordinates)
    {
        var artifactXform = Transform(artifact);
        return artifactXform.MapUid == _transform.GetMap(coordinates)
               && artifactXform.GridUid == _transform.GetGrid(coordinates);
    }

    #endregion

    #region Severity profile

    /// <summary>
    /// Steepness of the log shape. Higher front-loads the climb harder: most of the danger arrives
    /// in the first third of the graph, then a long plateau near the cap.
    /// </summary>
    public const float SeverityLogSteepness = 8f;

    /// <summary>
    /// Steepness of the exp shape. Higher back-loads the climb harder: safe for most of the graph,
    /// then a cliff on the last layers.
    /// </summary>
    public const float SeverityExpSteepness = 3f;

    /// <summary>
    /// Width of the Gaussian that reweights a table entry by how far its rating sits from the
    /// depth target. At 0.6 a target of 3 still reaches 2 and 4 at about 6% of their author weight
    /// and 1 and 5 at essentially nothing, so the curve reads in play without being a hard tier.
    /// </summary>
    public const float SeverityKernelSigma = 0.6f;

    /// <summary>
    /// Entries the kernel pushes below this fraction of their author weight are dropped from the
    /// roll entirely. If that empties the roll, the pick falls back to the author weights alone.
    /// </summary>
    public const float SeverityKernelFloor = 0.01f;

    /// <summary>
    /// Curve from normalised depth to normalised danger, both in [0, 1]. Every shape starts at 0
    /// and ends at 1; the shape decides where in between the climb happens.
    /// </summary>
    public static float GetSeverityCurve(XenoArtifactSeverityShape shape, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return shape switch
        {
            XenoArtifactSeverityShape.Log => MathF.Log(1f + SeverityLogSteepness * t) / MathF.Log(1f + SeverityLogSteepness),
            XenoArtifactSeverityShape.Exp => (MathF.Exp(SeverityExpSteepness * t) - 1f) / (MathF.Exp(SeverityExpSteepness) - 1f),
            _ => t,
        };
    }

    /// <summary>
    /// The danger a node at normalised depth <paramref name="t"/> aims for on this artifact: 1 at the
    /// roots, <see cref="XenoArtifactComponent.SeverityCap"/> at the leaves.
    /// </summary>
    public static float GetTargetDanger(XenoArtifactComponent artifact, float t)
    {
        return 1f + (artifact.SeverityCap - 1f) * GetSeverityCurve(artifact.SeverityShape, t);
    }

    /// <summary>
    /// The trigger difficulty a node at normalised depth <paramref name="t"/> aims for. Triggers ride
    /// the same curve as effects so the hard-to-stage ones cluster on the dangerous leaves, but they
    /// always run the full 1..5 regardless of cap: a gentle artifact is gentle in what it does to
    /// you, not in what it asks of you.
    /// </summary>
    public static float GetTargetTriggerDifficulty(XenoArtifactComponent artifact, float t)
    {
        return 1f + 4f * GetSeverityCurve(artifact.SeverityShape, t);
    }

    /// <summary>
    /// Gaussian reweighting of an entry rated <paramref name="value"/> against <paramref name="target"/>.
    /// </summary>
    public static float GetSeverityKernel(float value, float target)
    {
        var d = (value - target) / SeverityKernelSigma;
        return MathF.Exp(-d * d);
    }

    /// <summary>
    /// Whether enough of the graph is solved to name the severity peak: the player has reached the
    /// deepest layer of some segment, where the cap actually lives.
    /// </summary>
    public bool IsSeverityPeakRevealed(Entity<XenoArtifactComponent> ent)
    {
        foreach (var segment in GetSegments(ent))
        {
            var maxDepth = 0;
            foreach (var node in segment)
                maxDepth = Math.Max(maxDepth, node.Comp.Depth);

            foreach (var node in segment)
            {
                if (!node.Comp.Locked && node.Comp.Depth == maxDepth)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether enough of the graph is solved to name the severity profile's shape: any three nodes
    /// unlocked (or the whole artifact, on a tiny one). Depth deliberately does not matter, so a
    /// crew can go wide across cheap roots to scout the profile before deciding whether to go deep.
    /// </summary>
    public bool IsSeverityProfileRevealed(Entity<XenoArtifactComponent> ent)
    {
        var total = 0;
        var unlocked = 0;
        foreach (var node in GetAllNodes(ent))
        {
            total++;
            if (!node.Comp.Locked)
                unlocked++;
        }

        return unlocked >= Math.Min(3, total);
    }

    /// <summary>
    /// Rolls the shape and cap for a freshly generated artifact.
    /// </summary>
    public void RollSeverityProfile(Entity<XenoArtifactComponent> ent)
    {
        ent.Comp.SeverityShape = RobustRandom.Pick(ent.Comp.SeverityShapeWeights);
        ent.Comp.SeverityCap = RobustRandom.Pick(ent.Comp.SeverityCapWeights);
        Dirty(ent);
    }

    /// <summary>
    /// Picks a trigger from what is left in the pool, weighted toward <paramref name="targetDifficulty"/>
    /// and removed from the pool. The pool is already a without-replacement draw from the weighted
    /// roster, so this only decides the order triggers are handed out in; it can never starve a node.
    /// </summary>
    public XenoArchTriggerPrototype PickTriggerForTarget(List<XenoArchTriggerPrototype> pool, float targetDifficulty)
    {
        var weights = new Dictionary<XenoArchTriggerPrototype, float>(pool.Count);
        foreach (var trigger in pool)
        {
            var k = GetSeverityKernel(GetTriggerDifficulty(trigger), targetDifficulty);
            if (k >= SeverityKernelFloor)
                weights[trigger] = k;
        }

        if (weights.Count == 0)
        {
            foreach (var trigger in pool)
                weights[trigger] = 1f;
        }

        var pick = RobustRandom.Pick(weights);
        pool.Remove(pick);
        return pick;
    }

    /// <summary>
    /// Picks an effect prototype from <paramref name="table"/>, author weights multiplied by how close
    /// each entry's danger sits to <paramref name="targetDanger"/>. Walks the selector tree rather than
    /// calling it, so the form-specific sub-tables and their weights are honoured without a second
    /// copy of the roster split by tier.
    /// </summary>
    public EntProtoId PickEffectForTarget(EntityTableSelector table, float targetDanger)
    {
        var flat = new List<(EntProtoId Id, float Weight)>();
        FlattenEffectTable(table, 1f, flat);

        var weights = new Dictionary<EntProtoId, float>(flat.Count);
        foreach (var (id, weight) in flat)
        {
            var k = GetSeverityKernel(GetEffectDanger(id), targetDanger);
            if (k < SeverityKernelFloor)
                continue;

            weights[id] = weights.GetValueOrDefault(id) + weight * k;
        }

        if (weights.Count == 0)
        {
            foreach (var (id, weight) in flat)
                weights[id] = weights.GetValueOrDefault(id) + weight;
        }

        return RobustRandom.Pick(weights);
    }

    /// <summary>
    /// Reads the authored danger off an effect prototype, or the component default if it has none.
    /// </summary>
    public float GetEffectDanger(EntProtoId id)
    {
        if (PrototypeManager.TryIndex(id, out var proto)
            && proto.TryGetComponent<XenoArtifactNodeComponent>(out var node, EntityManager.ComponentFactory))
            return node.Danger;

        return new XenoArtifactNodeComponent().Danger;
    }

    /// <summary>
    /// Flattens an entity table into (prototype, probability mass) pairs. Group weights are
    /// normalised per level so the mass matches what <c>GetSpawns</c> would have rolled.
    /// Public so the integration tests can enumerate exactly what the tables can roll.
    /// </summary>
    public void FlattenEffectTable(EntityTableSelector selector, float scale, List<(EntProtoId Id, float Weight)> output)
    {
        switch (selector)
        {
            case EntSelector ent:
                output.Add((ent.Id, scale));
                break;
            case NestedSelector nested:
                FlattenEffectTable(PrototypeManager.Index(nested.TableId).Table, scale, output);
                break;
            case GroupSelector group:
                var total = group.Children.Sum(c => c.Weight);
                if (total <= 0f)
                    break;
                foreach (var child in group.Children)
                    FlattenEffectTable(child, scale * child.Weight / total, output);
                break;
            case AllSelector all:
                foreach (var child in all.Children)
                    FlattenEffectTable(child, scale, output);
                break;
        }
    }

    #endregion

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
    public const float NodeDifficultyExponent = 1.7f; // Triad: 1.3 pre-curve; raised so the severity cap is worth gambling for (cap-5 vs cap-2 spread)

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

    #region Depth term

    /// <summary>
    /// Base of the predecessor-count term in node research value, upstream's 1.4. With the severity
    /// curve on, danger already climbs with depth, so this term at 1.4 double-dips: a leaf paid the
    /// 1.4^((n+1)^1.2) ramp AND the difficulty scale its curve-assigned danger earns. Re-fitted by
    /// the sampler with the curve enabled.
    /// </summary>
    public const float DepthValueBase = 1.25f; // Frontier: 1.4

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
    /// Payout multiplier on a full solve, and the base of the exponential ramp toward it.
    /// </summary>
    public const float FullSolveMultiplier = 5.0f;

    /// <summary>
    /// Whole-artifact payout multiplier from how much of the graph is unlocked: FullSolve^f, so
    /// every unlock compounds the payout by the same factor. x1 at nothing, x2.24 at half, exactly
    /// x5 on the last node, no snap. Shared between the price handler and the analyzer extract path
    /// so credits and research points cannot drift apart.
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
        return MathF.Pow(FullSolveMultiplier, f);
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

/// <summary>
/// Triad: how an artifact's danger climbs from its roots to its leaves.
/// </summary>
[Serializable, NetSerializable]
public enum XenoArtifactSeverityShape : byte
{
    /// <summary> Even climb, one tier every few layers. </summary>
    Linear,
    /// <summary> Front-loaded: gets dangerous early, then plateaus near the cap. </summary>
    Log,
    /// <summary> Back-loaded: safe for most of the graph, then a cliff on the last layers. </summary>
    Exp,
}

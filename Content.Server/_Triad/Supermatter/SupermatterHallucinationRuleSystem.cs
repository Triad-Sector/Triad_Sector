using Content.Server.GameTicking.Rules;
using Content.Server.Traits.Assorted;
using Content.Shared._EE.Supermatter.Components;
using Content.Shared.GameTicking.Components;
using Content.Shared.Humanoid;
using Content.Shared.Mind.Components;
using Content.Shared.Silicons.Laws.Components;
using Content.Shared.Traits.Assorted;
using Robust.Shared.Map;

namespace Content.Server._Triad.Supermatter;

/// <summary>
/// Runs <see cref="SupermatterHallucinationRuleComponent"/>: paracusia for everyone on the
/// crystal's map, cleaned up when the rule's duration expires.
/// </summary>
public sealed partial class SupermatterHallucinationRuleSystem : GameRuleSystem<SupermatterHallucinationRuleComponent>
{
    [Dependency] private ParacusiaSystem _paracusia = default!;

    /// <summary>
    /// Starts the rule scoped to <paramref name="map"/>. Use this rather than
    /// <c>GameTicker.StartGameRule</c> directly, since the map has to be seeded between the rule
    /// entity being spawned and the rule starting.
    /// </summary>
    /// <returns>False if the prototype is missing or isn't a hallucination rule.</returns>
    public bool StartOnMap(string ruleId, MapId map)
    {
        var ruleEntity = GameTicker.AddGameRule(ruleId);

        if (!TryComp<SupermatterHallucinationRuleComponent>(ruleEntity, out var rule))
        {
            Log.Error($"Tried to start {ruleId} as a supermatter hallucination rule, but it has no {nameof(SupermatterHallucinationRuleComponent)}.");
            Del(ruleEntity);
            return false;
        }

        rule.TargetMap = map;
        return GameTicker.StartGameRule(ruleEntity);
    }

    protected override void Started(EntityUid uid, SupermatterHallucinationRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        component.EndTime = Timing.CurTime + component.Duration;

        var query = EntityQueryEnumerator<MindContainerComponent, HumanoidAppearanceComponent, TransformComponent>();
        while (query.MoveNext(out var mob, out _, out _, out var xform))
        {
            if (component.TargetMap is { } map && xform.MapID != map)
                continue;

            // Silicons and anything explicitly immune don't hear the crystal
            if (HasComp<SiliconLawBoundComponent>(mob) || HasComp<SupermatterHallucinationImmuneComponent>(mob))
                continue;

            // EnsureComp returns true when the mob already had paracusia. Skip those, so that
            // ending the rule can't strip paracusia off someone who has it as a trait.
            if (EnsureComp<ParacusiaComponent>(mob, out var paracusia))
                continue;

            _paracusia.SetSounds(mob, component.Sounds, paracusia);
            _paracusia.SetTime(mob, component.MinTimeBetweenIncidents, component.MaxTimeBetweenIncidents, paracusia);
            _paracusia.SetDistance(mob, component.MaxSoundDistance, paracusia);

            component.AffectedEntities.Add(mob);
        }
    }

    protected override void Ended(EntityUid uid, SupermatterHallucinationRuleComponent component, GameRuleComponent gameRule, GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);

        foreach (var mob in component.AffectedEntities)
        {
            RemComp<ParacusiaComponent>(mob);
        }

        component.AffectedEntities.Clear();
    }

    protected override void ActiveTick(EntityUid uid, SupermatterHallucinationRuleComponent component, GameRuleComponent gameRule, float frameTime)
    {
        base.ActiveTick(uid, component, gameRule, frameTime);

        if (Timing.CurTime < component.EndTime)
            return;

        ForceEndSelf(uid, gameRule);
    }
}

using System.Linq;
using Content.Server.Beam;
using Content.Shared.Beam.Components;
using Content.Server.Lightning.Components;
using Content.Shared.Lightning;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes; // Mono
using Robust.Shared.Random;

namespace Content.Server.Lightning;

// TheShuEd:
//I've redesigned the lightning system to be more optimized.
//Previously, each lightning element, when it touched something, would try to branch into nearby entities.
//So if a lightning bolt was 20 entities long, each one would check its surroundings and have a chance to create additional lightning...
//which could lead to recursive creation of more and more lightning bolts and checks.

//I redesigned so that lightning branches can only be created from the point where the lightning struck, no more collide checks
//and the number of these branches is explicitly controlled in the new function.
public sealed partial class LightningSystem : SharedLightningSystem
{
    [Dependency] private BeamSystem _beam = default!;
    [Dependency] private IPrototypeManager _proto = default!; // Mono
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private TransformSystem _transform = default!;

    /// <summary>
    /// Hard ceiling on how deeply lightning may re-enter itself before a cascade is abandoned.
    /// </summary>
    // Triad: arcDepth bounds a single call chain, but it is a parameter, so anything that shoots
    // lightning in response to being hit by lightning starts a fresh budget and the cascade never has
    // to end. Two such entities in range of each other ping-pong until the stack runs out, which took
    // the server down in prod on 2026-08-06 ("Stack overflow.", then a fresh boot). This counter spans
    // the whole re-entrant chain rather than one call, so it closes the loop wherever it is closed.
    private const int MaxLightningRecursion = 16;

    private int _lightningRecursion;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LightningComponent, ComponentRemove>(OnRemove);
    }

    private void OnRemove(EntityUid uid, LightningComponent component, ComponentRemove args)
    {
        if (!TryComp<BeamComponent>(uid, out var lightningBeam) || !TryComp<BeamComponent>(lightningBeam.VirtualBeamController, out var beamController))
        {
            return;
        }

        beamController.CreatedBeams.Remove(uid);
    }

    /// <summary>
    /// Fires lightning from user to target
    /// </summary>
    /// <param name="user">Where the lightning fires from</param>
    /// <param name="target">Where the lightning fires to</param>
    /// <param name="lightningPrototype">The prototype for the lightning to be created</param>
    /// <param name="triggerLightningEvents">if the lightnings being fired should trigger lightning events.</param>
    public void ShootLightning(EntityUid user, EntityUid target, string lightningPrototype = "Lightning", bool triggerLightningEvents = true)
    {
        // Mono
        EntProtoId? spawnOnHit = null;
        var proto = _proto.Index(lightningPrototype);
        if (proto.TryComp<LightningComponent>(out var lightningComp, EntityManager.ComponentFactory))
            spawnOnHit = lightningComp.SpawnOnHit;

        ShootLightning(user, target, lightningPrototype, triggerLightningEvents);
    }

    // Mono - for optimisation purposes
    private void ShootLightning(EntityUid user, EntityUid target, EntProtoId? spawnOnHit, string lightningPrototype = "Lightning", bool triggerLightningEvents = true)
    {
        var spriteState = LightningRandomizer();
        _beam.TryCreateBeam(user, target, lightningPrototype, spriteState);

        if (triggerLightningEvents) // we don't want certain prototypes to trigger lightning level events
        {
            var ev = new HitByLightningEvent(user, target);
            RaiseLocalEvent(target, ref ev);
        }

        if (spawnOnHit != null)
            Spawn(spawnOnHit.Value, _transform.GetMapCoordinates(target));
    }


    /// <summary>
    /// Looks for objects with a LightningTarget component in the radius, prioritizes them, and hits the highest priority targets with lightning.
    /// </summary>
    /// <param name="user">Where the lightning fires from</param>
    /// <param name="range">Targets selection radius</param>
    /// <param name="boltCount">Number of lightning bolts</param>
    /// <param name="lightningPrototype">The prototype for the lightning to be created</param>
    /// <param name="arcDepth">how many times to recursively fire lightning bolts from the target points of the first shot.</param>
    /// <param name="triggerLightningEvents">if the lightnings being fired should trigger lightning events.</param>
    public void ShootRandomLightnings(EntityUid user, float range, int boltCount, string lightningPrototype = "Lightning", int arcDepth = 0, bool triggerLightningEvents = true)
    {
        // Mono
        EntProtoId? spawnOnHit = null;
        var proto = _proto.Index(lightningPrototype);
        if (proto.TryComp<LightningComponent>(out var lightningComp, EntityManager.ComponentFactory))
            spawnOnHit = lightningComp.SpawnOnHit;

        ShootRandomLightnings(user, range, boltCount, spawnOnHit, lightningPrototype, arcDepth, triggerLightningEvents);
    }

    // Mono - for optimisation purposes
    private void ShootRandomLightnings(EntityUid user, float range, int boltCount, EntProtoId? spawnOnHit, string lightningPrototype = "Lightning", int arcDepth = 0, bool triggerLightningEvents = true)
    {
        //TODO: add support to different priority target tablem for different lightning types
        //TODO: Remove Hardcode LightningTargetComponent (this should be a parameter of the SharedLightningComponent)
        //TODO: This is still pretty bad for perf but better than before and at least it doesn't re-allocate
        // several hashsets every time

        // Triad: see MaxLightningRecursion. Bail before doing any work so a runaway cascade stops
        // cheaply, and warn once per cascade rather than once per bolt.
        if (_lightningRecursion >= MaxLightningRecursion)
        {
            if (_lightningRecursion == MaxLightningRecursion)
                Log.Warning($"Lightning from {ToPrettyString(user)} exceeded {MaxLightningRecursion} levels of recursion, abandoning the cascade.");

            return;
        }

        _lightningRecursion++;

        try
        {
            var targets = _lookup.GetEntitiesInRange<LightningTargetComponent>(_transform.GetMapCoordinates(user), range).ToList();
            // Triad: strike-attempt hook. Each candidate may adjust its effective priority and hit chance
            // from live state (tesla coils bid by charge headroom) before the volley is sorted and rolled.
            // Targets with no subscriber keep their static values, so behavior is unchanged for them.
            // _random.Shuffle(targets);
            // targets.Sort((x, y) => y.Comp.Priority.CompareTo(x.Comp.Priority));
            var candidates = new List<(Entity<LightningTargetComponent> Target, int Priority, float HitProbability)>(targets.Count);
            foreach (var target in targets)
            {
                var attempt = new LightningStrikeAttemptEvent(user, target.Comp.Priority, target.Comp.HitProbability);
                RaiseLocalEvent(target, ref attempt);
                candidates.Add((target, attempt.Priority, Math.Clamp(attempt.HitProbability, 0f, 1f)));
            }
            _random.Shuffle(candidates);
            candidates.Sort((x, y) => y.Priority.CompareTo(x.Priority));

            int shootedCount = 0;
            int count = -1;
            while (shootedCount < boltCount)
            {
                count++;

                if (count >= candidates.Count) { break; }

                var curTarget = candidates[count];
                if (!_random.Prob(curTarget.HitProbability)) //Chance to ignore target
                    continue;

                ShootLightning(user, curTarget.Target.Owner, spawnOnHit, lightningPrototype, triggerLightningEvents);
                if (arcDepth - curTarget.Target.Comp.LightningResistance > 0)
                {
                    ShootRandomLightnings(curTarget.Target.Owner, range, 1, spawnOnHit, lightningPrototype, arcDepth - curTarget.Target.Comp.LightningResistance, triggerLightningEvents);
                }
                shootedCount++;
            }
        }
        finally
        {
            _lightningRecursion--;
        }
    }
}

/// <summary>
/// Raised directed on the target when an entity becomes the target of a lightning strike (not when touched)
/// </summary>
/// <param name="Source">The entity that created the lightning</param>
/// <param name="Target">The entity that was struck by lightning.</param>
[ByRefEvent]
public readonly record struct HitByLightningEvent(EntityUid Source, EntityUid Target);

/// <summary>
/// Triad: raised directed on each candidate target before a lightning volley is sorted and rolled.
/// Subscribers may adjust <see cref="Priority"/> and <see cref="HitProbability"/> from live state
/// (e.g. a tesla coil bidding by charge headroom). Both start at the target's static
/// LightningTargetComponent values; HitProbability is clamped to [0, 1] after the event.
/// </summary>
/// <param name="Source">The entity shooting the lightning volley</param>
/// <param name="Priority">Effective sort priority for this volley; higher is struck first</param>
/// <param name="HitProbability">Effective chance this target is not skipped by the roll</param>
[ByRefEvent]
public record struct LightningStrikeAttemptEvent(EntityUid Source, int Priority, float HitProbability);

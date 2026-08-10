using Content.Server._Mono.NPC.HTN;
using Content.Server.NPC.HTN;
using Content.Server.Shuttles.Components;
using Content.Shared.NPC;
using Robust.Shared.Map;

namespace Content.Server._Triad.NPC;

/// <summary>
/// Tears down an NPC ship brain's helm and guns when the brain is put to sleep.
///
/// Sleeping only removes <see cref="ActiveNPCComponent"/>; it never shuts the HTN plan down,
/// so <see cref="ShipSteererComponent"/> survived and kept piloting per-tick via the mover,
/// chasing blackboard coordinates anchored to the target entity. A drone whose target FTL'd
/// away would sleep mid-chase and then autopilot itself across the sector (typically into the
/// core) the moment the target's map matched again. Killing the plan and the steering/targeting
/// components here leaves the hull decelerating in place, where grid cleanup despawns it.
/// </summary>
public sealed partial class ShipNpcSleepSystem : EntitySystem
{
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private ShipSteeringSystem _steering = default!;
    [Dependency] private ShipTargetingSystem _targeting = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ActiveNPCComponent, ComponentShutdown>(OnActiveNpcShutdown);
    }

    private void OnActiveNpcShutdown(EntityUid uid, ActiveNPCComponent comp, ComponentShutdown args)
    {
        if (TerminatingOrDeleted(uid))
            return;

        // Ship brains only. Mob NPCs never carry these components, and player autopilot
        // consoles (which share the HTN + steering stack) must keep flying while "asleep".
        if (HasComp<ShuttleConsoleComponent>(uid)
            || !HasComp<ShipSteererComponent>(uid) && !HasComp<ShipTargetingComponent>(uid))
            return;

        // Kill the plan first: runs ConditionalShutdown on background (PlanFinished) operators
        // and nulls the plan, so waking replans from the root instead of resuming a stale chase.
        if (TryComp<HTNComponent>(uid, out var htn))
        {
            _htn.ShutdownPlan(htn);

            // The combat compounds set removeKeyOnFinish: false, so the target keys outlive the
            // plan. Scrub them, or a KeyExistsPrecondition branch can re-plan onto the escaped
            // target on wake. Key names are the _Mono/NPCs/Shuttle YAML convention.
            htn.Blackboard.Remove<EntityUid>("Target");
            htn.Blackboard.Remove<EntityCoordinates>("TargetCoordinates");
        }

        // ShutdownPlan only conditionally shuts down PlanFinished-flagged operators; the default
        // TaskFinished operators never get their teardown from it, so stop both directly.
        // Steering's Stop also restores inertia dampening so the hull parks for grid cleanup.
        _steering.Stop(uid);
        _targeting.Stop(uid);
    }
}

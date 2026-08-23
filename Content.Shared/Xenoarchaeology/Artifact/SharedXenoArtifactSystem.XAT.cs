using System.Linq;
using Content.Shared.Chemistry;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems; // Triad: BeforeStaminaDamageEvent
using Content.Shared.Electrocution; // Triad
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Hitscan.Events; // Triad
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Artifact.XAT.Components;
using Content.Shared.Tiles; // Frontier
using Robust.Shared.Physics.Events; // Triad: StartCollideEvent

namespace Content.Shared.Xenoarchaeology.Artifact;

public abstract partial class SharedXenoArtifactSystem
{
    private void InitializeXAT()
    {
        XATRelayLocalEvent<DamageChangedEvent>();
        XATRelayLocalEvent<InteractUsingEvent>();
        XATRelayLocalEvent<PullStartedMessage>();
        XATRelayLocalEvent<AttackedEvent>();
        XATRelayLocalEvent<XATToolUseDoAfterEvent>();
        XATRelayLocalEvent<InteractHandEvent>();
        XATRelayLocalEvent<ReactionEntityEvent>();
        XATRelayLocalEvent<LandEvent>();
        // Triad: the wizard-harvest triggers arrived without their relay plumbing, and a trigger whose
        // event never reaches the node is unsolvable no matter what the prototype says. One line here
        // per delivery path; BeingMicrowavedEvent is relayed server-side because it is a server class
        // event on this tree.
        XATRelayLocalEvent<XATInteractWithDoAfterEvent>();
        XATRelayLocalEvent<ElectrocutionAttemptEvent>();
        XATRelayLocalEvent<StartCollideEvent>();
        XATRelayLocalEvent<HitscanRaycastStrikeEvent>();
        XATRelayLocalEvent<BeforeStaminaDamageEvent>();

        // special case this one because we need to order the messages
        SubscribeLocalEvent<XenoArtifactComponent, ExaminedEvent>(OnExamined);
    }

    /// <summary> Relays artifact events for artifact nodes. </summary>
    protected void XATRelayLocalEvent<T>() where T : notnull
    {
        SubscribeLocalEvent<XenoArtifactComponent, T>(RelayEventToNodes);
    }

    private void OnExamined(Entity<XenoArtifactComponent> ent, ref ExaminedEvent args)
    {
        using (args.PushGroup(nameof(XenoArtifactComponent)))
        {
            RelayEventToNodes(ent, ref args);
        }
    }

    protected void RelayEventToNodes<T>(Entity<XenoArtifactComponent> ent, ref T args) where T : notnull
    {
        var ev = new XenoArchNodeRelayedEvent<T>(ent, args);

        var nodes = GetAllNodes(ent);
        foreach (var node in nodes)
        {
            RaiseLocalEvent(node, ref ev);
        }
    }

    /// <summary>
    /// Attempts to shift artifact into unlocking state, in which it is going to listen to interactions, that could trigger nodes.
    /// </summary>
    public void TriggerXenoArtifact(Entity<XenoArtifactComponent> ent, Entity<XenoArtifactNodeComponent>? node, bool force = false)
    {
        // limits spontaneous chain activations, also prevents spamming every triggering tool to activate nodes
        // without real knowledge about triggers
        if (!force && _timing.CurTime < ent.Comp.NextUnlockTime)
            return;

        // Frontier: Disable activations on protected grids
        if (TryComp(ent, out TransformComponent? xform)
            && TryComp<ProtectedGridComponent>(xform.GridUid, out var prot)
            && prot.PreventArtifactTriggers)
        {
            return;
        }
        // End Frontier: Disable activations on protected grids

        if (!_unlockingQuery.TryGetComponent(ent, out var unlockingComp))
        {
            unlockingComp = EnsureComp<XenoArtifactUnlockingComponent>(ent);
            unlockingComp.EndTime = _timing.CurTime + ent.Comp.UnlockStateDuration;
            Log.Debug($"{ToPrettyString(ent)} entered unlocking state");

            if (_net.IsServer)
                _popup.PopupEntity(Loc.GetString("artifact-unlock-state-begin"), ent);
            Dirty(ent);
        }
        else if (node != null)
        {
            var index = GetIndex(ent, node.Value);
            // Frontier: lenience with node unlocking

            // var predecessorNodeIndices = GetPredecessorNodes((ent, ent), index);
            // var successorNodeIndices = GetSuccessorNodes((ent, ent), index);
            // if (unlockingComp.TriggeredNodeIndexes.Count == 0
            //     || unlockingComp.TriggeredNodeIndexes.All(
            //         x => predecessorNodeIndices.Contains(x) || successorNodeIndices.Contains(x)
            //     )
            //    )
            //     // we add time on each new trigger, if it is not going to fail us
            //     unlockingComp.EndTime += ent.Comp.UnlockStateIncrementPerNode;

            if (!unlockingComp.TriggeredNodeIndexes.Contains(index))
                unlockingComp.EndTime += ent.Comp.UnlockStateIncrementPerNode;
            // End Frontier: lenience with node unlocking
        }

        if (node != null && unlockingComp.TriggeredNodeIndexes.Add(GetIndex(ent, node.Value)))
        {
            Dirty(ent, unlockingComp);
        }
    }
}

/// <summary>
/// Event wrapper for XenoArch Trigger events.
/// </summary>
[ByRefEvent]
public record struct XenoArchNodeRelayedEvent<TEvent>(Entity<XenoArtifactComponent> Artifact, TEvent Args)
{
    /// <summary>
    /// Original event.
    /// </summary>
    public TEvent Args = Args;

    /// <summary>
    /// Artifact entity, that received original event.
    /// </summary>
    public Entity<XenoArtifactComponent> Artifact = Artifact;
}

using Content.Shared.Damage.Systems; // Triad: BeforeStaminaDamageEvent lives beside StaminaSystem here
using Content.Shared.Popups;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Artifact.XAT.Components;
using Robust.Shared.Network;

namespace Content.Shared.Xenoarchaeology.Artifact.XAT;

/// <summary>
/// System for xeno artifact trigger that requires stamina damage to be applied to artifact within a timeframe.
/// </summary>
public sealed partial class XATStaminaDamageThresholdReachedSystem : BaseXATSystem<XATStaminaDamageThresholdReachedComponent>
{
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly INetManager _net = default!;

    public override void Initialize()
    {
        base.Initialize();

        XATSubscribeDirectEvent<BeforeStaminaDamageEvent>(OnStaminaDamage);
    }

    /// <summary>
    /// Respond to stamina damage, store amount received over multiple interactions.
    /// If accumulated amount exceeds threshold, trigger artifact and reset.
    /// </summary>
    private void OnStaminaDamage(Entity<XenoArtifactComponent> artifact, Entity<XATStaminaDamageThresholdReachedComponent, XenoArtifactNodeComponent> node, ref BeforeStaminaDamageEvent args)
    {
        if (args.Value <= 0) //prevent stamina regeneration being considered
            return;

        // Triad: BeforeStaminaDamageEvent is raised on both sides and the client re-runs it on every
        // prediction pass, so the accumulator counted one hit several times and drifted until the
        // next server state corrected it. XATDamageThresholdReached already guards its accumulator
        // this way; this one was missed when the trigger came over in the rework.
        if (!Timing.IsFirstTimePredicted)
            return;

        node.Comp1.AccumulatedDamage += args.Value;
        Dirty(node);

        if (node.Comp1.AccumulatedDamage >= node.Comp1.DamageNeeded)
        {
            node.Comp1.AccumulatedDamage = 0;
            Dirty(node);
            Trigger(artifact, node);
        }
        else
        {
            // Triad: unguarded PopupEntity in a shared system shows once from the client's own pass
            // and again when the server's popup arrives. The rest of this feature lets the server own
            // its popups; match it.
            if (node.Comp1.InsufficientDamagePopup != null && _net.IsServer)
                _popup.PopupEntity(Loc.GetString(node.Comp1.InsufficientDamagePopup), artifact); //tell user they need to interact in the same way more times
        }


    }
}

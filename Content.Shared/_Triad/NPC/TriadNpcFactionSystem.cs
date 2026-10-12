using Content.Shared._Triad.Kitchen;
using Content.Shared.Popups;
using Content.Shared.Interaction.Events;
using Content.Shared.NPC.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._Triad.NPC;

public sealed partial class TriadNpcFactionSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NpcFactionSystem _npcFaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    [SubscribeLocalEvent]
    private void OnAddFactionMapInit(Entity<AddNpcFactionOnMapInitComponent> ent, ref MapInitEvent args)
    {
        _npcFaction.AddFactions(ent.Owner, ent.Comp.AddedFactions);
    }

    [SubscribeLocalEvent]
    private void OnAttackingBlockAttempt(Entity<BlockAttackingNpcFactionsComponent> ent, ref AttackAttemptEvent args)
    {
        if (args.Target is not { } target)
            return;

        if (args.Disarm)
            return;

        if (!_npcFaction.IsMemberOfAny(target, ent.Comp.BlockedFactions))
            return;

        args.Cancel();

        // Cooldown is needed because the input events for melee/shooting etc. will fire continuously
        if (target == ent.Comp.LastAttackedEntity
            && !(_timing.CurTime > ent.Comp.NextPopupTime))
            return;

        if (ent.Comp.Popup != null)
        {
            _popup.PopupClient(Loc.GetString(ent.Comp.Popup, ("entity", target)), ent.Owner, ent.Owner);
            ent.Comp.NextPopupTime = _timing.CurTime + ent.Comp.PopupCooldown;
            ent.Comp.LastAttackedEntity = target;
        }
    }

    [SubscribeLocalEvent]
    private void OnButcherBlockAttempt(Entity<BlockButcheringNpcFactionsComponent> ent, ref ButcherAttemptEvent args)
    {
        if (!_npcFaction.IsMemberOfAny(args.Target, ent.Comp.BlockedFactions))
            return;

        args.Cancel();

        if (ent.Comp.Popup != null)
            args.Popup = Loc.GetString(ent.Comp.Popup, ("entity", args.Target));
    }
}

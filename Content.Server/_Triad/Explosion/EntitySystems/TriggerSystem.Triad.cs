using Content.Shared.Implants.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Morgue.Components;

namespace Content.Server.Explosion.EntitySystems;

public sealed partial class TriggerSystem : EntitySystem
{
    public void UpdateRattleTimer()
    {
        var query = EntityQueryEnumerator<SubdermalImplantComponent, RattleComponent>();

        while (query.MoveNext(out var implantUid, out var implant, out var rattle))
        {
            if (implant.ImplantedEntity is not { } host)
                continue;

            if (!TryComp<MobStateComponent>(host, out var mobState))
                continue;

            var shouldReset =
                mobState.CurrentState != MobState.Dead &&
                (rattle.DeathTime != TimeSpan.Zero ||
                rattle.NextTrigger != TimeSpan.Zero);

            if (shouldReset)
            {
                rattle.DeathTime = TimeSpan.Zero;
                rattle.NextTrigger = TimeSpan.Zero;
                continue;
            }

            if (rattle.NextTrigger == TimeSpan.Zero)
                continue;

            // Check the time then trigger the implant again
            if (rattle.NextTrigger > _timing.CurTime)
                continue;

            var isInMorgue = _container.TryGetContainingContainer(host, out var container) && HasComp<MorgueComponent>(container.Owner);
            // In-case we want to add other checks
            var shouldAlert = !isInMorgue;

            if (!shouldAlert)
            {
                rattle.NextTrigger = _timing.CurTime + rattle.RetriggerDelay;
                continue;
            }

            Trigger(implantUid);
        }
    }
}

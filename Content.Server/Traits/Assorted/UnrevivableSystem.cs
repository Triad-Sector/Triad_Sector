using Content.Server._NF.Traits.Assorted;
using Content.Shared.Cloning.Events;
using Content.Shared.Traits.Assorted;

namespace Content.Server.Traits.Assorted;

public sealed class UnrevivableSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<UncloneableComponent, CloningAttemptEvent>(OnCloningAttempt);
    }

    private void OnCloningAttempt(Entity<UncloneableComponent> ent, ref CloningAttemptEvent args)
    {
        args.Cancelled = true;
    }
}

using Content.Shared._Goobstation.Flashbang;
using Content.Shared.Flash.Components;
using Content.Shared.StatusEffect;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared.Flash;

public abstract class SharedFlashSystem : EntitySystem
{
    public ProtoId<StatusEffectPrototype> FlashedKey = "Flashed";

    // Starlight edit start - Flash multiplier
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FlashModifierComponent, FlashDurationMultiplierEvent>(OnFlashModifier);
    }

    private void OnFlashModifier(Entity<FlashModifierComponent> ent, ref FlashDurationMultiplierEvent args)
    {
        args.Multiplier *= ent.Comp.Modifier;
    }
    // Starlight edit end

    public virtual void FlashArea(Entity<FlashComponent?> source, EntityUid? user, float range, float duration, float slowTo = 0.8f, bool displayPopup = false, float probability = 1f, SoundSpecifier? sound = null)
    {
    }
}

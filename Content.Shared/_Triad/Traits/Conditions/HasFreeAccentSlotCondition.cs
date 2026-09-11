/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
using Content.Shared._DV.Traits;
using Content.Shared._DV.Traits.Conditions;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Traits.Conditions;

/// <summary>
/// Triad: gates a paid dialect accent so a species that already speaks a heavy native accent (the Dwarven
/// Brogue, the Goblin Cant) can't stack a second dialect on top and garble its speech. Passes when the
/// player is NOT one of those species, OR has taken the Accentless trait (which strips the native accent and
/// frees the slot). Everyone else always passes. Fail-open: an unknown species or missing profile does not
/// block, so a content gap can never crash the spawn path.
/// </summary>
public sealed partial class HasFreeAccentSlotCondition : BaseTraitCondition
{
    // Species whose native accent is heavy enough that a second dialect would mangle speech. Kept in code,
    // not a DataField, because the identical gate rides on every paid accent; change the set here. Public so
    // the client lobby mirror (TraitEntry) shares one source of truth and the two evaluators can't drift.
    public static readonly ProtoId<SpeciesPrototype>[] HeavyNativeAccentSpecies =
        { "Dwarf", "Goblin" };

    // Taking this trait clears the native accent, which frees the slot for a chosen dialect.
    public static readonly ProtoId<TraitPrototype> AccentlessTrait = "Accentless";

    protected override bool EvaluateImplementation(TraitConditionContext ctx)
    {
        // Not a heavy-native-accent species (or species unknown) -> the slot is always free.
        if (ctx.SpeciesId is not { } species || Array.IndexOf(HeavyNativeAccentSpecies, species) < 0)
            return true;

        // Heavy-native species: free only once Accentless has cleared the native accent.
        return ctx.Profile is { } profile
            && profile.GetValidTraits(profile.TraitPreferences, ctx.Proto).Contains(AccentlessTrait);
    }

    public override string GetTooltip(IPrototypeManager proto, ILocalizationManager loc, int depth)
    {
        return new string(' ', depth * 2) + "- " + loc.GetString("trait-condition-free-accent-slot") + Environment.NewLine;
    }
}

/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
using Content.Server._Triad.Speech.Components;
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;

namespace Content.Server._Triad.Speech.EntitySystems;

/// <summary>
///     Freeport Corsair accent. Word swaps + the salty g-drop (sailin', fightin') on the shared
///     AccentHelpers, with "Arrr" prefix interjections and pirate suffix tics. Triad-original, inspired
///     by the upstream Pirate accent (trilled-r isn't modelled -- it does not survive as text).
/// </summary>
public sealed class FreeportCorsairAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FreeportCorsairAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    public string Accentuate(string message, FreeportCorsairAccentComponent component)
    {
        var msg = _replacement.ApplyReplacements(message, "freeportcorsair");

        // Salty g-drop: sailing -> sailin', fighting -> fightin' (keep-list spares king/ring).
        msg = AccentHelpers.DropG(msg);

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        msg = AccentHelpers.FixArticles(msg);

        if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

        if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));

        return msg;
    }

    private void OnAccentGet(EntityUid uid, FreeportCorsairAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}

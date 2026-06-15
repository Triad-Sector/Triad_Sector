using Content.Server.Speech.Components;
using Robust.Shared.Random;

namespace Content.Server.Speech.EntitySystems;

public sealed class BoganAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BoganAccentComponent, AccentGetEvent>(OnAccent);
    }

    private void OnAccent(EntityUid uid, BoganAccentComponent component, AccentGetEvent args)
    {
        var msg = _replacement.ApplyReplacements(args.Message, "bogan");

        // Triad: bogans drop the g too ("havin' a chat"); shared keep-list spares king/ring/wing.
        msg = AccentHelpers.DropG(msg);

        if (string.IsNullOrWhiteSpace(msg))
        {
            args.Message = msg;
            return;
        }

        // Triad: re-agree a/an after swaps, then data-driven prob prefix/suffix tics with the shared
        // caps-aware placement (replaces the old hardcoded probs + _random.Next index math).
        msg = AccentHelpers.FixArticles(msg);

        if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

        if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));

        args.Message = msg;
    }
}

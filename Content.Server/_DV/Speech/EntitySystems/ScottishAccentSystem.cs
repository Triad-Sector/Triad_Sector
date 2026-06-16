using Content.Server._DV.Speech.Components;
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;

namespace Content.Server._DV.Speech.EntitySystems;

public sealed class ScottishAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!; // Triad: tic pools
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ScottishAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    // converts left word when typed into the right word. For example typing you becomes ye.
    public string Accentuate(string message, ScottishAccentComponent component)
    {
        var msg = _replacement.ApplyReplacements(message, "scottish");

        // Triad: was a flat word list. Add systematic g-drop (the list only covers a handful of -ing
        // words), a/an agreement, and data-driven Scots tics via the shared helpers.
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

    private void OnAccentGet(EntityUid uid, ScottishAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}

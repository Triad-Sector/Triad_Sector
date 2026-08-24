using System.Linq;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Shared.Random;

namespace Content.Shared._Triad.Dossier;

/// <summary>
///     Handles paper-doll exemplars used in BuPers species guidebook dossiers.
///     On MapInit, randomizes HumanoidAppearance with random species-appropriate
///     gender-aware undergarment markings baked in — no jumpsuit, no shoes. Keeps the
///     dossier paper doll uniform: every species shows their natural body shape with
///     basic personnel coverage, decoupled from clothing displacement quirks per species.
/// </summary>
/// <remarks>
///     Sits in Shared (not Server) because the guidebook spawns its embedded entities
///     client-side; a server-only randomizer would never fire. Undergarments are baked
///     into the profile *before* LoadProfile runs because on a client-side entity there
///     is no subsequent state sync to re-trigger sprite layer rebuild — anything added
///     to the MarkingSet after LoadProfile would never render.
/// </remarks>
public sealed partial class DossierExemplarSystem : EntitySystem
{
    [Dependency] private SharedHumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private MarkingManager _markingManager = default!;
    [Dependency] private IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DossierExemplarComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<DossierExemplarComponent> ent, ref MapInitEvent args)
    {
        if (!TryComp<HumanoidAppearanceComponent>(ent, out var humanoid))
            return;

        var profile = HumanoidCharacterProfile.RandomWithSpecies(humanoid.Species);

        var markings = new List<Marking>(profile.Appearance.Markings);
        AddRandomUndergarments(markings, profile.Species, profile.Sex);
        profile = profile.WithCharacterAppearance(profile.Appearance.WithMarkings(markings));

        _humanoid.LoadProfile(ent, profile, humanoid);
    }

    private void AddRandomUndergarments(List<Marking> markings, string species, Sex sex)
    {
        // Top: only female base models get a top. Bras/sportsbras/binders only make sense on
        // bodies where the anatomical difference between sexes lives; male and unsexed go
        // bare-chested in the dossier.
        if (sex == Sex.Female)
        {
            var tops = _markingManager.MarkingsByCategoryAndSpecies(MarkingCategories.UndergarmentTop, species);
            if (tops.Count > 0)
                markings.Add(new Marking(_random.Pick(tops.Keys.ToList()), new List<Color> { Color.White }));
        }

        // Bottom: gender-neutral, pick freely from the species pool.
        var bottoms = _markingManager.MarkingsByCategoryAndSpecies(MarkingCategories.UndergarmentBottom, species);
        if (bottoms.Count > 0)
            markings.Add(new Marking(_random.Pick(bottoms.Keys.ToList()), new List<Color> { Color.White }));
    }
}

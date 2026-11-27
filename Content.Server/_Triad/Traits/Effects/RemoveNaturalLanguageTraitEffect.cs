using Content.Server._EinsteinEngines.Language;
using Content.Shared._DV.Traits.Effects;

namespace Content.Server._Triad.Traits.Effects;

/// <summary>
///     Removes the natural language (species language, basically) for an entity
/// </summary>
public sealed partial class RemoveNaturalLanguageTraitEffect : BaseTraitEffect
{
    public override void Apply(TraitEffectContext ctx)
    {
        var entMan = ctx.EntMan;
        var languageSys = entMan.System<LanguageSystem>();
        var user = ctx.Player;

        if (!entMan.TryGetComponent<LanguageKnowledgeComponent>(user, out var knowledge))
            return;

        var targetLanguage = knowledge.NaturalLanguage;
        if (targetLanguage != null)
            languageSys.RemoveLanguage(user, targetLanguage.Value, true, true);
    }
}

using Content.Shared._DV.CCVars;
using Content.Shared._DV.Traits;
using Content.Shared._DV.Traits.Conditions;
using Content.Shared._DV.Traits.Effects;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._DV.Traits;

/// <summary>
/// Server system that validates and applies traits to players on spawn.
/// </summary>
public sealed class TraitSystem : EntitySystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    private int _maxTraitCount;
    private int _maxTraitPoints;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);

        Subs.CVar(_config, DCCVars.MaxTraitCount, value => _maxTraitCount = value, true);
        Subs.CVar(_config, DCCVars.MaxTraitPoints, value => _maxTraitPoints = value, true);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        // Check if player's job allows traits
        if (args.JobId == null ||
            !_prototype.TryIndex<JobPrototype>(args.JobId, out var jobProto) ||
            !jobProto.ApplyTraits)
            return;

        // Use the species ID from the profile if for some reason we can't get the humanoid appearance
        ProtoId<SpeciesPrototype>? speciesId = args.Profile.Species;
        if (TryComp<HumanoidAppearanceComponent>(args.Mob, out var humanoid))
            speciesId = humanoid.Species;

        // Track disabled traits and reasons
        var disabledTraits = new Dictionary<ProtoId<TraitPrototype>, List<string>>();

        // Validate and collect valid traits. ValidateTraits returns them already in apply order (highest priority
        // first, ties broken by lower cost then id) so order-sensitive effects (e.g. add-vs-remove of the same
        // component) resolve deterministically.
        var validTraits = ValidateTraits(args.Mob, args.Profile.TraitPreferences, args.Player, args.JobId, speciesId, args.Profile, disabledTraits);

        foreach (var trait in validTraits)
            ApplyTrait(args.Mob, trait);

        // Send disabled traits notification to client if any were rejected
        if (disabledTraits.Count > 0)
        {
            RaiseNetworkEvent(new DisabledTraitsEvent(disabledTraits), args.Player);
        }
    }

    /// <summary>
    /// Validates a set of trait selections against all rules and returns the valid subset.
    /// </summary>
    private List<TraitPrototype> ValidateTraits(
        EntityUid player,
        IReadOnlySet<ProtoId<TraitPrototype>> selectedTraits,
        ICommonSession? session,
        ProtoId<JobPrototype>? jobId,
        ProtoId<SpeciesPrototype>? speciesId,
        HumanoidCharacterProfile? profile,
        Dictionary<ProtoId<TraitPrototype>, List<string>> disabledTraits)
    {
        var validTraits = new List<TraitPrototype>();
        var totalPoints = 0;
        var traitCount = 0;
        var categoryTraitCounts = new Dictionary<ProtoId<TraitCategoryPrototype>, int>();
        var categoryPointTotals = new Dictionary<ProtoId<TraitCategoryPrototype>, int>();

        // Build condition context
        var conditionCtx = new TraitConditionContext
        {
            Player = player,
            Session = session,
            EntMan = EntityManager,
            Proto = _prototype,
            CompFactory = _factory,
            LogMan = _log,
            JobId = jobId,
            SpeciesId = speciesId,
            Profile = profile,
        };

        // Resolve once (logging unknowns), then evaluate in a deterministic order (highest priority, then cheapest,
        // then id) so the surviving subset is stable across spawns and is returned already in apply order. Resolving
        // up front also keeps the sort comparator free of repeated prototype lookups. Ordinal keeps the tiebreak
        // host-independent.
        var sorted = new List<TraitPrototype>();
        foreach (var selectedId in selectedTraits)
        {
            if (_prototype.TryIndex(selectedId, out var resolved))
                sorted.Add(resolved);
            else
                Log.Warning($"Unknown trait ID in player preferences: {selectedId}");
        }

        sorted.Sort(static (a, b) =>
        {
            var byPriority = b.Priority.CompareTo(a.Priority); // highest priority first
            if (byPriority != 0)
                return byPriority;

            var byCost = a.Cost.CompareTo(b.Cost); // cheapest first
            return byCost != 0 ? byCost : string.CompareOrdinal(a.ID, b.ID);
        });

        // Triad: pre-compute extra category slots granted by selected traits (e.g. Foreigner granting
        // language slots). Done before the main loop so a granter raises the cap regardless of where it
        // sorts relative to the traits filling those slots. Only count granters whose own conditions pass,
        // so a rejected granter doesn't hand out slots.
        var categorySlotGrants = new Dictionary<ProtoId<TraitCategoryPrototype>, int>();
        foreach (var trait in sorted)
        {
            if (trait.GrantsCategorySlots.Count == 0)
                continue;
            if (!CheckConditions(trait, conditionCtx, new List<string>()))
                continue;
            foreach (var (cat, slots) in trait.GrantsCategorySlots)
            {
                categorySlotGrants.TryGetValue(cat, out var existing);
                categorySlotGrants[cat] = existing + slots;
            }
        }

        foreach (var trait in sorted)
        {
            ProtoId<TraitPrototype> traitId = trait.ID;
            var rejectionReasons = new List<string>();

            // Check global trait count limit. Triad: _maxTraitCount <= 0 means unlimited.
            if (_maxTraitCount > 0 && traitCount >= _maxTraitCount)
            {
                Log.Warning($"Trait {traitId} rejected: global trait count limit ({_maxTraitCount}) exceeded");
                rejectionReasons.Add(Loc.GetString("disabled-traits-reason-global-limit"));
                disabledTraits[traitId] = rejectionReasons;
                continue;
            }

            // Check global points limit. Triad: _maxTraitPoints <= 0 means unlimited, leaving per-category
            // MaxPoints as the only point budget (mirrors the _maxTraitCount <= 0 escape hatch above).
            if (_maxTraitPoints > 0 && totalPoints + trait.Cost > _maxTraitPoints)
            {
                Log.Warning($"Trait {traitId} rejected: global points limit ({_maxTraitPoints}) would be exceeded");
                rejectionReasons.Add(Loc.GetString("disabled-traits-reason-points-limit"));
                disabledTraits[traitId] = rejectionReasons;
                continue;
            }

            // Check category limits
            if (!ValidateCategoryLimits(trait, categoryTraitCounts, categoryPointTotals, categorySlotGrants, rejectionReasons))
            {
                Log.Warning($"Trait {traitId} rejected: category limits exceeded");
                disabledTraits[traitId] = rejectionReasons;
                continue;
            }

            // Check all conditions
            if (!CheckConditions(trait, conditionCtx, rejectionReasons))
            {
                Log.Warning($"Trait {traitId} rejected: conditions not met");
                disabledTraits[traitId] = rejectionReasons;
                continue;
            }

            // Trait is valid, add it
            validTraits.Add(trait);
            totalPoints += trait.Cost;
            traitCount++;

            // Update category tracking
            categoryTraitCounts.TryGetValue(trait.Category, out var catCount);
            categoryTraitCounts[trait.Category] = catCount + 1;

            categoryPointTotals.TryGetValue(trait.Category, out var catPoints);
            categoryPointTotals[trait.Category] = catPoints + trait.Cost;
        }

        return validTraits;
    }

    /// <summary>
    /// Validates that adding a trait wouldn't exceed category-specific limits.
    /// </summary>
    private bool ValidateCategoryLimits(
        TraitPrototype trait,
        Dictionary<ProtoId<TraitCategoryPrototype>, int> categoryTraitCounts,
        Dictionary<ProtoId<TraitCategoryPrototype>, int> categoryPointTotals,
        Dictionary<ProtoId<TraitCategoryPrototype>, int> categorySlotGrants,
        List<string> rejectionReasons)
    {
        if (!_prototype.TryIndex(trait.Category, out var category))
            return true; // Unknown category, allow it

        categoryTraitCounts.TryGetValue(trait.Category, out var currentCount);
        categoryPointTotals.TryGetValue(trait.Category, out var currentPoints);

        // Check category trait count limit. Triad: granter traits (GrantsCategorySlots) raise the effective cap.
        // HasTraitLimit (not MaxTraits.HasValue) so null OR <= 0 both read as unlimited.
        if (category.HasTraitLimit)
        {
            categorySlotGrants.TryGetValue(trait.Category, out var grantedSlots);
            var effectiveMaxTraits = category.MaxTraits!.Value + grantedSlots;
            if (currentCount >= effectiveMaxTraits)
            {
                rejectionReasons.Add(Loc.GetString("disabled-traits-reason-category-limit",
                    ("category", Loc.GetString(category.Name))));
                return false;
            }
        }

        // Check category points limit
        if (category.HasPointLimit && currentPoints + trait.Cost > category.MaxPoints!.Value)
        {
            rejectionReasons.Add(Loc.GetString("disabled-traits-reason-category-points",
                ("category", Loc.GetString(category.Name))));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks all conditions on a trait.
    /// </summary>
    private bool CheckConditions(TraitPrototype trait, TraitConditionContext ctx, List<string> rejectionReasons)
    {
        foreach (var condition in trait.Conditions)
        {
            bool passed;
            try
            {
                passed = condition.Evaluate(ctx);
            }
            catch (Exception e)
            {
                // Triad: a malformed condition must reject its trait, not crash OnPlayerSpawnComplete. Fail safe.
                Log.Error($"Error evaluating condition {condition.GetType().Name} for trait {trait.ID}: {e}");
                return false;
            }

            if (passed)
                continue;

            // Get human-readable reason from the condition
            var tooltip = condition.GetTooltip(ctx.Proto, Loc, 0);

            if (!string.IsNullOrEmpty(tooltip))
                rejectionReasons.Add(tooltip);

            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies a trait's effects to an entity.
    /// </summary>
    private void ApplyTrait(EntityUid player, TraitPrototype trait)
    {
        var transform = Transform(player);

        var effectCtx = new TraitEffectContext
        {
            Player = player,
            EntMan = EntityManager,
            Proto = _prototype,
            CompFactory = _factory,
            LogMan = _log,
            Transform = transform,
        };

        foreach (var effect in trait.Effects)
        {
            try
            {
                effect.Apply(effectCtx);
            }
            catch (Exception e)
            {
                Log.Error($"Error applying effect {effect.GetType().Name} for trait {trait.ID}: {e}");
            }
        }
    }
}
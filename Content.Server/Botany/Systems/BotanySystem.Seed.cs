using Content.Server.Botany.Components;
using Content.Server.Kitchen.Components;
using Content.Server.Popups;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Botany;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.Random;
using Content.Shared.Random.Helpers;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server._NF.Contraband.Systems; // Frontier
using Content.Server.Labels;

namespace Content.Server.Botany.Systems;

public sealed partial class BotanySystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IRobustRandom _robustRandom = default!;
    [Dependency] private AppearanceSystem _appearance = default!;
    [Dependency] private PopupSystem _popupSystem = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedSolutionContainerSystem _solutionContainerSystem = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private RandomHelperSystem _randomHelper = default!;
    [Dependency] private LabelSystem _labelSystem = default!; // triad: for copying labels from produce to seeds

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SeedComponent, ExaminedEvent>(OnExamined);
    }

    public bool TryGetSeed(SeedComponent comp, [NotNullWhen(true)] out SeedData? seed)
    {
        if (comp.Seed != null)
        {
            seed = comp.Seed;
            return true;
        }

        if (comp.SeedId != null
            && _prototypeManager.TryIndex(comp.SeedId, out SeedPrototype? protoSeed))
        {
            seed = protoSeed;
            return true;
        }

        seed = null;
        return false;
    }

    public bool TryGetSeed(ProduceComponent comp, [NotNullWhen(true)] out SeedData? seed)
    {
        if (comp.Seed != null)
        {
            seed = comp.Seed;
            return true;
        }

        if (comp.SeedId != null
            && _prototypeManager.TryIndex(comp.SeedId, out SeedPrototype? protoSeed))
        {
            seed = protoSeed;
            return true;
        }

        seed = null;
        return false;
    }

    private void OnExamined(EntityUid uid, SeedComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        if (!TryGetSeed(component, out var seed))
            return;

        using (args.PushGroup(nameof(SeedComponent), 1))
        {
            var name = Loc.GetString(seed.DisplayName);
            args.PushMarkup(Loc.GetString($"seed-component-description", ("seedName", name)));
            args.PushMarkup(Loc.GetString($"seed-component-plant-yield-text", ("seedYield", seed.Yield)));
            args.PushMarkup(Loc.GetString($"seed-component-plant-potency-text", ("seedPotency", seed.Potency)));
        }
    }

    #region SeedPrototype prototype stuff

    /// <summary>
    /// Spawns a new seed packet on the floor at a position, then tries to put it in the user's hands if possible.
    /// </summary>
    ///
    ///  triad: added label as a non-optional field for applying the new label to the seed packet
    public EntityUid SpawnSeedPacket(SeedData proto, EntityCoordinates coords, EntityUid user, string? label, float? healthOverride = null)
    {
        var seed = SpawnAtPosition(proto.PacketPrototype, coords); // Frontier: Spawn<SpawnAtPosition
        var seedComp = EnsureComp<SeedComponent>(seed);
        seedComp.Seed = proto;
        seedComp.HealthOverride = healthOverride;

        var name = Loc.GetString(proto.Name);
        var noun = Loc.GetString(proto.Noun);
        var val = Loc.GetString(proto.PacketName, ("seedName", name), ("seedNoun", noun)); // Frontier: "botany-seed-packet-name"<proto.PacketName
        _metaData.SetEntityName(seed, val);

        // try to automatically place in user's other hand
        _hands.TryPickupAnyHand(user, seed);

        _labelSystem.Label(seed, label); // triad: apply the label
        return seed;
    }

    // triad: saves callers from needing to depend directly on the label system
    public EntityUid SpawnSeedPacket(SeedData proto,
        EntityCoordinates coords,
        EntityUid user,
        EntityUid? toCopyLabelFrom,
        float? healthOverride = null)
    {
        return SpawnSeedPacket(proto, coords, user, _labelSystem.TryGetLabelString(toCopyLabelFrom), healthOverride);
    }

    public IEnumerable<EntityUid> AutoHarvest(SeedData proto, EntityCoordinates position, string? label, int yieldMod = 1)
    {
        if (position.IsValid(EntityManager) &&
            proto.ProductPrototypes.Count > 0)
            return GenerateProduct(proto, position, label, yieldMod);

        return Enumerable.Empty<EntityUid>();
    }

    public IEnumerable<EntityUid> Harvest(SeedData proto, EntityUid user, string? label, int yieldMod = 1)
    {
        if (proto.ProductPrototypes.Count == 0 || proto.Yield <= 0)
        {
            _popupSystem.PopupCursor(Loc.GetString("botany-harvest-fail-message"), user, PopupType.Medium);
            return Enumerable.Empty<EntityUid>();
        }

        var name = Loc.GetString(proto.DisplayName);
        _popupSystem.PopupCursor(Loc.GetString("botany-harvest-success-message", ("name", name)), user, PopupType.Medium);
        return GenerateProduct(proto, Transform(user).Coordinates, label, yieldMod);
    }

    public IEnumerable<EntityUid> GenerateProduct(SeedData proto, EntityCoordinates position, string? label, int yieldMod = 1)
    {
        var totalYield = 0;
        if (proto.Yield > -1)
        {
            if (yieldMod < 0)
                totalYield = proto.Yield;
            else
                totalYield = proto.Yield * yieldMod;

            totalYield = Math.Max(1, totalYield);
        }

        var products = new List<EntityUid>();

        if (totalYield > 1 || proto.HarvestRepeat != HarvestType.NoRepeat)
            proto.Unique = false;

        for (var i = 0; i < totalYield; i++)
        {
            var product = _robustRandom.Pick(proto.ProductPrototypes);

            var entity = SpawnAtPosition(product, position); // Frontier: Spawn<SpawnAtPosition
            _labelSystem.Label(entity, label);
            _randomHelper.RandomOffset(entity, 0.25f);
            products.Add(entity);

            var produce = EnsureComp<ProduceComponent>(entity);

            produce.Seed = proto;
            ProduceGrown(entity, produce);

            _appearance.SetData(entity, ProduceVisuals.Potency, proto.Potency);

            if (proto.Mysterious)
            {
                var metaData = MetaData(entity);
                _metaData.SetEntityName(entity, metaData.EntityName + "?", metaData);
                _metaData.SetEntityDescription(entity,
                    metaData.EntityDescription + " " + Loc.GetString("botany-mysterious-description-addon"), metaData);
            }
        }

        return products;
    }

    public bool CanHarvest(SeedData proto, EntityUid? held = null)
    {
        return !proto.Ligneous || proto.Ligneous && held != null && HasComp<SharpComponent>(held);
    }

    #endregion
}

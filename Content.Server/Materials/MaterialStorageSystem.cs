using System.Linq;
using Content.Server.Administration.Logs;
using Content.Shared.Materials;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Server.Storage.Components; // Frontier
using Content.Server.Cargo.Systems; // Frontier
using Content.Server.Power.Components;
using Content.Server.Stack;
using Content.Shared.ActionBlocker;
using Content.Shared.Construction;
using Content.Shared.Database;
using JetBrains.Annotations;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

// Mono
using Content.Shared.Destructible;
using Content.Shared.Damage; // Triad ~ fissile glass shard super massive event preventer

namespace Content.Server.Materials;

/// <summary>
/// This handles <see cref="SharedMaterialStorageSystem"/>
/// </summary>
public sealed partial class MaterialStorageSystem : SharedMaterialStorageSystem
{
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private StackSystem _stackSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MaterialStorageComponent, MachineDeconstructedEvent>(OnDeconstructed);
        SubscribeLocalEvent<MaterialStorageComponent, DestructionEventArgs>(OnDestroyed); // Mono
        SubscribeLocalEvent<MaterialStorageComponent, PriceCalculationEvent>(OnPriceCalculation); // Frontier

        SubscribeAllEvent<EjectMaterialMessage>(OnEjectMessage);
    }

    private void OnDeconstructed(EntityUid uid, MaterialStorageComponent component, MachineDeconstructedEvent args)
    {
        if (!component.DropOnDeconstruct)
            return;

        // Normal deconstruction: preserve the original drop behavior.
        DropAll((uid, component));
    }
    // Triad: switch to limited stack spawning when mass material drops would create excessive entities.
    private const int MaterialDropStackThreshold = 100;
    private const int MaxStacksPerMaterialOnHighVolumeDrop = 10;
    private const int MaterialLossDamageThreshold = 600;
    // Triad: fragile glasses are destroyed instead of dropped due to them not being handled correctly by the engine down stream during explosions making the server die
    private static readonly HashSet<ProtoId<MaterialPrototype>> MaterialsLostOnHighVolumeDrop = new()
    {
        "Glass",
        "PlasmaGlass",
        "ReinforcedPlasmaGlass",
        "UraniumGlass",
        "ReinforcedUraniumGlass",
        "ReinforcedGlass",
    };

    private void OnDestroyed(Entity<MaterialStorageComponent> ent, ref DestructionEventArgs args)
    {
        if (!ent.Comp.DropOnDestroy)
            return;
        // Triad: determine whether destruction should use reduced-spawn recovery rules.
        var highDamage =
      TryComp<DamageableComponent>(ent, out var damageable) &&
      damageable.TotalDamage > MaterialLossDamageThreshold;

        // Triad: estimate normal stack count before spawning to avoid mass entity creation.
        long totalNormalStacks = 0;

        foreach (var (material, amount) in ent.Comp.Storage)
        {
            if (!_prototypeManager.TryIndex<MaterialPrototype>(material, out var materialProto))
                continue;

            totalNormalStacks += GetNormalStackCount(amount, materialProto);
            // Triad: preserve vanilla destruction behavior when stack counts remain reasonable.
            if (totalNormalStacks > MaterialDropStackThreshold)
                break;
        }

        // Normal destruction = vanilla behavior.
        if (totalNormalStacks <= MaterialDropStackThreshold)
        {
            DropAll(ent);
            return;
        }

        var coordinates = Transform(ent).Coordinates;

        // High-volume destruction path.
        foreach (var (material, amount) in ent.Comp.Storage)
        {
            if (highDamage &&
                MaterialsLostOnHighVolumeDrop.Contains(material))
            {
                continue;
            }

            if (!_prototypeManager.TryIndex<MaterialPrototype>(material, out var materialProto))
                continue;

            SpawnMaterialInLimitedStacks(
                amount,
                materialProto,
                coordinates,
                MaxStacksPerMaterialOnHighVolumeDrop);
        }
    }
    private int GetNormalStackCount(int amount, MaterialPrototype materialProto)
    {
        if (amount <= 0 || materialProto.StackEntity == null)
            return 0;

        var entProto = _prototypeManager.Index<EntityPrototype>(materialProto.StackEntity);

        if (!entProto.TryComp<PhysicalCompositionComponent>(
                out var composition,
                EntityManager.ComponentFactory))
            return 0;

        if (!composition.MaterialComposition.TryGetValue(
                materialProto.ID,
                out var materialPerStack) || materialPerStack <= 0)
            return 0;

        var totalCount = amount / materialPerStack;
        if (totalCount <= 0)
            return 0;

        if (!entProto.TryComp<StackComponent>(
                out var prototypeStack,
                EntityManager.ComponentFactory))
            return 0;

        var normalMaxCount = _stackSystem.GetMaxCount(prototypeStack);
        if (normalMaxCount <= 0)
            return 0;

        // Ceiling division without adding to totalCount, avoiding overflow.
        return 1 + ((totalCount - 1) / normalMaxCount);
    }

    private void SpawnMaterialInLimitedStacks(
        int amount,
        MaterialPrototype materialProto,
        EntityCoordinates coordinates,
        int maxObjects)
    {
        if (amount <= 0 || maxObjects <= 0 || materialProto.StackEntity == null)
            return;

        var entProto = _prototypeManager.Index<EntityPrototype>(materialProto.StackEntity);

        if (!entProto.TryComp<PhysicalCompositionComponent>(
                out var composition,
                EntityManager.ComponentFactory))
            return;

        if (!composition.MaterialComposition.TryGetValue(
                materialProto.ID,
                out var materialPerStack) || materialPerStack <= 0)
            return;

        // Convert stored material units into sheets.
        var totalCount = amount / materialPerStack;
        if (totalCount <= 0)
            return;

        if (!entProto.TryComp<StackComponent>(
                out var prototypeStack,
                EntityManager.ComponentFactory))
            return;

        var normalMaxCount = _stackSystem.GetMaxCount(prototypeStack);
        if (normalMaxCount <= 0)
            return;

        var normalObjectCount = 1 + ((totalCount - 1) / normalMaxCount);

        // If normal-sized stacks fit within this material's limit,
        // retain the normal stack spawning behavior.
        if (normalObjectCount <= maxObjects)
        {
            _stackSystem.SpawnMultiple(
                materialProto.StackEntity,
                totalCount,
                coordinates);
            return;
        }

        // Otherwise distribute all sheets across the allowed oversized stacks.
        var countPerObject = totalCount / maxObjects;
        var extraCount = totalCount % maxObjects;

        for (var i = 0; i < maxObjects; i++)
        {
            var count = countPerObject + (i < extraCount ? 1 : 0);

            var stackEntity = Spawn(materialProto.StackEntity, coordinates);
            var stack = Comp<StackComponent>(stackEntity);

            stack.MaxCountOverride = count;
            _stackSystem.SetCount(stackEntity, count, stack);
        }
    }
    // end triad
    // Mono
    public void DropAll(Entity<MaterialStorageComponent> ent)
    {
        var coord = Transform(ent).Coordinates;

        foreach (var (material, amount) in ent.Comp.Storage)
            SpawnMultipleFromMaterial(amount, material, coord);
    }

    // Start Frontier: add value of contents to appraisal price
    private void OnPriceCalculation(EntityUid uid, MaterialStorageComponent component, ref PriceCalculationEvent ev)
    {
        foreach (var (materialProto, amount) in component.Storage)
        {
            if (!_prototypeManager.TryIndex<MaterialPrototype>(materialProto, out var material))
            {
                Log.Error("Failed to index material prototype " + materialProto);
                continue;
            }
            ev.Price += material.Price * amount;
        }
    }
    // End Frontier: add value of contents to appraisal price

    private void OnEjectMessage(EjectMaterialMessage msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } player)
            return;

        var uid = GetEntity(msg.Entity);

        if (!TryComp<MaterialStorageComponent>(uid, out var component))
            return;

        if (!Exists(uid))
            return;

        if (!_actionBlocker.CanInteract(player, uid))
            return;

        if (!component.CanEjectStoredMaterials || !_prototypeManager.TryIndex<MaterialPrototype>(msg.Material, out var material))
            return;

        var volume = 0;

        if (material.StackEntity != null)
        {
            if (!_prototypeManager.Index<EntityPrototype>(material.StackEntity).TryComp<PhysicalCompositionComponent>(out var composition, EntityManager.ComponentFactory))
                return;

            var volumePerSheet = composition.MaterialComposition.FirstOrDefault(kvp => kvp.Key == msg.Material).Value;
            var sheetsToExtract = Math.Min(msg.SheetsToExtract, _stackSystem.GetMaxCount(material.StackEntity));

            volume = sheetsToExtract * volumePerSheet;
        }

        if (volume <= 0 || !TryChangeMaterialAmount(uid, msg.Material, -volume))
            return;

        // Frontier
        // If we made it this far, turn off the magnet before spawning materials
        if (TryComp<MaterialStorageMagnetPickupComponent>(uid, out var magnet))
        {
            magnet.MagnetEnabled = false;
        }
        // end Frontier

        var mats = SpawnMultipleFromMaterial(volume, material, Transform(uid).Coordinates, out _);
        foreach (var mat in mats.Where(mat => !TerminatingOrDeleted(mat)))
        {
            _stackSystem.TryMergeToContacts(mat);
        }
    }

    public override bool TryInsertMaterialEntity(EntityUid user,
        EntityUid toInsert,
        EntityUid receiver,
        MaterialStorageComponent? storage = null,
        MaterialComponent? material = null,
        PhysicalCompositionComponent? composition = null)
    {
        if (!Resolve(receiver, ref storage) || !Resolve(toInsert, ref material, ref composition, false))
            return false;
        if (TryComp<ApcPowerReceiverComponent>(receiver, out var power) && !power.Powered)
            return false;
        if (!base.TryInsertMaterialEntity(user, toInsert, receiver, storage, material, composition))
            return false;
        _audio.PlayPvs(storage.InsertingSound, receiver);
        _popup.PopupEntity(Loc.GetString("machine-insert-item",
                ("user", user),
                ("machine", receiver),
                ("item", toInsert)),
            receiver);
        if (user != receiver) // Goobstation - for automation to not spam popups
            _popup.PopupEntity(Loc.GetString("machine-insert-item", ("user", user), ("machine", receiver),
                ("item", toInsert)), receiver);
        //QueueDel(toInsert); // Frontier

        // Logging
        TryComp<StackComponent>(toInsert, out var stack);
        var count = stack?.Count ?? 1;
        _adminLogger.Add(LogType.Action,
            LogImpact.Low,
            $"{ToPrettyString(user):player} inserted {count} {ToPrettyString(toInsert):inserted} into {ToPrettyString(receiver):receiver}");
        Del(toInsert); // Frontier: delete immediately, don't queue
        return true;
    }

    // Frontier: partial stack insertion
    public override bool TryInsertMaxPossibleMaterialEntity(EntityUid user,
        EntityUid toInsert,
        EntityUid receiver,
        out bool empty,
        MaterialStorageComponent? storage = null,
        MaterialComponent? material = null,
        PhysicalCompositionComponent? composition = null)
    {
        empty = false;
        if (!Resolve(receiver, ref storage) || !Resolve(toInsert, ref material, ref composition, false))
            return false;
        if (TryComp<ApcPowerReceiverComponent>(receiver, out var power) && !power.Powered)
            return false;
        // Cache old count
        var initialCount = TryComp<StackComponent>(toInsert, out var stack) ? stack.Count : 1;
        if (!base.TryInsertMaxPossibleMaterialEntity(user, toInsert, receiver, out empty, storage, material, composition))
            return false;
        _audio.PlayPvs(storage.InsertingSound, receiver);
        _popup.PopupEntity(Loc.GetString("machine-insert-item", ("user", user), ("machine", receiver),
            ("item", toInsert)), receiver);

        // Logging
        var newCount = stack?.Count ?? 0;
        _adminLogger.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(user):player} inserted {initialCount - newCount} item(s) from {ToPrettyString(toInsert):inserted} into {ToPrettyString(receiver):receiver}");
        if (empty)
            Del(toInsert);
        return true;
    }
    // End Frontier: partial stack insertion

    /// <summary>
    ///     Spawn an amount of a material in stack entities.
    ///     Note the 'amount' is material dependent.
    ///     1 biomass = 1 biomass in its stack,
    ///     but 100 plasma = 1 sheet of plasma, etc.
    /// </summary>
    public List<EntityUid> SpawnMultipleFromMaterial(int amount, string material, EntityCoordinates coordinates)
    {
        return SpawnMultipleFromMaterial(amount, material, coordinates, out _);
    }

    /// <summary>
    ///     Spawn an amount of a material in stack entities.
    ///     Note the 'amount' is material dependent.
    ///     1 biomass = 1 biomass in its stack,
    ///     but 100 plasma = 1 sheet of plasma, etc.
    /// </summary>
    public List<EntityUid> SpawnMultipleFromMaterial(int amount, string material, EntityCoordinates coordinates, out int overflowMaterial)
    {
        overflowMaterial = 0;
        if (!_prototypeManager.TryIndex<MaterialPrototype>(material, out var stackType))
        {
            Log.Error("Failed to index material prototype " + material);
            return new List<EntityUid>();
        }

        return SpawnMultipleFromMaterial(amount, stackType, coordinates, out overflowMaterial);
    }

    /// <summary>
    ///     Spawn an amount of a material in stack entities.
    ///     Note the 'amount' is material dependent.
    ///     1 biomass = 1 biomass in its stack,
    ///     but 100 plasma = 1 sheet of plasma, etc.
    /// </summary>
    [PublicAPI]
    public List<EntityUid> SpawnMultipleFromMaterial(int amount, MaterialPrototype materialProto, EntityCoordinates coordinates)
    {
        return SpawnMultipleFromMaterial(amount, materialProto, coordinates, out _);
    }

    /// <summary>
    ///     Spawn an amount of a material in stack entities.
    ///     Note the 'amount' is material dependent.
    ///     1 biomass = 1 biomass in its stack,
    ///     but 100 plasma = 1 sheet of plasma, etc.
    /// </summary>
    public List<EntityUid> SpawnMultipleFromMaterial(int amount, MaterialPrototype materialProto, EntityCoordinates coordinates, out int overflowMaterial)
    {
        overflowMaterial = 0;

        if (amount <= 0 || materialProto.StackEntity == null)
            return new List<EntityUid>();

        var entProto = _prototypeManager.Index<EntityPrototype>(materialProto.StackEntity);
        if (!entProto.TryComp<PhysicalCompositionComponent>(out var composition, EntityManager.ComponentFactory))
            return new List<EntityUid>();

        var materialPerStack = composition.MaterialComposition[materialProto.ID];
        var amountToSpawn = amount / materialPerStack;
        overflowMaterial = amount - amountToSpawn * materialPerStack;

        if (amountToSpawn == 0)
            return new List<EntityUid>();

        return _stackSystem.SpawnMultiple(materialProto.StackEntity, amountToSpawn, coordinates);
    }

    /// <summary>
    /// Eject a material out of this storage. The internal counts are updated.
    /// Material that cannot be ejected stays in storage. (e.g. only have 50 but a sheet needs 100).
    /// </summary>
    /// <param name="entity">The entity with storage to eject from.</param>
    /// <param name="material">The material prototype to eject.</param>
    /// <param name="maxAmount">The maximum amount to eject. If not given, as much as possible is ejected.</param>
    /// <param name="coordinates">The position where to spawn the created sheets. If not given, they're spawned next to the entity.</param>
    /// <param name="component">The storage component on <paramref name="entity"/>. Resolved automatically if not given.</param>
    /// <returns>The stack entities that were spawned.</returns>
    public List<EntityUid> EjectMaterial(
        EntityUid entity,
        string material,
        int? maxAmount = null,
        EntityCoordinates? coordinates = null,
        MaterialStorageComponent? component = null)
    {
        if (!Resolve(entity, ref component))
            return new List<EntityUid>();

        coordinates ??= Transform(entity).Coordinates;

        var amount = GetMaterialAmount(entity, material, component);
        if (maxAmount != null)
            amount = Math.Min(maxAmount.Value, amount);

        var spawned = SpawnMultipleFromMaterial(amount, material, coordinates.Value, out var overflow);

        TryChangeMaterialAmount(entity, material, -(amount - overflow), component);
        return spawned;
    }

    /// <summary>
    /// Eject all material stored in an entity, with the same mechanics as <see cref="EjectMaterial"/>.
    /// </summary>
    /// <param name="entity">The entity with storage to eject from.</param>
    /// <param name="coordinates">The position where to spawn the created sheets. If not given, they're spawned next to the entity.</param>
    /// <param name="component">The storage component on <paramref name="entity"/>. Resolved automatically if not given.</param>
    /// <returns>The stack entities that were spawned.</returns>
    public List<EntityUid> EjectAllMaterial(
        EntityUid entity,
        EntityCoordinates? coordinates = null,
        MaterialStorageComponent? component = null)
    {
        if (!Resolve(entity, ref component))
            return new List<EntityUid>();

        coordinates ??= Transform(entity).Coordinates;

        var allSpawned = new List<EntityUid>();
        foreach (var material in component.Storage.Keys.ToArray())
        {
            var spawned = EjectMaterial(entity, material, null, coordinates, component);
            allSpawned.AddRange(spawned);
        }

        return allSpawned;
    }
}

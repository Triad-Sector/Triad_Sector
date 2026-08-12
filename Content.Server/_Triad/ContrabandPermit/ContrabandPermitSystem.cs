using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Shared._Triad.ContrabandPermit;
using Content.Shared.CartridgeLoader;
using Content.Server.CartridgeLoader;
using Content.Shared.Database;
using Content.Shared.PDA;
using Content.Server._NF.SectorServices;
using Content.Shared._Triad.Humanoid;
using System.Runtime.InteropServices;
using Content.Server.Radio.EntitySystems;
using Robust.Shared.Map.Components;
using Content.Server.Mind;
using Robust.Server.GameStates;
using Content.Shared.Humanoid;
using Content.Server.Preferences.Managers;
using Robust.Shared.Player;
using Content.Shared.Preferences;
using Content.Shared.Inventory;
using Robust.Shared.Prototypes;

namespace Content.Server._Triad.ContrabandPermit;

public sealed partial class ContrabandPermitSystem : SharedContrabandPermitSystem
{
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IServerPreferencesManager _pref = default!;
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SectorServiceSystem _sectorService = default!;

    private readonly HashSet<Entity<ContrabandPermitItemComponent>> _newPermitItems = new();

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<TransformComponent> _transformQuery;

    public override void Initialize()
    {
        base.Initialize();

        _gridQuery = GetEntityQuery<MapGridComponent>();
        _transformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<ContrabandPermittableComponent, ContrabandPermitGrantedEvent>(OnPermitGranted);
        SubscribeLocalEvent<ContrabandPermittableComponent, ContrabandPermitRevokedEvent>(OnPermitRevoked);

        SubscribeLocalEvent<ContrabandPermitItemComponent, EntityTerminatingEvent>(OnPermitItemTerminating);
    }

    private void OnPermitGranted(Entity<ContrabandPermittableComponent> ent, ref ContrabandPermitGrantedEvent args)
    {
        var permitItem = args.PermitEntity;
        var permitOwner = args.PermitOwner;
        var permitGranter = args.PermitGranter;
        var permitReason = args.Reason;

        var message = $"{ToPrettyString(permitGranter):player} granted contraband permit to {ToPrettyString(permitOwner)} with reason {permitReason} " +
            $"for the item {ToPrettyString(permitItem)}";

        _chat.SendAdminAlert(message);
        _adminLog.Add(LogType.Action, LogImpact.High, $"{message}");

        var header = Loc.GetString("contraband-permit-console-pda-message-header");
        var pdaMsg = Loc.GetString("contraband-permit-console-pda-message-permit-granted", ("item", permitItem), ("reason", permitReason));
        SendPermitOwnerPdaMessage(permitOwner, header, pdaMsg);

        if (args.Console != null && permitGranter != null)
        {
            var consoleMessage = Loc.GetString("contraband-permit-console-radio-message-permit-granted",
                ("item", permitItem),
                ("user", permitGranter),
                ("owner", permitOwner),
                ("reason", permitReason));
            SendConsoleRadioMessage(args.Console.Value, consoleMessage);
        }

        if (!TryComp<ContrabandPermitItemComponent>(permitItem, out var permitItemComp))
            return;

        // Now, add the permit record to the sector service
        AddPermitRecordToSectorService(permitOwner, (permitItem, permitItemComp));
    }

    private void OnPermitRevoked(Entity<ContrabandPermittableComponent> ent, ref ContrabandPermitRevokedEvent args)
    {
        var permitItem = args.PermitEntity;
        var permitOwner = args.PermitOwner;
        var permitRevoker = args.PermitRevoker;
        var permitReason = args.Reason;

        var message = $"{ToPrettyString(permitRevoker):player} revoked contraband permit from {ToPrettyString(permitOwner)} with reason {permitReason} " +
            $"for the item {ToPrettyString(permitItem)}";

        _chat.SendAdminAlert(message);
        _adminLog.Add(LogType.Action, LogImpact.High, $"{message}");

        var header = Loc.GetString("contraband-permit-console-pda-message-header");
        var pdaMsg = Loc.GetString("contraband-permit-console-pda-message-permit-revoked", ("item", permitItem), ("reason", permitReason));
        SendPermitOwnerPdaMessage(permitOwner, header, pdaMsg);

        if (args.Console != null && permitRevoker != null)
        {
            var consoleMessage = Loc.GetString("contraband-permit-console-radio-message-permit-revoked",
                ("item", permitItem),
                ("user", permitRevoker),
                ("owner", permitOwner),
                ("reason", permitReason));
            SendConsoleRadioMessage(args.Console.Value, consoleMessage);
        }

        if (!TryComp<ContrabandPermitItemComponent>(permitItem, out var permitItemComp) || permitItemComp.PermitRecordKey == null)
            return;

        // Goodbye
        RemovePermitRecordToSectorService(permitItemComp.PermitRecordKey.Value, (permitItem, permitItemComp));
    }

    private void OnPermitItemTerminating(Entity<ContrabandPermitItemComponent> ent, ref EntityTerminatingEvent args)
    {
        if (ent.Comp.PermitRecordKey == null)
            return;

        // Delete permit records of entities about to be terminated
        RemovePermitRecordToSectorService(ent.Comp.PermitRecordKey.Value, ent);
    }

    public void AddPermitRecordToSectorService(EntityUid permitOwner, Entity<ContrabandPermitItemComponent> permitItem)
    {
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorContrabandPermitsComponent? contrabandPermitNet))
            return;

        // The 'permit owner'. This humanoid view of the owner so the info stays static, and covers in cases where the original permit owner's body gets destroyed
        var entryEntity = GetNetEntity(permitOwner);
        if (TryComp<HumanoidViewComponent>(permitOwner, out var humanoidView) && humanoidView.ViewEntity != null)
            entryEntity = GetNetEntity(humanoidView.ViewEntity.Value);

        // Initalize the key if it doesn't exist, get the list of permits that the owner has or add one if it doesn't exist, then add the new permit item under the record
        ref var permitList = ref CollectionsMarshal.GetValueRefOrAddDefault(contrabandPermitNet.Records, entryEntity, out var exists);

        if (!exists || permitList == null)
            permitList = new List<NetEntity>();

        permitList.Add(GetNetEntity(permitItem));
        permitItem.Comp.PermitRecordKey = entryEntity; // Store the key so we know what to remove if the permit is ever revoked

        UpdatePermitConsoles();
    }

    public void RemovePermitRecordToSectorService(NetEntity recordKey, Entity<ContrabandPermitItemComponent> permitItem)
    {
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorContrabandPermitsComponent? contrabandPermitNet))
            return;

        if (contrabandPermitNet.Records.ContainsKey(recordKey) && contrabandPermitNet.Records.TryGetValue(recordKey, out var list))
        {
            list.Remove(GetNetEntity(permitItem));

            if (list.Count == 0)
                contrabandPermitNet.Records.Remove(recordKey);
        }

        RemComp(permitItem.Owner, permitItem.Comp);
        UpdatePermitConsoles();
    }

    public void InitializePermitItemsOnGrid(EntityUid gridUid, EntityUid user)
    {
        if (!_gridQuery.HasComp(gridUid))
            return;

        _newPermitItems.Clear();

        var gridTransform = _transformQuery.GetComponent(gridUid);
        var worldAABB = _lookup.GetWorldAABB(gridUid, gridTransform);
        _lookup.GetEntitiesIntersecting(gridTransform.MapID, worldAABB, _newPermitItems);

        foreach ((var ent, var comp) in _newPermitItems)
        {
            if (ent == gridUid)
                continue;

            if (!_transformQuery.TryComp(ent, out var entXForm) || entXForm.GridUid != gridUid)
                continue;

            comp.PermitOwner = user;

            if (_mind.TryGetMind(user, out var mindId, out var mindComp))
            {
                comp.PermitOwnerMind = mindId;

                // Log if the names are different
                if (mindComp.CharacterName != comp.PermitOwnerName)
                {
                    var message = $"{ToPrettyString(user):player} owns a contraband permit with a different logged name." +
                        $" (Permit Owner Name: {comp.PermitOwnerName}, Player Name: {mindComp.CharacterName})"
                        + " Possible abuse of the ship saving system may be at play here.";

                    _chat.SendAdminAlert(message);
                    _adminLog.Add(LogType.EntitySpawn, LogImpact.Medium, $"{message}");
                }

                // Re-stamp so a transferred ship only alerts once, not on every future load
                comp.PermitOwnerName = mindComp.CharacterName ?? comp.PermitOwnerName;
            }

            Dirty(ent, comp);
            AddPermitRecordToSectorService(user, (ent, comp));
        }
    }

    public void ClearPermitItemsOnGrid(EntityUid gridUid, EntityUid user)
    {
        if (!_gridQuery.HasComp(gridUid))
            return;

        var toDelete = new HashSet<EntityUid>();

        _newPermitItems.Clear();

        var gridTransform = _transformQuery.GetComponent(gridUid);
        var worldAABB = _lookup.GetWorldAABB(gridUid, gridTransform);
        _lookup.GetEntitiesIntersecting(gridTransform.MapID, worldAABB, _newPermitItems);

        foreach ((var ent, var comp) in _newPermitItems)
        {
            if (ent == gridUid)
                continue;

            if (!_transformQuery.TryComp(ent, out var entXForm) || entXForm.GridUid != gridUid)
                continue;

            if (comp.PermitOwnerMind != null && _mind.TryGetMind(user, out var userMindId, out _))
            {
                if (userMindId != comp.PermitOwnerMind)
                {
                    toDelete.Add(ent);
                    continue;
                }
            }
            else if (user != comp.PermitOwner)
            {
                toDelete.Add(ent);
                continue;
            }

            // If the permit item somehow doesn't have permittable or it was set to false
            if (!TryComp<ContrabandPermittableComponent>(ent, out var permittable) || !permittable.Permittable)
            {
                toDelete.Add(ent);
                continue;
            }
        }

        foreach (var uid in toDelete)
        {
            Del(uid);
        }
    }

    private void SendConsoleRadioMessage(EntityUid console, string message)
    {
        if (!TryComp<ContrabandPermitConsoleComponent>(console, out var consoleComp))
            return;

        _radio.SendRadioMessage(console, message, consoleComp.RadioChannel, console);
    }

    private void SendPermitOwnerPdaMessage(EntityUid permitOwner, string header, string message)
    {
        var query = EntityQueryEnumerator<PdaComponent, CartridgeLoaderComponent>();
        while (query.MoveNext(out var uid, out var comp, out var cartridgeComp))
        {
            // Find the permit owner's PDA and send them a message
            if (comp.PdaOwner != permitOwner)
                continue;

            _cartridgeLoader.SendNotification(uid, header, message, cartridgeComp);
            break; // PDA found, break
        }
    }

    private void UpdatePermitConsoles()
    {
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorContrabandPermitsComponent? contrabandPermitNet))
            return;

        var query = EntityQueryEnumerator<ContrabandPermitConsoleComponent>();
        while (query.MoveNext(out var uid, out var console))
        {
            var permitEntries = GetAllPermitEntryData(contrabandPermitNet);
            var permitEntryArray = permitEntries.ToArray();

            console.Entries = permitEntryArray;
            Dirty(uid, console);

            UpdateUserInterface(uid, console);
        }
    }

    private List<ContrabandPermitConsoleEntry> GetAllPermitEntryData(SectorContrabandPermitsComponent contrabandPermitNet)
    {
        var permitRecords = contrabandPermitNet.Records;

        var data = new List<ContrabandPermitConsoleEntry>();

        foreach (var record in permitRecords)
        {
            var owner = record.Key;
            var items = record.Value;

            // Grab character appearance to send it to the client
            var ownerEnt = GetEntity(owner);

            ICommonSession? playerSession = null;
            if (TryComp<ActorComponent>(ownerEnt, out var actor))
                playerSession = actor.PlayerSession;
            else if (TryComp<HumanoidViewerEntityComponent>(ownerEnt, out var humanoidViewerEnt))
                playerSession = humanoidViewerEnt.Session;

            if (playerSession == null)
                continue;

            var preferences = _pref.GetPreferences(playerSession.UserId);

            if (preferences == null)
                continue;

            if (preferences.SelectedCharacter is not HumanoidCharacterProfile profile)
                continue;

            var appearance = (HumanoidCharacterAppearance) profile.CharacterAppearance;

            // This is for the dummy entity so they do not appear naked
            var itemsToEquip = new List<EntProtoId>();

            var invEnumerator = _inventory.GetSlotEnumerator(ownerEnt, SlotFlags.WITHOUT_POCKET);
            while (invEnumerator.MoveNext(out var slot))
            {
                if (slot.ContainedEntity is not { } item)
                    continue;

                var itemMeta = MetaData(item);
                if (itemMeta.EntityPrototype is not { } proto)
                    continue;

                itemsToEquip.Add(proto.ID);
            }

            var newOwner = new ContrabandPermitConsoleOwner(owner, profile.Name, appearance, profile.Age, profile.Gender, profile.Sex, profile.Species, itemsToEquip);

            // Now, get the list of permit items that the permit carrier owns
            var newConsoleItemList = new List<ContrabandPermitConsoleItem>();

            foreach (var netEnt in items)
            {
                var item = GetEntity(netEnt);

                if (!TryComp<ContrabandPermitItemComponent>(item, out var permitComp))
                    continue;

                var meta = MetaData(item);

                if (meta == null || meta.EntityPrototype == null)
                    continue;

                var consoleItem = new ContrabandPermitConsoleItem(netEnt, permitComp.PermitReason, permitComp.DateGranted, meta.EntityPrototype.ID);
                newConsoleItemList.Add(consoleItem);
            }

            if (newConsoleItemList.Count == 0)
                continue;

            data.Add(new ContrabandPermitConsoleEntry(newOwner, newConsoleItemList));
        }

        return data;
    }
}

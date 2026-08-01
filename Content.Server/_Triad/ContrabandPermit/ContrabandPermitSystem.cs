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

namespace Content.Server._Triad.ContrabandPermit;

public sealed partial class ContrabandPermitSystem : SharedContrabandPermitSystem
{
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private SectorServiceSystem _sectorService = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ContrabandPermittableComponent, ContrabandPermitGrantedEvent>(OnPermitGranted);
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

        // Now, add the permit record to the sector service
        AddPermitRecordToSectorService(permitOwner, permitItem);
    }

    public void AddPermitRecordToSectorService(EntityUid permitOwner, EntityUid permitItem)
    {
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorContrabandPermitsComponent? contrabandPermitNet))
            return;

        // The 'permit owner'. This is the Global PVS humanoid view of the owner so the picture works outside of PVS range, or the entity itself if it doesn't exist
        var entryEntity = GetNetEntity(permitOwner);
        if (TryComp<HumanoidViewComponent>(permitOwner, out var humanoidView) && humanoidView.PvsView != null)
            entryEntity = GetNetEntity(humanoidView.PvsView.Value);

        // Initalize the key if it doesn't exist, get the list of permits that the owner has or add one if it doesn't exist, then add the new permit item under the record
        ref var permitList = ref CollectionsMarshal.GetValueRefOrAddDefault(contrabandPermitNet.Records, entryEntity, out var exists);

        if (!exists || permitList == null)
            permitList = new List<NetEntity>();

        permitList.Add(GetNetEntity(permitItem));

        UpdatePermitConsoles();
    }

    public void RemovePermitRecordToSectorService(EntityUid permitOwner, EntityUid permitItem)
    {
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorContrabandPermitsComponent? contrabandPermitNet))
            return;

        var entryEntity = GetNetEntity(permitOwner);
        if (TryComp<HumanoidViewComponent>(permitOwner, out var humanoidView) && humanoidView.PvsView != null)
            entryEntity = GetNetEntity(humanoidView.PvsView.Value);

        if (contrabandPermitNet.Records.ContainsKey(entryEntity) && contrabandPermitNet.Records.TryGetValue(entryEntity, out var list))
            list.Remove(GetNetEntity(permitItem));

        UpdatePermitConsoles();
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

    private static List<ContrabandPermitConsoleEntry> GetAllPermitEntryData(SectorContrabandPermitsComponent contrabandPermitNet)
    {
        var permitRecords = contrabandPermitNet.Records;

        var data = new List<ContrabandPermitConsoleEntry>();

        foreach (var record in permitRecords)
        {
            var owner = record.Key;
            var items = record.Value;

            data.Add(new ContrabandPermitConsoleEntry(owner, items));
        }

        return data;
    }
}

using System.Linq;
using Content.Server._NF.Shipyard.Components;
using Content.Server._NF.Station.Components;
using Content.Server._Triad.ContrabandPermit;
using Content.Server._Triad.Shipyard;
using Content.Server.Database;
using Content.Server.Maps;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.StationEvents.Components;
using Content.Server.StationRecords;
using Content.Shared._Mono.Company;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._NF.ShuttleRecords;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared.Access.Components;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Forensics.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Content.Shared.Radio;
using Content.Shared.Shuttles.Components;
using Content.Shared.Station.Components;
using Content.Shared.StationRecords;
using Content.Shared.Whitelist;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Content.Server._Triad.Market;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    [Dependency] private ContrabandPermitSystem _contrabandPermit = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private TriadTamperPolicyService _tamperPolicy = default!;

    private static readonly ProtoId<VesselPrototype> DefaultVesselFallbackId = "Framework";

    private void AddCompanyInformation(EntityUid idCard, EntityUid shuttleUid)
    {
        // Add company information to the shuttle from the ID card or voucher
        string? companyName = null;

        // First try to get company from ID card
        if (TryComp<IdCardComponent>(idCard, out var idCardCompany) &&
            !string.IsNullOrEmpty(idCardCompany.CompanyName))
        {
            companyName = idCardCompany.CompanyName;
        }
        // If no ID card company, try to get from voucher
        else if (TryComp<ShipyardVoucherComponent>(idCard, out var voucherCompany) &&
                !string.IsNullOrEmpty(voucherCompany.CompanyName))
        {
            companyName = voucherCompany.CompanyName;
        }

        // Apply company to ship if we found one
        if (!string.IsNullOrEmpty(companyName))
        {
            var shipCompany = EnsureComp<CompanyComponent>(shuttleUid);
            shipCompany.CompanyName = companyName;
            Dirty(shuttleUid, shipCompany);
        }
    }

    /// <summary>
    /// Adds the <see cref="FTLLockComponent"/> to a shuttle grid and sets it to enabled
    /// </summary>
    private void SetFtlLockEnabled(EntityUid shuttleUid)
    {
        // Add FTLLockComponent to the shuttle with Enabled set to true
        // We need to use the ShuttleConsoleSystem to properly set the Enabled property
        EnsureComp<FTLLockComponent>(shuttleUid);

        var dockedEntities = new List<NetEntity>();
        _shuttleConsole.ToggleFTLLock(shuttleUid, dockedEntities, true);
    }

    /// <summary>
    /// Adds new access levels to a shuttle deed from a <see cref="ShipyardConsoleComponent"/>
    /// </summary>
    private void AddNewShuttleDeedAccessLevels(EntityUid targetId, ShipyardConsoleComponent console)
    {
        if (!TryComp<AccessComponent>(targetId, out var newCap))
            return;

        var newAccess = newCap.Tags.ToList();
        newAccess.AddRange(console.NewAccessLevels);
        _accessSystem.TrySetTags(targetId, newAccess, newCap);
    }

    /// <summary>
    /// Checks if a player is valid for saving a ship based on the entity whitelist and blacklist of the shipyard console.
    /// </summary>
    private bool IsShipSaveWhitelistValid(EntityUid user, ShipyardConsoleComponent console)
    {
        return _whitelist.CheckBoth(user, console.ShipSaveBlacklist, console.ShipSaveWhitelist);
    }
}

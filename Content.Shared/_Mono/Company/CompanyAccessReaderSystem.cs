using Content.Shared.Administration.Managers; // Triad
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Content.Shared.Whitelist;

namespace Content.Shared._Mono.Company;

/// <summary>
/// This system handles checking if a user belongs to the required company
/// before granting access to an entity.
/// </summary>
public sealed partial class CompanyAccessReaderSystem : EntitySystem
{
    [Dependency] private ISharedAdminManager _admin = default!; // Triad
    [Dependency] private EntityWhitelistSystem _whitelist = default!; // Triad
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CompanyAccessReaderComponent, ActivatableUIOpenAttemptEvent>(OnUIOpenAttempt);
    }

    private void OnUIOpenAttempt(Entity<CompanyAccessReaderComponent> entity, ref ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        var user = args.User;

        // Triad start
        if (_admin.IsAdmin(user))
            return;

        if (entity.Comp.IgnoreWhitelist is { } whitelist && _whitelist.IsValid(whitelist, user))
            return;
        // Triad end

        // Get user's company
        if (!TryComp<CompanyComponent>(user, out var userCompany))
        {
            args.Cancel();
            if (entity.Comp.PopupMessage != null)
                _popup.PopupClient(Loc.GetString(entity.Comp.PopupMessage), entity, user);
            return;
        }

        // Check if user's company matches the required company
        if (userCompany.CompanyName != entity.Comp.RequiredCompany)
        {
            args.Cancel();
            if (entity.Comp.PopupMessage != null)
                _popup.PopupClient(Loc.GetString(entity.Comp.PopupMessage), entity, user);
        }
    }
}

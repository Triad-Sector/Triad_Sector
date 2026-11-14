// Triad: ghosts and admin ghosts do not use shipyard consoles. The engine lets any ghost open a UI
// and lets an admin ghost (Ghost.canInteract, IgnoreUIRange) press every button from anywhere on the
// map, and the console binds a press to the session's account, so an aghost bought, stored and
// retrieved ships as the admin's own player: charged to their selected character, with the login
// name written as captain and deed owner. Admins act on ships through drydockadmin instead.

using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Ghost;
using Content.Shared.UserInterface;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    /// <summary>A ghost of any kind is refused the console before it opens, and told why.</summary>
    private void OnConsoleOpenAttempt(EntityUid uid, ShipyardConsoleComponent component, ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled || !HasComp<GhostComponent>(args.User))
            return;

        args.Cancel();
        DenyWithReason(args.User, uid, component, Loc.GetString("shipyard-console-ghost-refused"));
    }

    /// <summary>
    /// The server half: any message a ghost sends a shipyard console is dropped, whatever the client
    /// shows. Closing is let through, so a window that was somehow open can still be shut.
    /// </summary>
    private void OnConsoleMessageAttempt(BoundUserInterfaceMessageAttempt args)
    {
        if (args.Message is CloseBoundInterfaceMessage
            || !HasComp<ShipyardConsoleComponent>(args.Target)
            || !HasComp<GhostComponent>(args.Actor))
        {
            return;
        }

        args.Cancel();
    }
}

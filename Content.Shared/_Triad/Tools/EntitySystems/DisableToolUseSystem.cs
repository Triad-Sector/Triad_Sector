using Content.Shared._Triad.Tools.Components;
using Content.Shared.Popups;
using Content.Shared.Tools.Components;

namespace Content.Shared._Triad.Tools.EntitySystems;

public sealed partial class DisableToolUseSystem : EntitySystem
{
    [Dependency] private SharedPopupSystem _popup = default!;

    [SubscribeLocalEvent]
    private void OnToolUseAttempt(Entity<DisableToolUseComponent> ent, ref ToolUseAttemptEvent args)
    {
        if (ent.Comp.DisableAll)
        {
            args.Cancel();
            return;
        }

        // Check each tool quality being cancelled.
        var cancelled = false;
        foreach (var quality in args.Qualities)
        {
            if (ent.Comp.Disabled.Contains(quality))
            {
                args.Cancel();
                cancelled = true;
            }
        }

        if (cancelled && ent.Comp.Popup is { } locId)
        {
            var popupText = Loc.GetString(locId, ("tool", args.Tool), ("target", ent.Owner));
            _popup.PopupClient(popupText, args.User, args.User, PopupType.SmallCaution);
        }
    }
}

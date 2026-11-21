using Content.Client._Triad.ShipPreview.UI;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Client._Triad.ShipPreview;

public sealed partial class ShipPreviewCommand : LocalizedCommands
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override string Command => "triad_shippreview";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number-need-specific",
                ("properAmount", 1),
                ("currentAmount", args.Length)));
            return;
        }

        var vesselId = args[0];

        if (!_prototypes.TryIndex<VesselPrototype>(vesselId, out var vessel))
        {
            shell.WriteError(Loc.GetString("cmd-triad_shippreview-unknown", ("id", vesselId)));
            return;
        }

        ShipPreviewWindow.OpenFor(vessel);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        return CompletionResult.FromHintOptions(
            CompletionHelper.PrototypeIDs<VesselPrototype>(proto: _prototypes),
            Loc.GetString("cmd-triad_shippreview-hint-vessel"));
    }
}

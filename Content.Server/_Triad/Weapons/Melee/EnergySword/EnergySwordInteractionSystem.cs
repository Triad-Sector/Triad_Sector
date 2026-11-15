using Content.Server.Administration;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Tools.Systems;
using Content.Shared.Weapons.Melee.EnergySword;
using Robust.Shared.Player;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Content.Server._Triad.Weapons.Melee.EnergySword;

public sealed partial class EnergySwordInteractionSystem : EntitySystem
{
    [Dependency] private QuickDialogSystem _quickDialog = default!;
    [Dependency] private EnergySwordSystem _energySwordSystem = default!;
    [Dependency] private SharedToolSystem _toolSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<EnergySwordComponent, InteractUsingEvent>(OnInteractUsing);
    }

    private void OnInteractUsing(Entity<EnergySwordComponent> entity, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (!entity.Comp.BlockHacking && _toolSystem.HasQuality(args.Used, "Screwing"))
        {
            ChangeEswordColor(entity, ref args);
            return;
        }

        if (entity.Comp.BlockHacking || !_toolSystem.HasQuality(args.Used, SharedToolSystem.PulseQuality)) // Frontier: add entity.Comp.BlocksHacking
            return;

        args.Handled = true;
        _energySwordSystem.ToggleHacked(entity);
    }

    private void ChangeEswordColor(Entity<EnergySwordComponent> entity, ref InteractUsingEvent args)
    {
        if (!TryComp<ActorComponent>(args.User, out var actor))
            return;

        args.Handled = true;
        _quickDialog.OpenDialog(actor.PlayerSession,
            Loc.GetString("energy-sword-color-dialog-title"),
            Loc.GetString("energy-sword-color-dialog-prompt"),
            (string colorInput) =>
            {
               colorInput = Regex.Replace(colorInput, "[^a-zA-Z0-9#]", String.Empty);
                if (!Color.TryParse(colorInput.Trim(), out var color))
                    return;
                color.A = 1;
                _energySwordSystem.SetActivatedColor(entity.Owner, color);
            });
    }
}
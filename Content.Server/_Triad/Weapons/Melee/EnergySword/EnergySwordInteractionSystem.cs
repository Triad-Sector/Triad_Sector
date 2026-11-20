using Content.Server.Administration;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Tools.Systems;
using Content.Shared.Weapons.Melee.EnergySword;
using Robust.Shared.Player;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Content.Server._Triad.Weapons.Melee.EnergySword;

namespace Content.Server._Triad.Weapons.Melee.EnergySword;

public sealed partial class EnergySwordInteractionSystem : EntitySystem
{
    [Dependency] private EnergySwordSystem _energySwordSystem = default!;
    [Dependency] private SharedToolSystem _toolSystem = default!;
    [Dependency] private EnergySwordColorPicker _colorPicker = default!;

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
            _colorPicker.ChangeEswordColor(entity, ref args);
            return;
        }

        if (entity.Comp.BlockHacking || !_toolSystem.HasQuality(args.Used, SharedToolSystem.PulseQuality)) // Frontier: add entity.Comp.BlocksHacking
            return;

        args.Handled = true;
        _energySwordSystem.ToggleHacked(entity);
    }

}
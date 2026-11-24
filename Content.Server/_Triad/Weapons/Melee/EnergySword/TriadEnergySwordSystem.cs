using Content.Shared.Weapons.Melee.EnergySword;
using Robust.Shared.Player;
using Content.Server.Administration;
using System.Text.RegularExpressions;
using Content.Shared._Triad.Weapons.Melee.EnergySword;
using Robust.Shared.Prototypes;
using Robust.Shared.Audio;
using Robust.Server.Audio;
using Content.Shared.Coordinates;
using Content.Shared.Popups;

namespace Content.Server._Triad.Weapons.Melee.EnergySword;

public sealed partial class TriadEnergySwordSystem : SharedTriadEnergySwordSystem
{
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private QuickDialogSystem _quickDialog = default!;
    [Dependency] private EnergySwordSystem _energySwordSystem = default!;

    private static readonly Regex ValidColorRegex = new Regex("[^a-zA-Z0-9#]");
    private static readonly EntProtoId ColorPickerEffect = "EffectSparks";
    private static readonly SoundSpecifier ColorPickerSound = new SoundCollectionSpecifier("sparks")
    {
        Params = AudioParams.Default.WithVariation(0.125f),
    };

    public override bool TryOpenColorPicker(Entity<EnergySwordComponent> ent, EntityUid user)
    {
        if (!TryComp<ActorComponent>(user, out var actor))
            return false;

        _quickDialog.OpenDialog(actor.PlayerSession,
            Loc.GetString("energy-sword-color-dialog-title"),
            Loc.GetString("energy-sword-color-dialog-prompt"),
            (string colorInput) =>
            {
                colorInput = ValidColorRegex.Replace(colorInput, string.Empty);

                if (!Color.TryParse(colorInput.Trim(), out var color))
                {
                    _popup.PopupEntity(Loc.GetString("energy-sword-color-invalid-color"), user, user, PopupType.SmallCaution);
                    return;
                }

                color.A = 1; // Set the alpha
                _energySwordSystem.SetActivatedColor(ent, color);

                SpawnEffects(ent.Owner);
            });

        return true;
    }

    private void SpawnEffects(EntityUid sword)
    {
        SpawnAtPosition(ColorPickerEffect, sword.ToCoordinates());
        _audio.PlayPvs(ColorPickerSound, sword);
    }
}

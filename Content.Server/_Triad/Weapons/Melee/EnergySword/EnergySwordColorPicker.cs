    using Content.Shared.Weapons.Melee.EnergySword;
    using Content.Shared.Interaction;
    using Robust.Shared.Player;
    using Content.Server.Administration;
    using System.Text.RegularExpressions;

    namespace Content.Server._Triad.Weapons.Melee.EnergySword;
    public sealed partial class EnergySwordColorPicker: EntitySystem
    {  
        [Dependency] private QuickDialogSystem _quickDialog = default!;
        [Dependency] private EnergySwordSystem _energySwordSystem = default!;
        public void ChangeEswordColor(Entity<EnergySwordComponent> entity, ref InteractUsingEvent args)
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
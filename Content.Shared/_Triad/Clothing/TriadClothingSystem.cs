using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

namespace Content.Shared._Triad.Clothing;

public sealed partial class TriadClothingSystem : EntitySystem
{
    [Dependency] private ClothingSystem _clothing = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    [SubscribeLocalEvent]
    private void AddAlternateClothingFoldVerb(Entity<AlternateClothingFoldableComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null)
            return;

        var user = args.User;
        foreach (var type in ent.Comp.Types)
        {
            AlternativeVerb verb = new()
            {
                Act = () => TryToggleAlternateClothingFold(ent, type, user),
                Text = Loc.GetString(type.Name),
                Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/fold.svg.192dpi.png")),
                Priority = type.Priority,
            };

            args.Verbs.Add(verb);
        }
    }

    public void TryToggleAlternateClothingFold(Entity<AlternateClothingFoldableComponent> ent, ClothingAlternateFoldableType type, EntityUid? user)
    {
        if (type.Prefix == ent.Comp.ActivatedPrefix) // already activated
        {
            SetAlternateClothingPrefix(ent, null);
        }
        else
        {
            if (type.BlacklistedPrefix == ent.Comp.ActivatedPrefix && ent.Comp.ActivatedPrefix != null)
            {
                if (type.BlacklistPopup != null && user != null)
                {
                    var msg = Loc.GetString(type.BlacklistPopup);
                    _popup.PopupClient(msg, user.Value, user.Value, PopupType.SmallCaution);
                }

                return;
            }

            SetAlternateClothingPrefix(ent, type.Prefix);
        }
    }

    public void SetAlternateClothingPrefix(Entity<AlternateClothingFoldableComponent> ent, string? prefix)
    {
        ent.Comp.ActivatedPrefix = prefix;
        Dirty(ent);

        _clothing.SetEquippedPrefix(ent.Owner, ent.Comp.ActivatedPrefix);
    }
}

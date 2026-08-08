using Content.Shared._DV.Abilities;
using Content.Shared.Popups;
using Robust.Client.GameObjects;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client._DV.Abilities;

public sealed partial class HideUnderTableAbilitySystem : SharedCrawlUnderObjectsSystem
{
    [Dependency] private AppearanceSystem _appearance = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CrawlUnderObjectsComponent, AppearanceChangeEvent>(OnAppearanceChange);
    }

    private void OnAppearanceChange(EntityUid uid,
        CrawlUnderObjectsComponent component,
        AppearanceChangeEvent args)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        _appearance.TryGetData(uid, SneakMode.Enabled, out bool enabled);
        if (enabled)
        {
            if (component.OriginalDrawDepth != null)
                return;

            component.OriginalDrawDepth = sprite.DrawDepth;
            _sprite.SetDrawDepth((uid, sprite), (int) DrawDepth.SmallMobs);
        }
        else
        {
            if (component.OriginalDrawDepth == null)
                return;

            _sprite.SetDrawDepth((uid, sprite), (int) component.OriginalDrawDepth);
            component.OriginalDrawDepth = null;
        }
    }
}

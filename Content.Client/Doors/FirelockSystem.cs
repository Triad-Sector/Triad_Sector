using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Client.GameObjects;

namespace Content.Client.Doors;

public sealed partial class FirelockSystem : SharedFirelockSystem
{
    [Dependency] private SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<FirelockComponent, AppearanceChangeEvent>(OnAppearanceChange);
    }

    private void OnAppearanceChange(EntityUid uid, FirelockComponent comp, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (!args.TryGetData<DoorState>(DoorVisuals.State, out var state))
            state = DoorState.Closed;

        var boltedVisible = args.TryGetData<bool>(DoorVisuals.BoltLights, out var lights) && lights;
        var unlitVisible =
            state == DoorState.Closing
            || state == DoorState.Opening
            || state == DoorState.Denying
            || args.TryGetData<bool>(DoorVisuals.ClosedLights, out var closedLights) && closedLights;

        _sprite.LayerSetVisible((uid, args.Sprite), DoorVisualLayers.BaseUnlit, unlitVisible && !boltedVisible);
        _sprite.LayerSetVisible((uid, args.Sprite), DoorVisualLayers.BaseBolted, boltedVisible);
    }
}

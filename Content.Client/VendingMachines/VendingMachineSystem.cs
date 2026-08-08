using Content.Shared.VendingMachines;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;

namespace Content.Client.VendingMachines;

public sealed partial class VendingMachineSystem : SharedVendingMachineSystem
{
    [Dependency] private AnimationPlayerSystem _animationPlayer = default!;
    [Dependency] private SharedAppearanceSystem _appearanceSystem = default!;
    [Dependency] private SharedUserInterfaceSystem _uiSystem = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<VendingMachineComponent, AppearanceChangeEvent>(OnAppearanceChange);
        SubscribeLocalEvent<VendingMachineComponent, AnimationCompletedEvent>(OnAnimationCompleted);
        SubscribeLocalEvent<VendingMachineComponent, AfterAutoHandleStateEvent>(OnVendingAfterState);
        SubscribeLocalEvent<VendingMachineComponent, EntInsertedIntoContainerMessage>(OnEntityInserted); // Frontier
        SubscribeLocalEvent<VendingMachineComponent, EntRemovedFromContainerMessage>(OnEntityRemoved); // Frontier
    }

    private void OnVendingAfterState(EntityUid uid, VendingMachineComponent component, ref AfterAutoHandleStateEvent args)
    {
        if (_uiSystem.TryGetOpenUi<VendingMachineBoundUserInterface>(uid, VendingMachineUiKey.Key, out var bui))
        {
            bui.Refresh();
        }
    }

    // Frontier
    private void OnEntityInserted(Entity<VendingMachineComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (_uiSystem.TryGetOpenUi<VendingMachineBoundUserInterface>(ent.Owner, VendingMachineUiKey.Key, out var bui))
        {
            bui.Refresh();
        }
    }

    private void OnEntityRemoved(Entity<VendingMachineComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (_uiSystem.TryGetOpenUi<VendingMachineBoundUserInterface>(ent.Owner, VendingMachineUiKey.Key, out var bui))
        {
            bui.Refresh();
        }
    }
    // End Frontier

    private void OnAnimationCompleted(EntityUid uid, VendingMachineComponent component, AnimationCompletedEvent args)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        if (!TryComp<AppearanceComponent>(uid, out var appearance) ||
            !_appearanceSystem.TryGetData<VendingMachineVisualState>(uid, VendingMachineVisuals.VisualState, out var visualState, appearance))
        {
            visualState = VendingMachineVisualState.Normal;
        }

        UpdateAppearance(uid, visualState, component, sprite);
    }

    private void OnAppearanceChange(EntityUid uid, VendingMachineComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (!args.AppearanceData.TryGetValue(VendingMachineVisuals.VisualState, out var visualStateObject) ||
            visualStateObject is not VendingMachineVisualState visualState)
        {
            visualState = VendingMachineVisualState.Normal;
        }

        UpdateAppearance(uid, visualState, component, args.Sprite);
    }

    private void UpdateAppearance(EntityUid uid, VendingMachineVisualState visualState, VendingMachineComponent component, SpriteComponent sprite)
    {
        SetLayerState(uid, VendingMachineVisualLayers.Base, component.OffState, sprite);

        switch (visualState)
        {
            case VendingMachineVisualState.Normal:
                SetLayerState(uid, VendingMachineVisualLayers.BaseUnshaded, component.NormalState, sprite);
                SetLayerState(uid, VendingMachineVisualLayers.Screen, component.ScreenState, sprite);
                break;

            case VendingMachineVisualState.Deny:
                if (component.LoopDenyAnimation)
                    SetLayerState(uid, VendingMachineVisualLayers.BaseUnshaded, component.DenyState, sprite);
                else
                    PlayAnimation(uid, VendingMachineVisualLayers.BaseUnshaded, component.DenyState, component.DenyDelay, sprite);

                SetLayerState(uid, VendingMachineVisualLayers.Screen, component.ScreenState, sprite);
                break;

            case VendingMachineVisualState.Eject:
                PlayAnimation(uid, VendingMachineVisualLayers.BaseUnshaded, component.EjectState, component.EjectDelay, sprite);
                SetLayerState(uid, VendingMachineVisualLayers.Screen, component.ScreenState, sprite);
                break;

            case VendingMachineVisualState.Broken:
                HideLayers(uid, sprite);
                SetLayerState(uid, VendingMachineVisualLayers.Base, component.BrokenState, sprite);
                break;

            case VendingMachineVisualState.Off:
                HideLayers(uid, sprite);
                break;
        }
    }

    private void SetLayerState(EntityUid uid, VendingMachineVisualLayers layer, string? state, SpriteComponent sprite)
    {
        if (string.IsNullOrEmpty(state))
            return;

        _sprite.LayerSetVisible((uid, sprite), layer, true);
        _sprite.LayerSetAutoAnimated((uid, sprite), layer, true);
        _sprite.LayerSetRsiState((uid, sprite), layer, state);
    }

    private void PlayAnimation(EntityUid uid, VendingMachineVisualLayers layer, string? state, float animationTime, SpriteComponent sprite)
    {
        if (string.IsNullOrEmpty(state))
            return;

        if (!_animationPlayer.HasRunningAnimation(uid, state))
        {
            var animation = GetAnimation(layer, state, animationTime);
            _sprite.LayerSetVisible((uid, sprite), layer, true);
            _animationPlayer.Play(uid, animation, state);
        }
    }

    private static Animation GetAnimation(VendingMachineVisualLayers layer, string state, float animationTime)
    {
        return new Animation
        {
            Length = TimeSpan.FromSeconds(animationTime),
            AnimationTracks =
                {
                    new AnimationTrackSpriteFlick
                    {
                        LayerKey = layer,
                        KeyFrames =
                        {
                            new AnimationTrackSpriteFlick.KeyFrame(state, 0f)
                        }
                    }
                }
        };
    }

    private void HideLayers(EntityUid uid, SpriteComponent sprite)
    {
        HideLayer(uid, VendingMachineVisualLayers.BaseUnshaded, sprite);
        HideLayer(uid, VendingMachineVisualLayers.Screen, sprite);
    }

    private void HideLayer(EntityUid uid, VendingMachineVisualLayers layer, SpriteComponent sprite)
    {
        if (!_sprite.LayerMapTryGet((uid, sprite), layer, out var actualLayer, false))
            return;

        _sprite.LayerSetVisible((uid, sprite), actualLayer, false);
    }
}

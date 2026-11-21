using System.Numerics;
using Content.Client.Viewport;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client._Triad.ShipPreview.UI;

public sealed partial class ShipPreviewView : Control
{
    [Dependency] private IEntityManager _entities = default!;

    private const float FitPadding = 1.1f;
    private const float ZoomPerWheelStep = 1.25f;
    private const float FurthestZoomOutFromFit = 2f;
    private const float ClosestZoomInFromFit = 1f / 16f;
    private const int FramesForSpritesToSettle = 2;

    private readonly ScalingViewport _camera;
    private readonly Label _status;
    private readonly FixedEye _eye = new()
    {
        DrawFov = false,
        DrawLight = false,
    };

    private ShipPreviewScene? _scene;
    private VesselPrototype? _waitingVessel;
    private VesselPrototype? _shownVessel;
    private bool _statusNeedsOneFrame;
    private bool _fitOnceSized;
    private int _settleFramesLeft;

    private float _fittedZoom = 1f;
    private float _zoom = 1f;
    private Vector2? _grabbedWorldPoint;

    public event Action? ShipChanged;

    public bool HasShip => _scene is { HasShip: true };
    public Vector2i ShipSizeInTiles => _scene?.ShipSizeInTiles ?? Vector2i.Zero;
    public int ShipTileCount => _scene?.ShipTileCount ?? 0;

    private ShipPreviewSceneSystem Scenes => _entities.System<ShipPreviewSceneSystem>();

    public ShipPreviewView()
    {
        IoCManager.InjectDependencies(this);

        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;

        _camera = new ScalingViewport
        {
            Eye = _eye,
            ViewportSize = Vector2i.One,
            StretchMode = ScalingViewportStretchMode.Nearest,
            HorizontalExpand = true,
            VerticalExpand = true,
            Visible = false,
        };

        _status = new Label
        {
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
            Align = Label.AlignMode.Center,
        };

        AddChild(_camera);
        AddChild(_status);

        ShowStatus("triad-ship-preview-no-vessel");
    }

    public void ShowVessel(VesselPrototype vessel)
    {
        if (_waitingVessel == vessel || (_waitingVessel == null && HasShip && _shownVessel == vessel))
            return;

        _waitingVessel = vessel;
        _statusNeedsOneFrame = true;
        _settleFramesLeft = 0;
        ShowStatus("triad-ship-preview-loading");
    }

    public void FitShipToView()
    {
        if (_scene is not { HasShip: true } scene)
            return;

        if (!HasUsableSize())
        {
            _fitOnceSized = true;
            return;
        }

        var paddedShipSize = Vector2.Max(scene.ShipBounds.Size, Vector2.One) * FitPadding;
        var zoomForWidth = paddedShipSize.X * EyeManager.PixelsPerMeter / PixelSize.X;
        var zoomForHeight = paddedShipSize.Y * EyeManager.PixelsPerMeter / PixelSize.Y;

        _fittedZoom = MathF.Max(zoomForWidth, zoomForHeight);
        _fitOnceSized = false;

        ApplyZoom(_fittedZoom);
        _eye.Position = new MapCoordinates(scene.ShipBounds.Center, scene.MapId);
    }

    public void CloseScene()
    {
        _waitingVessel = null;

        if (_scene != null)
            Scenes.CloseScene(_scene);

        _scene = null;
        ForgetShownShip("triad-ship-preview-no-vessel");
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        ClearShipDeletedElsewhere();
        MatchCameraToControlSize();
        RevealShipOnceSettled();
        LoadWaitingVessel();
    }

    private void RevealShipOnceSettled()
    {
        if (_settleFramesLeft == 0 || --_settleFramesLeft > 0 || _scene == null)
            return;

        _status.Visible = false;
        _camera.Visible = true;
    }

    private void ClearShipDeletedElsewhere()
    {
        if (_scene?.Ship is not { } ship || _entities.EntityExists(ship))
            return;

        Scenes.RemoveShip(_scene);
        ForgetShownShip("triad-ship-preview-no-vessel");
        ShipChanged?.Invoke();
    }

    private void MatchCameraToControlSize()
    {
        if (!HasUsableSize())
            return;

        if (_camera.ViewportSize != PixelSize)
            _camera.ViewportSize = PixelSize;

        if (_fitOnceSized)
            FitShipToView();
    }

    private void LoadWaitingVessel()
    {
        if (_waitingVessel is not { } vessel)
            return;

        if (_statusNeedsOneFrame)
        {
            _statusNeedsOneFrame = false;
            return;
        }

        _waitingVessel = null;
        _scene ??= Scenes.OpenScene();

        if (Scenes.TryShowShip(_scene, vessel.ShuttlePath))
            OnShipLoaded(vessel);
        else
            ForgetShownShip("triad-ship-preview-failed");

        ShipChanged?.Invoke();
    }

    private void OnShipLoaded(VesselPrototype vessel)
    {
        _shownVessel = vessel;
        _settleFramesLeft = FramesForSpritesToSettle;
        FitShipToView();
    }

    private void ForgetShownShip(string statusLocId)
    {
        _shownVessel = null;
        _grabbedWorldPoint = null;
        _settleFramesLeft = 0;
        ShowStatus(statusLocId);
    }

    private void ShowStatus(string statusLocId)
    {
        _status.Text = Loc.GetString(statusLocId);
        _status.Visible = true;
        _camera.Visible = false;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);

        if (!HasShip || !IsDragButton(args))
            return;

        _grabbedWorldPoint = _camera.ScreenToMap(args.PointerLocation.Position).Position;
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);

        if (!IsDragButton(args))
            return;

        _grabbedWorldPoint = null;
        args.Handle();
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);

        if (_grabbedWorldPoint is { } grabbedPoint)
            KeepWorldPointUnderCursor(grabbedPoint, args.GlobalPixelPosition.Position);
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);

        if (!HasShip || args.Delta.Y == 0f)
            return;

        args.Handle();

        var cursor = args.GlobalPixelPosition.Position;
        var pointUnderCursor = _camera.ScreenToMap(cursor).Position;

        ApplyZoom(_zoom * MathF.Pow(ZoomPerWheelStep, -args.Delta.Y));
        KeepWorldPointUnderCursor(pointUnderCursor, cursor);
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();

        _grabbedWorldPoint = null;
    }

    private void KeepWorldPointUnderCursor(Vector2 worldPoint, Vector2 cursor)
    {
        var drift = worldPoint - _camera.ScreenToMap(cursor).Position;
        _eye.Position = _eye.Position.Offset(drift);
    }

    private void ApplyZoom(float zoom)
    {
        _zoom = Math.Clamp(
            zoom,
            _fittedZoom * ClosestZoomInFromFit,
            _fittedZoom * FurthestZoomOutFromFit);

        _eye.Zoom = new Vector2(_zoom, _zoom);
    }

    private bool HasUsableSize()
    {
        return PixelSize.X > 0 && PixelSize.Y > 0;
    }

    private static bool IsDragButton(GUIBoundKeyEventArgs args)
    {
        return args.Function == EngineKeyFunctions.UIClick
               || args.Function == EngineKeyFunctions.UIRightClick;
    }
}

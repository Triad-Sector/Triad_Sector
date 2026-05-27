using System.Linq;
using System.Numerics;
using Content.Client.Shuttles.UI;
using Content.Shared._Mono.FireControl;
using Content.Shared._Mono.ShipGuns;
using Content.Shared.Physics;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Client._Mono.Radar;
using Content.Shared._Mono.Radar;
using Content.Shared._Crescent.ShipShields;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Client.ResourceManagement;
using Robust.Shared.Timing;

namespace Content.Client._Mono.FireControl.UI;

public sealed class FireControlNavControl : ShuttleNavControl
{
    private readonly SharedTransformSystem _transform;
    private readonly SharedPhysicsSystem _physics;
    private readonly RadarBlipsSystem _blips;

    private EntityUid? _activeConsole;
    private FireControllableEntry[]? _controllables;
    private HashSet<NetEntity> _selectedWeapons = new();

    private readonly Dictionary<NetEntity, Color> _blipColors = new();

    private float _lastCursorUpdateTime = 0f;
    private const float CursorUpdateInterval = 0.1f; // 10 updates per second

    // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
    private EntityUid? _selectedTargetGrid;
    private string? _selectedTargetName;
    private const float TargetSelectRadius = 30f;
    private static readonly Color TargetColor = Color.OrangeRed;
    private readonly Font _boldFont;
    // Triad: targeting lock code end

    public FireControlNavControl() : base(64f, 512f, 512f)
    {
        IoCManager.InjectDependencies(this);
        _blips = EntManager.System<RadarBlipsSystem>();
        _physics = EntManager.System<SharedPhysicsSystem>();
        _transform = EntManager.System<SharedTransformSystem>();
        // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
        _boldFont = new VectorFont(IoCManager.Resolve<IResourceCache>().GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 12);
        // Triad: targeting lock code end
    }

    // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        if (args.Function == EngineKeyFunctions.UseSecondary)
            return;
        base.KeyBindUp(args);
    }
    // Triad: targeting lock code end

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (_isMouseInside)
            TryUpdateCursorPosition(_lastMousePos);
    }

    // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        // Prevent right-click from starting a pan drag so the cursor stays as Crosshair
        if (args.Function == EngineKeyFunctions.UseSecondary)
        {
            args.Handle();
            return;
        }

        base.KeyBindDown(args);

        if (args.Function != EngineKeyFunctions.UIRightClick)
            return;

        args.Handle();

        var clickPos = args.RelativePosition;
        EntityUid? bestGrid = null;
        string? bestName = null;
        var bestDist = TargetSelectRadius;

        foreach (var entry in CachedGridEntries)
        {
            var checkPos = entry.IsOnScreen ? entry.RawUiPosition : entry.ClampedUiPosition;
            var dist = Vector2.Distance(clickPos, checkPos);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestGrid = entry.GridUid;
                bestName = entry.Label;
            }
        }

        _selectedTargetGrid = bestGrid;
        _selectedTargetName = bestName;
    }
    // Triad: targeting lock code end

    protected override void Draw(DrawingHandleScreen handle)
    {
        if (_coordinates == null || _rotation == null)
            return;

        var xformQuery = EntManager.GetEntityQuery<TransformComponent>();
        if (!xformQuery.TryGetComponent(_coordinates.Value.EntityId, out var xform)
            || xform.MapID == MapId.Nullspace)
        {
            return;
        }

        base.Draw(handle);

        var coordEntRot = _transform.GetWorldRotation(_coordinates.Value.EntityId);

        var worldRot = _rotation.Value;

        var mapPos = _transform.ToMapCoordinates(_coordinates.Value).Offset(_rotation.Value.RotateVec(Offset));
        var mapCoord = _transform.ToCoordinates(mapPos);
        var worldToShuttle = Matrix3Helpers.CreateTranslation(-mapCoord.Position) * Matrix3Helpers.CreateRotation(-worldRot);
        Matrix3x2.Invert(worldToShuttle, out var shuttleToWorld);
        var shuttleToView = Matrix3x2.CreateScale(new Vector2(MinimapScale, -MinimapScale)) * Matrix3x2.CreateTranslation(MidPointVector);
        var worldToView = worldToShuttle * shuttleToView;
        Matrix3x2.Invert(worldToView, out var viewToWorld);

        var blips = _blips.GetCurrentBlips();
        _blipColors.Clear();
        foreach (var blip in blips)
            _blipColors[blip.NetUid] = blip.Config.Color;

        if (_controllables != null)
        {
            foreach (var controllable in _controllables)
            {
                var coords = EntManager.GetCoordinates(controllable.Coordinates);
                var worldPos = _transform.ToMapCoordinates(coords).Position;

                if (_selectedWeapons.Contains(controllable.NetEntity))
                {
                    var cursorViewPos = InverseScalePosition(_lastMousePos);
                    cursorViewPos = ScalePosition(cursorViewPos);

                    var cursorWorldPos = Vector2.Transform(cursorViewPos, viewToWorld);

                    var direction = cursorWorldPos - worldPos;
                    var ray = new CollisionRay(worldPos, direction.Normalized(), (int)CollisionGroup.Impassable);

                    var results = _physics.IntersectRay(xform.MapID, ray, direction.Length(), ignoredEnt: _coordinates?.EntityId);

                    if (!results.Any() && _blipColors.TryGetValue(controllable.NetEntity, out var color))
                        handle.DrawLine(Vector2.Transform(worldPos, worldToView), cursorViewPos, color.WithAlpha(0.3f));
                }
            }
        }

        // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
        if (_selectedTargetGrid != null && _controllables != null)
            DrawInterceptIndicators(handle, worldToView, xform.MapID);

        DrawTargetIndicator(handle);
        // Triad: targeting lock code end
    }

    // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
    private void DrawTargetIndicator(DrawingHandleScreen handle)
    {
        if (_selectedTargetGrid == null)
            return;

        foreach (var entry in CachedGridEntries)
        {
            if (entry.GridUid != _selectedTargetGrid)
                continue;

            var color = TargetColor;
            var label = $"[ {_selectedTargetName ?? "Grid"} ]";

            if (entry.IsOnScreen)
            {
                if (!entry.BlipOnly)
                {
                    var aabb = entry.LocalAABB;
                    var gToV = entry.GridToView;

                    var bl = Vector2.Transform(new Vector2(aabb.Left, aabb.Bottom), gToV);
                    var br = Vector2.Transform(new Vector2(aabb.Right, aabb.Bottom), gToV);
                    var tr = Vector2.Transform(new Vector2(aabb.Right, aabb.Top), gToV);
                    var tl = Vector2.Transform(new Vector2(aabb.Left, aabb.Top), gToV);

                    var frameColor = color.WithAlpha(0.8f);
                    handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, new[] { bl, br, tr, tl, bl }, frameColor);
                    handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip,
                        new[] { bl + new Vector2(1f, -1f), br + new Vector2(-1f, -1f), tr + new Vector2(-1f, 1f), tl + new Vector2(1f, 1f), bl + new Vector2(1f, -1f) },
                        frameColor);

                    var topVirt = MathF.Min(MathF.Min(bl.Y, br.Y), MathF.Min(tr.Y, tl.Y)) / UIScale;
                    var centerXVirt = (bl.X + br.X + tr.X + tl.X) / 4f / UIScale;
                    var labelDim = handle.GetDimensions(_boldFont, label, 0.9f);
                    var textPos = new Vector2(centerXVirt - labelDim.X * 0.5f, topVirt - labelDim.Y - 4f);
                    handle.DrawString(_boldFont, textPos * UIScale, label, UIScale * 0.9f, color);
                }
                else
                {
                    const float crossRadius = 8f;
                    var c = entry.RawUiPosition * UIScale;
                    var crossColor = color.WithAlpha(0.8f);
                    handle.DrawLine(c - new Vector2(crossRadius, 0f), c + new Vector2(crossRadius, 0f), crossColor);
                    handle.DrawLine(c - new Vector2(crossRadius, 1f), c + new Vector2(crossRadius, 1f), crossColor);
                    handle.DrawLine(c - new Vector2(0f, crossRadius), c + new Vector2(0f, crossRadius), crossColor);
                    handle.DrawLine(c - new Vector2(1f, crossRadius), c + new Vector2(1f, crossRadius), crossColor);
                    handle.DrawCircle(c, crossRadius, color.WithAlpha(0.4f), false);
                    handle.DrawCircle(c, crossRadius + 1f, color.WithAlpha(0.4f), false);

                    var labelDim = handle.GetDimensions(_boldFont, label, 0.9f);
                    var textPos = entry.RawUiPosition + new Vector2(-labelDim.X * 0.5f, -crossRadius / UIScale - labelDim.Y - 2f);
                    handle.DrawString(_boldFont, textPos * UIScale, label, UIScale * 0.9f, color);
                }
            }
            else
            {
                var center = new Vector2(Width * 0.5f, Height * 0.5f);
                var dir = entry.RawUiPosition - center;
                if (dir.LengthSquared() < 0.001f)
                    dir = Vector2.UnitY;
                dir = Vector2.Normalize(dir);

                const float arrowSize = 10f;
                var perp = new Vector2(-dir.Y, dir.X);
                var tip = entry.ClampedUiPosition;
                var left = tip - dir * arrowSize + perp * arrowSize * 0.5f;
                var right = tip - dir * arrowSize - perp * arrowSize * 0.5f;

                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan,
                    new[] { tip * UIScale, left * UIScale, right * UIScale },
                    color.WithAlpha(0.9f));
                handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip,
                    new[] { tip * UIScale, left * UIScale, right * UIScale, tip * UIScale },
                    color.WithAlpha(0.9f));

                var labelDim = handle.GetDimensions(_boldFont, label, 0.9f);
                var labelVirt = tip + dir * (arrowSize + 4f) + new Vector2(-labelDim.X * 0.5f, -labelDim.Y * 0.5f);
                handle.DrawString(_boldFont, labelVirt * UIScale, label, UIScale * 0.9f, color);
            }

            break;
        }
    }

    private void DrawInterceptIndicators(DrawingHandleScreen handle, Matrix3x2 worldToView, MapId mapId)
    {
        if (_selectedTargetGrid == null || _controllables == null)
            return;

        if (!EntManager.TryGetComponent<TransformComponent>(_selectedTargetGrid.Value, out var targetXform)
            || targetXform.MapID != mapId)
            return;

        EntManager.TryGetComponent<PhysicsComponent>(_selectedTargetGrid.Value, out var targetPhys);
        var targetVel = _physics.GetMapLinearVelocity(_selectedTargetGrid.Value);
        var targetGridRot = _transform.GetWorldRotation(_selectedTargetGrid.Value);
        // Physics body center may differ from the transform origin; apply rotation to get world center.
        var targetWorldPos = _transform.GetWorldPosition(_selectedTargetGrid.Value)
            + targetGridRot.RotateVec(targetPhys?.LocalCenter ?? Vector2.Zero);

        // _coordinates.EntityId IS the shooter grid itself, so GetMapLinearVelocity walks its parents.
        var shooterVel = _physics.GetMapLinearVelocity(_coordinates!.Value.EntityId);

        const float indicatorSize = 8f;

        foreach (var entry in _controllables)
        {
            if (!_selectedWeapons.Contains(entry.NetEntity))
                continue;

            var coords = EntManager.GetCoordinates(entry.Coordinates);
            var weaponWorldPos = _transform.ToMapCoordinates(coords).Position;

            Vector2 interceptWorldPos;
            if (entry.ProjectileSpeed == null || entry.GunType == ShipGunType.Missile)
            {
                // Hitscan weapons aim directly at the target.
                // Missiles home autonomously, so the indicator tracks the target itself
                // rather than a ballistic lead point.
                interceptWorldPos = targetWorldPos;
            }
            else
            {
                if (!TryComputeIntercept(weaponWorldPos, targetWorldPos, targetVel, shooterVel, entry.ProjectileSpeed.Value, out interceptWorldPos))
                    continue;
            }

            var screenPos = Vector2.Transform(interceptWorldPos, worldToView);
            var size = indicatorSize * UIScale;

            switch (entry.GunType)
            {
                case ShipGunType.Missile:
                    DrawCircleIndicator(handle, screenPos, size, Color.Blue);
                    break;
                case ShipGunType.Energy:
                    DrawSquareIndicator(handle, screenPos, size, Color.Cyan);
                    break;
                default:
                    DrawDiamondIndicator(handle, screenPos, size, Color.Red);
                    break;
            }
        }
    }

    /// <summary>
    /// Solves for the world-space aim point that a projectile fired from <paramref name="shooterPos"/>
    /// must target so that it intercepts a moving target.
    /// </summary>
    /// <remarks>
    /// A projectile inherits the shooter's velocity, so its world velocity is
    ///   <c>shooterVel + vp * dir</c>
    /// where <c>vp</c> is the muzzle speed and <c>dir</c> is the unit aim direction.
    ///
    /// Setting the projectile's world position equal to the target's position at time <c>t</c>:
    ///   <c>shooterPos + (shooterVel + vp*dir)*t = targetPos + targetVel*t</c>
    ///
    /// Rearranging with <c>delta = targetPos - shooterPos</c> and <c>relVel = targetVel - shooterVel</c>:
    ///   <c>vp*dir*t = delta + relVel*t</c>
    ///
    /// Taking the squared magnitude (since |dir| = 1):
    ///   <c>vp²t² = |delta + relVel*t|²</c>
    ///   <c>(vp² - |relVel|²)t² - 2(delta·relVel)t - |delta|² = 0</c>
    ///
    /// Solving the quadratic for the smallest positive <c>t</c> gives the time of flight.
    /// The aim point is then <c>targetPos + relVel*t</c>  (NOT <c>targetPos + targetVel*t</c>),
    /// because we want where to point, not where the target will be:
    /// <list type="bullet">
    ///   <item>Stationary target, moving shooter → aim BEHIND target by <c>shooterVel*t</c></item>
    ///   <item>Moving target, stationary shooter → aim AHEAD of target by <c>targetVel*t</c></item>
    ///   <item>Both moving at equal velocity → aim at current target position</item>
    /// </list>
    /// </remarks>
    private static bool TryComputeIntercept(
        Vector2 shooterPos,
        Vector2 targetPos,
        Vector2 targetVel,
        Vector2 shooterVel,
        float projectileSpeed,
        out Vector2 interceptPos)
    {
        var delta = targetPos - shooterPos;
        var relVel = targetVel - shooterVel;

        // Quadratic coefficients: at² + bt + c = 0
        var a = projectileSpeed * projectileSpeed - relVel.LengthSquared();
        var b = -2f * Vector2.Dot(delta, relVel);
        var c = -delta.LengthSquared();

        float t;
        if (MathF.Abs(a) < 1e-4f)
        {
            // Linear case: projectile speed ≈ relative speed component along approach axis.
            if (MathF.Abs(b) < 1e-4f)
            {
                interceptPos = targetPos;
                return false;
            }
            t = -c / b;
            if (t <= 0f)
            {
                interceptPos = targetPos;
                return false;
            }
        }
        else
        {
            var discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
            {
                // Target is moving faster than the projectile can close; no solution.
                interceptPos = targetPos;
                return false;
            }
            var sqrtD = MathF.Sqrt(discriminant);
            var t1 = (-b - sqrtD) / (2f * a);
            var t2 = (-b + sqrtD) / (2f * a);

            if (t1 > 0f && t2 > 0f)
                t = MathF.Min(t1, t2);
            else if (t1 > 0f)
                t = t1;
            else if (t2 > 0f)
                t = t2;
            else
            {
                interceptPos = targetPos;
                return false;
            }
        }

        interceptPos = targetPos + relVel * t;
        return true;
    }

    private static void DrawDiamondIndicator(DrawingHandleScreen handle, Vector2 center, float size, Color color)
    {
        var top = center + new Vector2(0f, -size);
        var right = center + new Vector2(size, 0f);
        var bottom = center + new Vector2(0f, size);
        var left = center + new Vector2(-size, 0f);
        handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, new[] { top, right, bottom, left, top }, color);
        var s1 = size - 1f;
        handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip,
            new[] { center + new Vector2(0f, -s1), center + new Vector2(s1, 0f), center + new Vector2(0f, s1), center + new Vector2(-s1, 0f), center + new Vector2(0f, -s1) },
            color);
    }

    private static void DrawCircleIndicator(DrawingHandleScreen handle, Vector2 center, float size, Color color)
    {
        handle.DrawCircle(center, size, color, false);
        handle.DrawCircle(center, size + 1f, color, false);
    }

    private static void DrawSquareIndicator(DrawingHandleScreen handle, Vector2 center, float size, Color color)
    {
        var tl = center + new Vector2(-size, -size);
        var tr = center + new Vector2(size, -size);
        var br = center + new Vector2(size, size);
        var bl = center + new Vector2(-size, size);
        handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, new[] { tl, tr, br, bl, tl }, color);
        var s1 = size - 1f;
        handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip,
            new[] { center + new Vector2(-s1, -s1), center + new Vector2(s1, -s1), center + new Vector2(s1, s1), center + new Vector2(-s1, s1), center + new Vector2(-s1, -s1) },
            color);
    }

    public EntityUid? SelectedTargetGrid => _selectedTargetGrid;
    // Triad: targeting lock code end

    public void UpdateControllables(EntityUid console, FireControllableEntry[] controllables)
    {
        _activeConsole = console;
        _controllables = controllables;
    }

    public void UpdateSelectedWeapons(HashSet<NetEntity> selectedWeapons)
    {
        _selectedWeapons = selectedWeapons;
    }

    private void TryUpdateCursorPosition(Vector2 relativePosition)
    {
        var currentTime = IoCManager.Resolve<IGameTiming>().CurTime.TotalSeconds;
        if (currentTime - _lastCursorUpdateTime < CursorUpdateInterval)
            return;

        _lastCursorUpdateTime = (float)currentTime;

        var coords = GetMouseEntityCoordinates(relativePosition);
        OnRadarClick?.Invoke(coords);
    }

    /// <summary>
    /// Returns true if the mouse button is currently pressed down
    /// </summary>
    public bool IsMouseDown() => _isMouseDown;
}

using Content.Client.Overlays;
using Content.Shared._Triad.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.NightVision;
using Content.Shared.Overlays;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Player;

namespace Content.Client.NightVision;

/// <inheritdoc/>
public sealed partial class NightVisionSystem : SharedNightVisionSystem
{
    [Dependency] private IConfigurationManager _config = default!; // Triad
    [Dependency] private IOverlayManager _overlayMan = default!;
    [Dependency] private IPlayerManager _player = default!;

    private NightVisionOverlay _overlay = default!;

    private bool _customNightVisionColorEnabled; // Triad
    private string? _customNightVisionColor; // Triad

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new NightVisionOverlay();

        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<NightVisionComponent, AfterAutoHandleStateEvent>(OnHandleState);
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(OnRoundRestart);

        _config.OnValueChanged(TriadCCVars.UseNightVisionColor, OnCustomColorEnabledCvarUpdated, true);
        _config.OnValueChanged(TriadCCVars.NightVisionColor, OnCustomColorCvarUpdated, true);
    }

    private void OnPlayerAttached(LocalPlayerAttachedEvent args)
    {
        RefreshOverlay(args.Entity);
    }

    private void OnPlayerDetached(LocalPlayerDetachedEvent args)
    {
        Deactivate(_player.LocalEntity);
    }

    private void OnHandleState(Entity<NightVisionComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        RefreshOverlay(ent);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer != null)
            Deactivate(localPlayer.Value);
    }

    private void OnCustomColorEnabledCvarUpdated(bool value)
    {
        _customNightVisionColorEnabled = value;

        if (_player.LocalSession?.AttachedEntity == null)
            return;

        RefreshOverlay(_player.LocalSession.AttachedEntity.Value);
    }

    private void OnCustomColorCvarUpdated(string value)
    {
        _customNightVisionColor = value;

        if (_player.LocalSession?.AttachedEntity == null)
            return;

        RefreshOverlay(_player.LocalSession.AttachedEntity.Value);
    }

    /// <summary>
    /// Update the state of the overlay. Add/remove/modify based on <see cref="NightVisionComponent"/>s if any.
    /// </summary>
    /// <param name="entity">The entity to have an overlay added/removed from.</param>
    /// <param name="entities">A list of entities with a <see cref="NightVisionComponent"/>.</param>
    private void Update(EntityUid entity, List<Entity<NightVisionComponent>> entities)
    {
        if (entity != _player.LocalSession?.AttachedEntity)
            return;

        // Find the component with the lowest noise.
        NightVisionComponent? nvision = null;
        foreach (var ent in entities)
        {
            if (!ent.Comp.Enabled)
                continue;

            if (ent.Comp.RelayOverlay == (ent.Owner == entity))
                continue;

            nvision = ent.Comp;

            // Take the first priority component
            if (ent.Comp.Prioritized)
                break;
        }

        // There is no active night vision components, so we disable the overlay.
        if (nvision == null)
        {
            Deactivate(entity);
            return;
        }

        // Triad Start, custom nv colors
        var phosphor = nvision.PhosphorColor;

        if (_customNightVisionColorEnabled && _customNightVisionColor is { } customColor)
            phosphor = Color.FromHex(customColor);

        // Relay all the settings from the component.
        _overlay.SetParameters(
            nvision.LightingColor,
            phosphor,
            nvision.GoggleEffect,
            nvision.ViewCircleRadius,
            nvision.ViewCircleSpacing,
            nvision.ViewCircleCount,
            nvision.Amplification);
        // Triad end

        if (!_overlayMan.HasOverlay<NightVisionOverlay>())
            _overlayMan.AddOverlay(_overlay);
    }

    private void Deactivate(EntityUid? ent)
    {
        if (ent != _player.LocalSession?.AttachedEntity)
            return;

        _overlayMan.RemoveOverlay(_overlay);
    }

    protected override void RefreshOverlay(EntityUid target)
    {
        if (target != _player.LocalSession?.AttachedEntity)
            return;
        var ev = new RefreshNightVisionEvent();
        RaiseLocalEvent(target, ref ev);

        if (ev.Entities.Count > 0)
            Update(target, ev.Entities);
        else
            Deactivate(target);
    }
}

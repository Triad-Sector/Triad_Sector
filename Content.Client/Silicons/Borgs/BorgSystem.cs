using Content.Shared.Mobs;
using Content.Shared.Silicons.Borgs;
using Content.Shared.Silicons.Borgs.Components;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;

namespace Content.Client.Silicons.Borgs;

/// <inheritdoc/>
public sealed partial class BorgSystem : SharedBorgSystem
{
    [Dependency] private AppearanceSystem _appearance = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BorgChassisComponent, AppearanceChangeEvent>(OnBorgAppearanceChanged);
        SubscribeLocalEvent<MMIComponent, AppearanceChangeEvent>(OnMMIAppearanceChanged);
    }

    private void OnBorgAppearanceChanged(EntityUid uid, BorgChassisComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;
        UpdateBorgAppearance(uid, component, args.Component, args.Sprite);
    }

    protected override void OnInserted(EntityUid uid, BorgChassisComponent component, EntInsertedIntoContainerMessage args)
    {
        if (!component.Initialized)
            return;

        base.OnInserted(uid, component, args);
        UpdateBorgAppearance(uid, component);
    }

    protected override void OnRemoved(EntityUid uid, BorgChassisComponent component, EntRemovedFromContainerMessage args)
    {
        if (!component.Initialized)
            return;

        base.OnRemoved(uid, component, args);
        UpdateBorgAppearance(uid, component);
    }

    private void UpdateBorgAppearance(EntityUid uid,
        BorgChassisComponent? component = null,
        AppearanceComponent? appearance = null,
        SpriteComponent? sprite = null)
    {
        if (!Resolve(uid, ref component, ref appearance, ref sprite))
            return;

        if (_appearance.TryGetData<MobState>(uid, MobStateVisuals.State, out var state, appearance))
        {
            if (state != MobState.Alive)
            {
                _sprite.LayerSetVisible((uid, sprite), BorgVisualLayers.Light, false);
                return;
            }
        }

        if (!_appearance.TryGetData<bool>(uid, BorgVisuals.HasPlayer, out var hasPlayer, appearance))
            hasPlayer = false;

        _sprite.LayerSetVisible((uid, sprite), BorgVisualLayers.Light, component.BrainEntity != null || hasPlayer);
        _sprite.LayerSetRsiState((uid, sprite), BorgVisualLayers.Light, hasPlayer ? component.HasMindState : component.NoMindState);
    }

    private void OnMMIAppearanceChanged(Entity<MMIComponent> ent, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (!args.TryGetData(MMIVisuals.BrainPresent, out bool brain))
            brain = false;
        if (!args.TryGetData(MMIVisuals.HasMind, out bool hasMind))
            hasMind = false;

        var spriteEnt = (ent.Owner, args.Sprite);

        _sprite.LayerSetVisible(spriteEnt, MMIVisualLayers.Brain, brain);
        if (!brain)
        {
            _sprite.LayerSetRsiState(spriteEnt, MMIVisualLayers.Base, ent.Comp.NoBrainState);
        }
        else
        {
            var state = hasMind
                ? ent.Comp.HasMindState
                : ent.Comp.NoMindState;
            _sprite.LayerSetRsiState(spriteEnt, MMIVisualLayers.Base, state);
        }
    }

    /// <summary>
    /// Sets the sprite states used for the borg "is there a mind or not" indication.
    /// </summary>
    /// <param name="borg">The entity and component to modify.</param>
    /// <param name="hasMindState">The state to use if the borg has a mind.</param>
    /// <param name="noMindState">The state to use if the borg has no mind.</param>
    /// <seealso cref="BorgChassisComponent.HasMindState"/>
    /// <seealso cref="BorgChassisComponent.NoMindState"/>
    public void SetMindStates(Entity<BorgChassisComponent> borg, string hasMindState, string noMindState)
    {
        borg.Comp.HasMindState = hasMindState;
        borg.Comp.NoMindState = noMindState;
    }
}

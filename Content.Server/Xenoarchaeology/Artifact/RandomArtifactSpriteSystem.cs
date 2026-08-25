using Content.Shared.Item;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.XenoArtifacts;
using Robust.Server.GameObjects;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Xenoarchaeology.Artifact;

public sealed class RandomArtifactSpriteSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _time = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedItemSystem _item = default!;

    /// <summary>
    /// Triad: artifacts that started up this tick carrying no sprite index. Settled on the next tick,
    /// once their map's life stage can be read. See <see cref="Update"/>.
    /// </summary>
    private readonly List<EntityUid> _pendingRoll = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RandomArtifactSpriteComponent, MapInitEvent>(OnMapInit);
        // Triad: ComponentStartup runs for a file-restored artifact, MapInitEvent does not. It must
        // not WRITE anything though: it also runs for a bare prototype spawn on an uninitialised map,
        // and UninitializedSaveTest holds those to serialising exactly like their prototype. See
        // OnStartup.
        SubscribeLocalEvent<RandomArtifactSpriteComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, ArtifactUnlockingStartedEvent>(UnlockingStageStarted);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, ArtifactUnlockingFinishedEvent>(UnlockingStageFinished);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, XenoArtifactActivatedEvent>(ArtifactActivated);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        SettlePendingRolls();

        var query = EntityQueryEnumerator<RandomArtifactSpriteComponent, AppearanceComponent>();
        while (query.MoveNext(out var uid, out var component, out var appearance))
        {
            if (component.ActivationStart == null)
                continue;

            var timeDif = _time.CurTime - component.ActivationStart.Value;
            // Triad: TimeSpan.Seconds is the seconds COMPONENT of the span, not its length, so with
            // the default 0.4s activation this stayed 0 for a whole second and only cleared when it
            // ticked over to 1. The flash ran two and a half times as long as authored.
            if (timeDif.TotalSeconds >= component.ActivationTime)
            {
                _appearance.SetData(uid, SharedArtifactsVisuals.IsActivated, false, appearance);
                component.ActivationStart = null;
            }
        }
    }

    /// <summary>
    /// Triad: settles artifacts that started up with no index. The test is the MAP's life stage, not
    /// the entity's: an artifact on a map that has not initialised yet (a prototype spawn, the
    /// shipyard hold) still has its MapInit coming and must be left completely alone, because writing
    /// to it now would land in an uninitialised save. What is left is an artifact restored from a save
    /// written before spriteIndex existed, onto a map whose init already happened. That one is
    /// stranded and is the one this is for.
    /// </summary>
    private void SettlePendingRolls()
    {
        if (_pendingRoll.Count == 0)
            return;

        foreach (var uid in _pendingRoll)
        {
            if (!TryComp<RandomArtifactSpriteComponent>(uid, out var comp) || comp.SpriteIndex != null)
                continue;

            if (Transform(uid).MapUid is not { } mapUid
                || MetaData(mapUid).EntityLifeStage < EntityLifeStage.MapInitialized)
                continue;

            RollSprite((uid, comp));
        }

        _pendingRoll.Clear();
    }

    private void OnMapInit(EntityUid uid, RandomArtifactSpriteComponent component, MapInitEvent args)
    {
        RollSprite((uid, component));
    }

    /// <summary>
    /// Triad: appearance data does not serialise (AppearanceComponent.AppearanceData is internal with
    /// no DataField), so a restored artifact has to be told its sprite again on the way in, and
    /// MapInitEvent never arrives for one loaded from a file onto a running map.
    ///
    /// This only ever RE-APPLIES an index the component already carries. It must not roll one, and it
    /// must not write: ComponentStartup also runs for a plain prototype spawn on an uninitialised map,
    /// and UninitializedSaveTest holds those to serialising byte-for-byte like their prototype. Doing
    /// the roll here put spriteIndex and heldPrefix into every uninitialised save and failed CI, which
    /// is the same trap the self-activate action fell into in SharedXenoArtifactSystem.
    ///
    /// An artifact out of a save written before spriteIndex existed carries no index, so there is
    /// nothing to re-apply and nothing above will ever roll it. <see cref="Update"/> picks those up.
    /// </summary>
    private void OnStartup(Entity<RandomArtifactSpriteComponent> ent, ref ComponentStartup args)
    {
        if (ent.Comp.SpriteIndex is not { } index)
        {
            _pendingRoll.Add(ent.Owner);
            return;
        }

        ApplySprite(ent, index);
    }

    /// <summary>
    /// Triad: rolls an index if the component does not have one yet, then applies it.
    /// </summary>
    private void RollSprite(Entity<RandomArtifactSpriteComponent> ent)
    {
        var index = ent.Comp.SpriteIndex ??= _random.Next(ent.Comp.MinSprite, ent.Comp.MaxSprite + 1);
        ApplySprite(ent, index);
    }

    private void ApplySprite(Entity<RandomArtifactSpriteComponent> ent, int index)
    {
        _appearance.SetData(ent, SharedArtifactsVisuals.SpriteIndex, index);
        _item.SetHeldPrefix(ent, "ano" + index.ToString("D2")); //set item artifact inhands
    }

    private void UnlockingStageStarted(Entity<RandomArtifactSpriteComponent> ent, ref ArtifactUnlockingStartedEvent args)
    {
        _appearance.SetData(ent, SharedArtifactsVisuals.IsUnlocking, true);
    }

    private void UnlockingStageFinished(Entity<RandomArtifactSpriteComponent> ent, ref ArtifactUnlockingFinishedEvent args)
    {
        _appearance.SetData(ent, SharedArtifactsVisuals.IsUnlocking, false);
    }

    private void ArtifactActivated(Entity<RandomArtifactSpriteComponent> ent, ref XenoArtifactActivatedEvent args)
    {
        _appearance.SetData(ent, SharedArtifactsVisuals.IsActivated, true);
        ent.Comp.ActivationStart = _time.CurTime;
    }
}

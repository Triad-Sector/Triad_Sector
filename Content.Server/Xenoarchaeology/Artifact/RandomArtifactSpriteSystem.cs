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

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RandomArtifactSpriteComponent, MapInitEvent>(OnMapInit);
        // Triad: ComponentStartup runs for a file-restored artifact, MapInitEvent does not. See
        // EnsureSprite.
        SubscribeLocalEvent<RandomArtifactSpriteComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, ArtifactUnlockingStartedEvent>(UnlockingStageStarted);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, ArtifactUnlockingFinishedEvent>(UnlockingStageFinished);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, XenoArtifactActivatedEvent>(ArtifactActivated);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

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

    private void OnMapInit(EntityUid uid, RandomArtifactSpriteComponent component, MapInitEvent args)
    {
        EnsureSprite((uid, component));
    }

    private void OnStartup(Entity<RandomArtifactSpriteComponent> ent, ref ComponentStartup args)
    {
        EnsureSprite(ent);
    }

    /// <summary>
    /// Triad: rolls the sprite index once and pushes it into appearance. This has to run on startup
    /// rather than only on MapInit: appearance data does not serialise, and an artifact loaded from a
    /// ship file onto a running map never receives MapInitEvent, so the index was gone and the
    /// artifact rendered as its bare prototype sprite. The held prefix survived on its own because
    /// ItemComponent.HeldPrefix is a real DataField, which is why a restored artifact looked correct
    /// in hand and wrong on the floor.
    /// </summary>
    private void EnsureSprite(Entity<RandomArtifactSpriteComponent> ent)
    {
        ent.Comp.SpriteIndex ??= _random.Next(ent.Comp.MinSprite, ent.Comp.MaxSprite + 1);

        var index = ent.Comp.SpriteIndex.Value;
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

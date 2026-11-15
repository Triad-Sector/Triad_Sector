using Content.Shared._Triad.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Mobs;
using Content.Shared.Standing;
using Content.Shared.StatusEffect;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Shared.Animations;
using Robust.Shared.Prototypes;

namespace Content.Client._Triad.Atmos.EntitySystems;

public sealed partial class StopDropRollVisualsSystem : EntitySystem
{
    [Dependency] private AnimationPlayerSystem _animation = default!;

    private const string RollAnimKey = "StopDropRollAnimation";
    private static readonly ProtoId<StatusEffectPrototype> KnockDownedKey = "KnockedDown";

    private static readonly Animation RollAnimation = new Animation
    {
        Length = TimeSpan.FromSeconds(5),
        AnimationTracks =
            {
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(TransformComponent),
                    Property = nameof(TransformComponent.LocalRotation),
                    InterpolationMode = AnimationInterpolationMode.Linear,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(Angle.Zero, 0f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(90), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(180), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(270), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(350), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.Zero, 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(90), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(180), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(270), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(350), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.Zero, 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(90), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(180), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(270), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(350), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.Zero, 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(90), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(180), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(270), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(350), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.Zero, 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(90), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(180), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(270), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(350), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.Zero, 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(90), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(180), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(270), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.FromDegrees(350), 0.25f),
                        new AnimationTrackProperty.KeyFrame(Angle.Zero, 0.25f),
                    }
                }
            }
    };

    [SubscribeLocalEvent, SubscribeNetworkEvent]
    private void OnResist(StopDropRollVisualsNetworkEvent ev)
    {
        if (!TryGetEntity(ev.User, out var user) || !HasComp<FlammableComponent>(user))
            return;

        if (_animation.HasRunningAnimation(user.Value, RollAnimKey))
            return;

        _animation.Play(user.Value, RollAnimation, RollAnimKey);
    }

    [SubscribeLocalEvent]
    private void OnMobStateChanged(Entity<FlammableComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
            return;

        _animation.Stop(ent.Owner, RollAnimKey);
    }

    [SubscribeLocalEvent]
    private void OnStatusEffectEnded(Entity<FlammableComponent> ent, ref StatusEffectEndedEvent args)
    {
        if (args.Key != KnockDownedKey)
            return;

        _animation.Stop(ent.Owner, RollAnimKey);
    }

    [SubscribeLocalEvent]
    private void OnStood(Entity<FlammableComponent> ent, ref StoodEvent args)
    {
        _animation.Stop(ent.Owner, RollAnimKey);
    }
}

using Content.Shared.Interaction;

namespace Content.Shared.MouseRotator;

/// <summary>
/// This handles rotating an entity based on mouse location
/// </summary>
/// <see cref="MouseRotatorComponent"/>
public abstract partial class SharedMouseRotatorSystem : EntitySystem
{
    [Dependency] private RotateToFaceSystem _rotate = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeAllEvent<RequestMouseRotatorRotationEvent>(OnRequestRotation);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // TODO maybe `ActiveMouseRotatorComponent` to avoid querying over more entities than we need?
        // (if this is added to players)
        // (but arch makes these fast anyway, so)
        var query = EntityQueryEnumerator<MouseRotatorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var rotator, out var xform))
        {
            if (rotator.GoalRotation == null)
                continue;

            if (_rotate.TryRotateTo(
                    uid,
                    rotator.GoalRotation.Value,
                    frameTime,
                    rotator.AngleTolerance,
                    MathHelper.DegreesToRadians(rotator.RotationSpeed),
                    xform))
            {
                // Stop rotating if we finished
                rotator.GoalRotation = null;
                Dirty(uid, rotator);
            }
        }
    }

    private void OnRequestRotation(RequestMouseRotatorRotationEvent msg, EntitySessionEventArgs args)
    {
        // Triad: this used to be one guard logging one error for two very different conditions, and
        // in production it fired for eight distinct players. A rotation request already in flight
        // when the sender's entity changes, on death, ghosting, or being moved into another body,
        // arrives with nothing attached. That is an ordinary client/server race that the server is
        // right to drop, and it is not worth an error line per input frame.
        if (args.SenderSession.AttachedEntity is not { } ent)
        {
            Log.Debug($"Discarded a rotation request from {args.SenderSession.Name} ({args.SenderSession.UserId}) with no attached entity.");
            return;
        }

        // Attached to something that cannot rotate is a different claim: the client asked for a
        // capability its body does not have, which is worth keeping visible.
        if (!TryComp<MouseRotatorComponent>(ent, out var rotator))
        {
            Log.Warning($"User {args.SenderSession.Name} ({args.SenderSession.UserId}) tried setting local rotation on {ToPrettyString(ent)}, which has no {nameof(MouseRotatorComponent)}.");
            return;
        }

        rotator.GoalRotation = msg.Rotation;
        Dirty(ent, rotator);
    }
}

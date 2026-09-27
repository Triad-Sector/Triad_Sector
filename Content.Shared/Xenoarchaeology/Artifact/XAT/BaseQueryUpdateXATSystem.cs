using Content.Shared.Xenoarchaeology.Artifact.Components;

namespace Content.Shared.Xenoarchaeology.Artifact.XAT;

/// <summary>
/// Base type for xeno artifact trigger systems, that are relied on updating loop.
/// </summary>
/// <typeparam name="T">Type of XAT component that system will work with.</typeparam>
public abstract class BaseQueryUpdateXATSystem<T> : BaseXATSystem<T> where T : Component
{
    protected EntityQuery<XenoArtifactComponent> _xenoArtifactQuery;

    /// <inheritdoc />
    public override void Initialize()
    {
        base.Initialize();

        _xenoArtifactQuery = GetEntityQuery<XenoArtifactComponent>();
    }

    /// <inheritdoc />
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // TODO: add a way to defer triggering artifacts to the end of the Update loop

        var query = EntityQueryEnumerator<T, XenoArtifactNodeComponent>();
        while (query.MoveNext(out var uid, out var comp, out var node))
        {
            if (node.Attached == null)
                continue;

            // Triad: a node whose artifact does not resolve is skipped rather than thrown on every tick
            // var artifact = _xenoArtifactQuery.Get(GetEntity(node.Attached.Value));
            var artifactUid = GetEntity(node.Attached.Value);
            if (!_xenoArtifactQuery.TryComp(artifactUid, out var artifactComp))
                continue;

            Entity<XenoArtifactComponent> artifact = (artifactUid, artifactComp);
            // End Triad

            if (!CanTrigger(artifact, (uid, node)))
                continue;

            UpdateXAT(artifact, (uid, comp, node), frameTime);
        }
    }

    /// <summary>
    /// Handles update logic that is related to trigger component.
    /// </summary>
    protected abstract void UpdateXAT(
        Entity<XenoArtifactComponent> artifact,
        Entity<T, XenoArtifactNodeComponent> node,
        float frameTime
    );
}

using Content.Server.Kitchen.Components;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Artifact.XAT;
using Content.Shared.Xenoarchaeology.Artifact.XAT.Components;

namespace Content.Server.Xenoarchaeology.Artifact.XAT;

/// <summary>
/// System for checking if microwaved xenoartifact should be triggered.
/// Triad: server-side here because <see cref="BeingMicrowavedEvent"/> is a server event on this tree;
/// the artifact-to-node relay for it lives in <see cref="XenoArtifactSystem"/>.
/// </summary>
public sealed partial class XATMicrowaveSystem : BaseXATSystem<XATMicrowaveComponent>
{

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();
        XATSubscribeDirectEvent<BeingMicrowavedEvent>(OnMicrowaved);
    }

    private void OnMicrowaved(Entity<XenoArtifactComponent> artifact, Entity<XATMicrowaveComponent, XenoArtifactNodeComponent> node, ref BeingMicrowavedEvent args)
    {
        Trigger(artifact, node);
    }
}

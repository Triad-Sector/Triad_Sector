using Robust.Shared.Prototypes;

namespace Content.Server.Xenoarchaeology.Artifact.XAE.Components;

/// <summary>
///     When activated artifact will spawn a pair of portals. First - right in artifact, Second - at random point of station.
/// </summary>
[RegisterComponent, Access(typeof(XAEPortalSystem))]
public sealed partial class XAEPortalComponent : Component
{
    /// <summary>
    /// Entity that should be spawned as portal.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntProtoId PortalProto = "PortalArtifact";

    // Frontier: range limit
    /// <summary>
    /// Maximum range that the target entity should be from the portal, in meters.
    /// </summary>
    // Triad: 1000<100. Frontier added this as a fence against cross-map yanks, but on a sector
    // where every ship shares one map 1000m reaches most of the neighbourhood, so it fenced
    // nothing. The grid gate in XAEPortalSystem is the real limit now; this only keeps the pick
    // to somewhere on the hull you could plausibly have walked to.
    [DataField, AutoNetworkedField]
    public float MaxRange = 100f; // Frontier: 1000f
    // End Frontier
}

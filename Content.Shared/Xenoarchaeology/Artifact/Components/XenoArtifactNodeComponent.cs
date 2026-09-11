using Content.Shared.Destructible.Thresholds;
using Robust.Shared.GameStates;

namespace Content.Shared.Xenoarchaeology.Artifact.Components;

/// <summary>
/// Stores metadata about a particular artifact node
/// </summary>
[RegisterComponent, NetworkedComponent, Access(typeof(SharedXenoArtifactSystem)), AutoGenerateComponentState]
public sealed partial class XenoArtifactNodeComponent : Component
{
    /// <summary>
    /// Depth within the graph generation.
    /// Used for sorting.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int Depth;

    /// <summary>
    /// Denotes whether an artifact node has been activated at least once (through the required triggers).
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Locked = true;

    /// <summary>
    /// List of trigger descriptions that this node require for activation.
    /// </summary>
    [DataField, AutoNetworkedField]
    public LocId? TriggerTip;

    /// <summary>
    /// The entity whose graph this node is a part of.
    /// </summary>
    [DataField, AutoNetworkedField]
    public NetEntity? Attached;

    #region Durability
    /// <summary>
    /// Marker, is durability of node degraded or not.
    /// </summary>
    public bool Degraded => Durability <= 0;

    /// <summary>
    /// The amount of generic activations a node has left before becoming fully degraded and useless.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int Durability;

    /// <summary>
    /// The maximum amount of times a node can be generically activated before becoming useless
    /// </summary>
    [DataField, AutoNetworkedField]
    public int MaxDurability = 5;

    /// <summary>
    /// The variance from MaxDurability present when a node is created.
    /// </summary>
    [DataField]
    public MinMax MaxDurabilityCanDecreaseBy = new(0, 2);
    #endregion

    #region Research
    /// <summary>
    /// The amount of points a node is worth with no scaling
    /// </summary>
    [DataField, AutoNetworkedField]
    public float BasePointValue = 1000; // Frontier: 4000<1000

    /// <summary>
    /// Amount of points available currently for extracting.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int ResearchValue;

    /// <summary>
    /// Amount of points already extracted from node.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int ConsumedResearchValue;

    // Triad: difficulty axes, see SharedXenoArtifactSystem.Triad
    /// <summary>
    /// Triad: how dangerous this node's effect is to the crew, 1 (inert) to 5 (lethal).
    /// Authored per effect prototype, rated for a sealed hull in space rather than a station.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Danger = 2.0f;

    /// <summary>
    /// Triad: blended difficulty of the trigger rolled onto this node, 1..5. Stamped at creation.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float TriggerDifficulty = 2.0f;
    // End Triad
    #endregion
}

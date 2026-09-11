using Content.Shared.Destructible.Thresholds;
using Content.Shared.EntityTable.EntitySelectors;
using Content.Shared.Xenoarchaeology.Artifact.Prototypes;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad: TimeOffsetSerializer
using Robust.Shared.Utility;

namespace Content.Shared.Xenoarchaeology.Artifact.Components;

/// <summary>
/// This is used for handling interactions with artifacts as well as
/// storing data about artifact node graphs.
/// </summary>
[RegisterComponent, NetworkedComponent, Access(typeof(SharedXenoArtifactSystem)), AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class XenoArtifactComponent : Component
{
    public static string NodeContainerId = "node-container";

    /// <summary>
    /// Marker, if nodes graph should be generated for artifact.
    /// </summary>
    [DataField]
    public bool IsGenerationRequired = true;

    /// <summary>
    /// Container for artifact graph node entities.
    /// </summary>
    [ViewVariables]
    public Container NodeContainer = default!;

    /// <summary>
    /// The nodes in this artifact that are currently "active."
    /// This is cached and updated when nodes are removed, added, or unlocked.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<NetEntity> CachedActiveNodes = new();

    /// <summary>
    /// Cache of interconnected node chunks - segments.
    /// This is cached and updated when nodes are removed, added, or unlocked.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<List<NetEntity>> CachedSegments = new();

    /// <summary>
    /// Marker, if true - node activations should not happen.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Suppressed;

    /// <summary>
    /// A multiplier applied to the calculated point value
    /// to determine the monetary value of the artifact.
    /// </summary>
    [DataField]
    // Triad: 0.40<1.60<1.07. The 1.60 fit (300 sampled full solves: p10 ~167k, median ~280k, p90 ~465k
    // credits) let artifacts carry 56% of all sale value on prod in a day, so the first live season cut
    // it by a third: expect p10 ~111k, median ~187k, p90 ~310k. The credit knob lives here rather than on
    // BasePointValue so research points stay at roughly five times Frontier's scale instead of twenty.
    public float PriceMultiplier = 1.07f; // Frontier: 0.10f<0.40f

    /// <summary>
    /// Triad: per-form payout scalar. A handheld can be carried to its own trigger conditions, so the
    /// same node graph is cheaper to solve on one. Seeded at parity on both forms; WS8 fits it against
    /// sampled solves rather than guessing which way it falls.
    /// </summary>
    [DataField]
    public float FormValueMultiplier = 1.0f;

    #region Unlocking
    /// <summary>
    /// How long does the unlocking state last by default.
    /// </summary>
    [DataField]
    public TimeSpan UnlockStateDuration = TimeSpan.FromSeconds(10); // Frontier: 6<10

    /// <summary>
    /// By how much unlocking state should be prolonged for each node that was unlocked.
    /// </summary>
    [DataField]
    public TimeSpan UnlockStateIncrementPerNode = TimeSpan.FromSeconds(6); // Frontier: 10<6

    /// <summary>
    /// Minimum waiting time between unlock states.
    /// </summary>
    [DataField]
    public TimeSpan UnlockStateRefractory = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When next unlock session can be triggered.
    /// </summary>
    // Triad: this is an absolute CurTime, and CurTime restarts from zero every time the server does.
    // Written raw, a ship file carries the deadline the previous server stamped, so an artifact stored
    // aboard restored onto a fresher server and refused every trigger until the new uptime caught up
    // with the old one. TimeOffsetSerializer writes it as an offset from now and re-bases it on load.
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextUnlockTime;
    #endregion

    // NOTE: you should not be accessing any of these values directly. Use the methods in SharedXenoArtifactSystem.Graph
    #region Graph
    /// <summary>
    /// List of all nodes currently on this artifact.
    /// Indexes are used as a lookup table for <see cref="NodeAdjacencyMatrix"/>.
    /// </summary>
    [DataField, AutoNetworkedField]
    public NetEntity?[] NodeVertices = [];

    /// <summary>
    /// Adjacency matrix that stores connections between this artifact's nodes.
    /// A value of "true" denotes an directed edge from node1 to node2, where the location of the vertex is (node1, node2)
    /// A value of "false" denotes no edge.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<List<bool>> NodeAdjacencyMatrix = new();

    public int NodeAdjacencyMatrixRows => NodeAdjacencyMatrix.Count;
    public int NodeAdjacencyMatrixColumns => NodeAdjacencyMatrix.TryGetValue(0, out var value) ? value.Count : 0;
    #endregion

    #region GenerationInfo

    /// <summary>
    /// The total number of nodes that make up this artifact.
    /// </summary>
    [DataField]
    public MinMax NodeCount = new(10, 16);

    /// <summary>
    /// The amount of nodes that go in each segment.
    /// A segment is an interconnected series of nodes.
    /// </summary>
    [DataField]
    public MinMax SegmentSize = new(5, 8);

    /// <summary>
    /// For each "layer" in a segment (set of nodes with equal depth), how many will we generate?
    /// </summary>
    [DataField]
    public MinMax NodesPerSegmentLayer = new(1, 3);

    /// <summary>
    /// How man nodes can be randomly added on top of usual distribution (per layer).
    /// </summary>
    [DataField]
    public MinMax ScatterPerLayer = new(0, 2);

    /// <summary>
    /// Effects that can be used during this artifact generation.
    /// </summary>
    [DataField]
    public EntityTableSelector EffectsTable = new NestedSelector
    {
        TableId = "XenoArtifactEffectsDefaultTable"
    };

    /// <summary>
    /// Triggers that can be used during this artefact generation.
    /// </summary>
    [DataField]
    public ProtoId<WeightedRandomXenoArchTriggerPrototype> TriggerWeights = "DefaultTriggers";

    // Triad: severity profile, see SharedXenoArtifactSystem.Triad
    /// <summary>
    /// Triad: how node danger climbs with depth on this artifact. Rolled once at generation from
    /// <see cref="SeverityShapeWeights"/>.
    /// </summary>
    [DataField, AutoNetworkedField]
    public XenoArtifactSeverityShape SeverityShape = XenoArtifactSeverityShape.Linear;

    /// <summary>
    /// Triad: the danger the deepest nodes of each segment aim for, 1..5. Every segment climbs from 1
    /// at its roots to this at its leaves. Rolled once at generation from <see cref="SeverityCapWeights"/>.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float SeverityCap = 5f;

    /// <summary>
    /// Triad: weights for rolling <see cref="SeverityShape"/>.
    /// </summary>
    [DataField]
    public Dictionary<XenoArtifactSeverityShape, float> SeverityShapeWeights = new()
    {
        { XenoArtifactSeverityShape.Linear, 1f },
        { XenoArtifactSeverityShape.Log, 1f },
        { XenoArtifactSeverityShape.Exp, 1f },
    };

    /// <summary>
    /// Triad: weights for rolling <see cref="SeverityCap"/>. Higher caps are more common so the
    /// median artifact still has a real payday at the end.
    /// </summary>
    [DataField]
    public Dictionary<int, float> SeverityCapWeights = new()
    {
        { 2, 1f },
        { 3, 2f },
        { 4, 3f },
        { 5, 4f },
    };
    // End Triad
    #endregion

    /// <summary>
    /// Sound effect to be played when artifact node is force-activated.
    /// </summary>
    [DataField]
    public SoundSpecifier? ForceActivationSoundSpecifier = new SoundCollectionSpecifier("ArtifactForceActivation")
    {
        Params = new()
        {
            Variation = 0.1f
        }
    };

    // Triad: SelfActivateAction and ArtifactSelfActivateEvent removed with the sentient artifact;
    // nothing on this tree can attach a mind to press them.
}

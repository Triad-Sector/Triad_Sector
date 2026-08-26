using Content.Shared.Random;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Dictionary;

namespace Content.Shared.Xenoarchaeology.Artifact.Prototypes;

/// <summary> Proto for xeno artifact triggers - markers, which event could trigger node to unlock it. </summary>
[Prototype]
public sealed partial class XenoArchTriggerPrototype : IPrototype
{
    /// <inheritdoc/>
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Tip for user on how to activate this trigger.
    /// </summary>
    [DataField]
    public LocId Tip;

    /// <summary>
    /// Whitelist, describing for which subtype of artifacts this trigger could be used.
    /// </summary>
    [DataField]
    public EntityWhitelist? Whitelist;

    /// <summary>
    /// List of components that represent ways to trigger node.
    /// </summary>
    [DataField]
    public ComponentRegistry Components = new();

    // Triad: authored difficulty, three axes because they come apart. Blended in SharedXenoArtifactSystem.Triad.
    // A prototype left on the defaults is a silent mid-tier rating, not an authored one.

    /// <summary>Triad: can you obtain what this trigger needs, 1 (nothing) to 5 (another crew).</summary>
    [DataField]
    public float Sourcing = 2.0f;

    /// <summary>Triad: what performing it costs once you have the thing, 1 (a click) to 5 (minutes).</summary>
    [DataField]
    public float Effort = 2.0f;

    /// <summary>Triad: can you land it on demand inside an unlock window, 1 (instant) to 5 (external clock).</summary>
    [DataField]
    public float Schedulability = 2.0f;
}

/// <summary>
/// Container for list of xeno artifact triggers and their respective weights to be used in case randomly rolling trigger is required.
/// </summary>
[Prototype]
public sealed partial class WeightedRandomXenoArchTriggerPrototype : IWeightedRandomPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(customTypeSerializer: typeof(PrototypeIdDictionarySerializer<float, XenoArchTriggerPrototype>))]
    public Dictionary<string, float> Weights { get; private set; } = new();
}

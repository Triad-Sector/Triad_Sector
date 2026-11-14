namespace Content.Server._Triad.Drydock;

/// <summary>
/// The clearing sidecar: carries the component fields the engine serializer cannot write. Filled at
/// store and the originals cleared so the serializer does not choke on them, consumed and removed at
/// retrieve. It rides the entity, so nothing has to correlate ids across the round trip.
///
/// <para>Keyed <c>ComponentTypeName|FieldName</c>, valued as base64 of YAML. That key is the drift
/// surface: a C# rename on either side orphans one, so restore counts and names its misses.</para>
/// </summary>
[RegisterComponent]
public sealed partial class DrydockCapturedStateComponent : Component
{
    [DataField]
    public Dictionary<string, string> Fields = new();
}

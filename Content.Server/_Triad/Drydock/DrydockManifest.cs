using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// What a stored revision contained, recorded at store time from what the fidelity walk already
/// holds. A forensic record, not a second serializer: it answers "what was aboard" and the two
/// changes players dispute, and does not attempt a general field diff.
/// </summary>
public sealed class DrydockManifest
{
    /// <summary>
    /// Mirrors <see cref="DrydockFormat.Current"/> at write time, inside the document as well as in
    /// the revision column, so a manifest handled apart from its row stays readable.
    /// </summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = DrydockFormat.Current;

    [JsonPropertyName("e")]
    public List<DrydockManifestEntry> Entries { get; set; } = new();

    public string Serialize()
    {
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    public static DrydockManifest? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<DrydockManifest>(json, SerializerOptions);
    }

    /// <summary>
    /// Indefinite retention makes size a real cost, so property names are short and defaults omitted.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
    };
}

/// <summary>
/// One entity that was aboard. Walk order is load-bearing: <see cref="Parent"/> indexes the entry
/// list rather than naming an entity, because entity ids do not survive a round trip and a manifest
/// has to still mean something a year later.
/// </summary>
public sealed class DrydockManifestEntry
{
    [JsonPropertyName("p")]
    public string Proto { get; set; } = string.Empty;

    /// <summary>
    /// Index of the containing entry, null only for the grid itself: anything directly on the grid
    /// points at the grid's entry, index 0. What makes "removed from the locker" answerable rather
    /// than just "removed".
    /// </summary>
    [JsonPropertyName("c")]
    public int? Parent { get; set; }

    /// <summary>Total damage across all types, from the same dictionary the damage sidecar reads.</summary>
    [JsonPropertyName("d")]
    public float Damage { get; set; }

    /// <summary>Stack count, the one round-trip invariant nothing else here covers.</summary>
    [JsonPropertyName("s")]
    public int Stack { get; set; }

    /// <summary>
    /// The captured-state keys written for this entity, <c>ComponentName|FieldName</c>. A key that
    /// stops resolving after a rename is the drift this records; comparing them unsilences a skip.
    /// </summary>
    [JsonPropertyName("k")]
    public List<string>? CapturedKeys { get; set; }
}

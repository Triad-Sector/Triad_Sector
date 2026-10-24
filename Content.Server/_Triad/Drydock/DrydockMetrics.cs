using Prometheus;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Every loss class the durability design records, as counters the dashboard reads. Two of them are
/// incidents on sight: any fallback to an older revision, and skipped captured state after a merge,
/// which means a rename erased a key from stored ships.
/// </summary>
public static class DrydockMetrics
{
    /// <summary>
    /// A retrieve handed out an older revision than the current one: the current document was corrupt,
    /// would not load, or was stepped past, and an older one loaded.
    /// </summary>
    public static readonly Counter RetrieveFallbacks = Metrics.CreateCounter(
        "drydock_retrieve_fallbacks",
        "Retrieves that used an older revision because a newer one could not be used.");

    /// <summary>A retrieve refused because the document references content that no longer resolves.</summary>
    public static readonly Counter DriftRefusals = Metrics.CreateCounter(
        "drydock_drift_refusals",
        "Retrieves refused because the stored document references content that no longer exists.");

    /// <summary>Keys a retrieve could not restore, by sidecar.</summary>
    public static readonly Counter SkippedStateKeys = Metrics.CreateCounter(
        "drydock_skipped_state_keys",
        "Captured-state and appearance keys a retrieve could not restore.",
        new CounterConfiguration { LabelNames = new[] { "sidecar" } });

    /// <summary>A store refused because its document did not reload to the grid it came from.</summary>
    public static readonly Counter ValidationMismatches = Metrics.CreateCounter(
        "drydock_validation_mismatches",
        "Stores refused because the written document did not round-trip.");

    /// <summary>Re-bake outcomes, by result.</summary>
    public static readonly Counter Rebakes = Metrics.CreateCounter(
        "drydock_rebakes",
        "Re-bake attempts on stored ships, by result.",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    /// <summary>Stored ships whose current document no re-bake tier can heal, as of the last sweep.</summary>
    public static readonly Gauge UnresolvableShips = Metrics.CreateGauge(
        "drydock_unresolvable_ships",
        "Stored ships whose current document references content no mapping resolves, at the last sweep.");
}

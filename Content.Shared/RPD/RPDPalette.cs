namespace Content.Shared.RPD;

/// <summary>
/// Canonical pipe-color palette for the Rapid Piping Device. Shared so the BUI and the server-side validation
/// agree on which keys are valid — a misbehaving client can't get arbitrary colors stored on its RPD.
/// </summary>
/// <remarks>
/// The RPD is an atmospherics tool, so the palette is the atmos subset of the spray painter (PR #129):
/// pipe-loop labels plus every named-gas color. Decorative entries from the painter (bright yellow, coral,
/// pink, etc.) are intentionally omitted — they have no atmos meaning and would just inflate the picker.
/// Keys and hex values are kept byte-identical to the painter so a pipe built by the RPD and a label
/// sprayed by the painter read the same color.
/// </remarks>
public static class RPDPalette
{
    /// <summary>
    /// The "no override" palette slot. When this key is selected, the spawned pipe keeps its prototype's
    /// default color and skips the <c>PipeColorVisuals.Color</c> appearance write.
    /// </summary>
    public const string DefaultKey = "default";

    /// <summary>
    /// General-purpose pipe loop labels plus the no-override slot. Rendered on the first row of the picker
    /// so atmos techs always see them at a glance.
    /// </summary>
    public static readonly IReadOnlyList<string> GeneralKeys = new[]
    {
        DefaultKey, "waste", "distro", "air", "mix", "external",
    };

    /// <summary>
    /// Named-gas colors, alphabetized. Rendered on the second row of the picker.
    /// </summary>
    public static readonly IReadOnlyList<string> GasKeys = new[]
    {
        "ammonia", "bz", "carbon dioxide", "frezon", "healium",
        "nitrium", "nitrogen", "nitrous oxide", "oxygen", "plasma",
        "pluoxium", "tritium", "water vapor",
    };

    public static readonly IReadOnlyDictionary<string, Color?> Colors = new Dictionary<string, Color?>
    {
        { DefaultKey, null },
        // General-purpose pipe loop labels.
        { "waste", Color.FromHex("#990000") },
        { "distro", Color.FromHex("#0055cc") },
        { "air", Color.FromHex("#03fcd3") },
        { "mix", Color.FromHex("#947507") },
        { "external", Color.FromHex("#9955cc") },
        // Named gases (alphabetical).
        { "ammonia", Color.FromHex("#800080") },
        { "bz", Color.FromHex("#d7bdb4") },
        { "carbon dioxide", Color.FromHex("#525252") },
        { "frezon", Color.FromHex("#87CEEB") },
        { "healium", Color.FromHex("#0FFF50") },
        { "nitrium", Color.FromHex("#F1DD38") },
        { "nitrogen", Color.FromHex("#ba0000") },
        { "nitrous oxide", Color.FromHex("#FF4040") },
        { "oxygen", Color.FromHex("#0335FCFF") },
        { "plasma", Color.FromHex("#FF00FF") },
        { "pluoxium", Color.FromHex("#4682B4") },
        { "tritium", Color.FromHex("#7DC855") },
        { "water vapor", Color.FromHex("#969696") },
    };

    /// <summary>
    /// Returns true when the supplied key is a recognized palette slot. Used by the server to validate
    /// <c>RPDColorChangeMessage</c> payloads from clients.
    /// </summary>
    public static bool IsValid(string key) => Colors.ContainsKey(key);
}

namespace Content.Shared.RPD;

/// <summary>
/// Canonical pipe-color palette for the Rapid Piping Device. Shared so the BUI and the server-side validation
/// agree on which keys are valid — a misbehaving client can't get arbitrary colors stored on its RPD.
/// </summary>
public static class RPDPalette
{
    /// <summary>
    /// The "no override" palette slot. When this key is selected, the spawned pipe keeps its prototype's
    /// default color and skips the <c>PipeColorVisuals.Color</c> appearance write.
    /// </summary>
    public const string DefaultKey = "default";

    public static readonly IReadOnlyDictionary<string, Color?> Colors = new Dictionary<string, Color?>
    {
        { DefaultKey, null },
        { "red", Color.FromHex("#FF1212FF") },
        { "yellow", Color.FromHex("#B3A234FF") },
        { "brown", Color.FromHex("#947507FF") },
        { "green", Color.FromHex("#3AB334FF") },
        { "cyan", Color.FromHex("#03FCD3FF") },
        { "blue", Color.FromHex("#0335FCFF") },
        { "white", Color.FromHex("#FFFFFFFF") },
        { "black", Color.FromHex("#333333FF") },
        { "waste", Color.FromHex("#990000") },
        { "distro", Color.FromHex("#0055cc") },
        { "air", Color.FromHex("#03fcd3") },
        { "mix", Color.FromHex("#947507") },
    };

    /// <summary>
    /// Returns true when the supplied key is a recognized palette slot AND the supplied color matches the
    /// canonical value (or both sides agree it's the default null). Used by the server to validate
    /// <c>RPDColorChangeMessage</c> payloads from clients.
    /// </summary>
    public static bool IsValid(string key, Color? color)
    {
        if (!Colors.TryGetValue(key, out var expected))
            return false;
        return expected == color;
    }
}

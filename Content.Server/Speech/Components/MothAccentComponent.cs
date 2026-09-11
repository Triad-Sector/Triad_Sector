namespace Content.Server.Speech.Components;

/// <summary>
///     Buzzz!
/// </summary>
[RegisterComponent]
public sealed partial class MothAccentComponent : Component
{
    // Triad: the z-buzz alone almost never fires (most lines have no z), so moth speech read as plain
    // English. A low-chance fluttery wingbeat tic gives it identity on any line, mirroring the skeleton
    // "ACK ACK" append. Kept modest -- this is a baseline species accent, on every moth's every line.
    [DataField("flutterChance")]
    public float FlutterChance = 0.12f;

    [DataField]
    public List<string> Flutters = new()
    {
        "accent-moth-flutter-1",
        "accent-moth-flutter-2",
        "accent-moth-flutter-3",
    };
}

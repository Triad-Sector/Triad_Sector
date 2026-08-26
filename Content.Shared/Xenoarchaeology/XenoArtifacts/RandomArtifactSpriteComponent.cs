namespace Content.Shared.Xenoarchaeology.XenoArtifacts;

[RegisterComponent]
public sealed partial class RandomArtifactSpriteComponent : Component
{
    [DataField("minSprite")]
    public int MinSprite = 1;

    [DataField("maxSprite")]
    public int MaxSprite = 14;

    [DataField("activationTime")]
    public double ActivationTime = 0.4;

    /// <summary>
    /// Triad: the rolled sprite index, persisted. It used to live only in AppearanceData, which is an
    /// internal field with no DataField on it and therefore does not survive a save at all. Combined
    /// with the roll hanging off MapInitEvent, which a file-restored entity never receives, an
    /// artifact stored on a ship came back with no index and rendered as its bare prototype sprite.
    /// Null means "not rolled yet".
    /// </summary>
    [DataField]
    public int? SpriteIndex;

    public TimeSpan? ActivationStart;
}

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Version stamps for the formats the drydock owns, as opposed to the ones the engine owns.
/// </summary>
public static class DrydockFormat
{
    /// <summary>
    /// Written to <c>drydock_format_ver</c> on every revision, covering the sidecar encoding and the
    /// manifest shape together. Bump it when either changes in a way an older reader would misread,
    /// and add a step to <see cref="DrydockDocumentRebake.FormatSteps"/> that migrates the old version
    /// forward, so the re-bake sweep carries stored ships across. No step exists yet, so a version 1
    /// revision is read as it stands, per the note below.
    /// </summary>
    /// <remarks>
    /// Version 2: absolute-time fields gained <c>TimeOffsetSerializer</c> and are written as an
    /// offset from the storing server's clock. A version 1 revision holds raw values and this reader
    /// adds the current clock to them, so a stored microwave or AME idles until the inflated
    /// deadline passes. Not a refusal: the cost is a delay on a few machines and it clears on the
    /// next store, where refusing would strand every ship filed before the change.
    /// </remarks>
    public const int Current = 2;

    /// <summary>
    /// The oldest <see cref="Current"/> value a retrieve will still read. Raising this abandons
    /// every revision below it, so it moves only after every such revision has been re-baked. The
    /// sweep re-bakes only a document its transform changes, so that needs a format step for the
    /// version being abandoned first.
    /// </summary>
    public const int MinimumSupported = 1;
}

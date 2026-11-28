namespace Content.Server.Lathe.Components;

/// <summary>
/// For EntityQuery to keep track of which lathes are producing
/// </summary>
// Triad: UnsavedComponent. None of these fields are data fields, so a saved lathe mid-print
// reloaded with this marker and no recipe: the update loop skips a producing lathe with no
// recipe, and the reboot pass skips any lathe that is producing, so it never finished and never
// restarted. The queue is what persists; the reboot pass resumes it once this is absent.
// [RegisterComponent]
[RegisterComponent, UnsavedComponent]
public sealed partial class LatheProducingComponent : Component
{
    /// <summary>
    /// The time at which production began
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan StartTime;

    /// <summary>
    /// How long it takes to produce the recipe.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan ProductionLength;

    /// <summary>
    /// The Entity that queued this recipe - Mono
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? Actor;
}


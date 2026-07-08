using Robust.Shared.Configuration;

namespace Content.Shared._DV.CCVars;

/// <summary>
/// DeltaV specific cvars.
/// </summary>
[CVarDefs]
// ReSharper disable once InconsistentNaming - Shush you
public sealed class DCCVars
{
    /// <summary>
    /// Anti-EORG measure. Will add pacified to all players upon round end.
    /// Its not perfect, but gets the job done.
    /// </summary>
    public static readonly CVarDef<bool> RoundEndPacifist =
        CVarDef.Create("game.round_end_pacifist", false, CVar.REPLICATED);

    /// <summary>
    /// Whether the no EORG popup is enabled.
    /// </summary>
    public static readonly CVarDef<bool> RoundEndNoEorgPopup =
        CVarDef.Create("game.round_end_eorg_popup_enabled", false, CVar.SERVER | CVar.REPLICATED);

    /// <summary>
    /// Skip the no EORG popup.
    /// </summary>
    public static readonly CVarDef<bool> SkipRoundEndNoEorgPopup =
        CVarDef.Create("game.skip_round_end_eorg_popup", false, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// How long to display the EORG popup for.
    /// </summary>
    public static readonly CVarDef<float> RoundEndNoEorgPopupTime =
        CVarDef.Create("game.round_end_eorg_popup_time", 5f, CVar.SERVER | CVar.REPLICATED);

    /// <summary>
    /// Disables all vision filters for species like Vulpkanin or Harpies. There are good reasons someone might want to disable these.
    /// </summary>
    public static readonly CVarDef<bool> NoVisionFilters =
        CVarDef.Create("accessibility.no_vision_filters", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Whether the Shipyard is enabled.
    /// </summary>
    //public static readonly CVarDef<bool> Shipyard =
    //    CVarDef.Create("shuttle.shipyard", true, CVar.SERVERONLY);


    /// <summary>
    /// What year it is in the game. Actual value shown in game is server date + this value.
    /// </summary>
    public static readonly CVarDef<int> YearOffset =
        CVarDef.Create("game.current_year_offset", 550, CVar.SERVERONLY);

    /*
     * Traits
     */

    /// <summary>
    /// Maximum number of traits that can be selected globally.
    /// Triad: 0 (or any non-positive value) means unlimited; the count gate and the UI counter are
    /// both dropped, leaving per-category trait/point dials as the only limits. The per-category
    /// equivalents (TraitCategoryPrototype.MaxTraits/MaxPoints) use the SAME &lt;= 0 = unlimited rule,
    /// so 0 never means "forbid everything" on either side.
    /// </summary>
    public static readonly CVarDef<int> MaxTraitCount =
        CVarDef.Create("traits.max_count", 0, CVar.SERVER | CVar.REPLICATED);

    /// <summary>
    /// Maximum trait points available to spend from one global pool.
    /// Traits with positive cost consume points, negative cost traits grant points.
    /// Triad: defaults to 0 = unlimited; the global gate, the UI label and the progress bar are all
    /// dropped, leaving per-category MaxPoints as the point budget (so points allocate per category,
    /// the same way the count cap does). Set a positive value to restore a single shared pool.
    /// </summary>
    public static readonly CVarDef<int> MaxTraitPoints =
        CVarDef.Create("traits.max_points", 0, CVar.SERVER | CVar.REPLICATED);

    /// <summary>
    /// Whether to skip showing the disabled traits popup when spawning.
    /// </summary>
    public static readonly CVarDef<bool> SkipDisabledTraitsPopup =
        CVarDef.Create("traits.skip_disabled_traits_popup", false, CVar.CLIENT | CVar.ARCHIVE);
}

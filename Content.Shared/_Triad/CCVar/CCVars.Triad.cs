
using Robust.Shared.Configuration;

namespace Content.Shared._Triad.CCVar;

/// <summary>
/// Configuration variables for Triad features
/// </summary>
[CVarDefs]
public sealed class TriadCCVars
{
    /// <summary>
    ///     How much loading a saved ship costs, as a fraction of its full appraisal. 0.15f = 15%.
    ///     Was 0.3f until 2026-08-29.
    /// </summary>
    public static readonly CVarDef<float> LoadShipPrice =
        CVarDef.Create("triad.load_ship_price", 0.15f, CVar.SERVER | CVar.REPLICATED);

    // Triad: tamper protection
    /// <summary>
    /// Tamper protection rollout mode. "off" disables all checks and logging; "notify" passively
    /// collects signatures and writes audit events but never blocks; "enforce" rejects loads that
    /// fail the signature/trust checks. Unrecognised values fall back to "notify".
    /// </summary>
    public static readonly CVarDef<string> TamperMode =
        CVarDef.Create("triad.tamper_mode", "notify", CVar.SERVERONLY);

    /// <summary>
    /// Default page size for the admin audit-log viewer. Hard-capped server-side at 500.
    /// </summary>
    public static readonly CVarDef<int> TamperAdminPageSize =
        CVarDef.Create("triad.tamper_admin_page_size", 50, CVar.SERVERONLY);

    /// <summary>
    /// F14 fix: directory holding the on-disk PEM files for the tamper-protection signing keys.
    /// The DB stores only the public key and a KeyId; the private key lives in a file named
    /// {KeyId}.pem inside this directory. Default is relative to the server's working directory.
    /// Admins should ensure the directory has restrictive permissions (0700 on Unix; ACL
    /// equivalent on Windows). The keystore creates the directory with 0700 on first use.
    /// </summary>
    public static readonly CVarDef<string> TamperSigningKeysDir =
        CVarDef.Create("triad.tamper_signing_keys_dir", "./triad-signing-keys", CVar.SERVERONLY);

    // Triad: radiator overhaul
    /// <summary>
    /// Whether radiators pushed into the top thermal bucket (white-hot) slowly
    /// take structural damage until they rupture. Off by default: the glow ramp
    /// and the contact burn already telegraph an overloaded fin, so losing the
    /// hardware on top of that is punishment rather than feedback. Turn it on
    /// to force players to spread load across an array.
    /// </summary>
    public static readonly CVarDef<bool> RadiatorOverheatDamage =
        CVarDef.Create("triad.radiator_overheat_damage", false, CVar.SERVERONLY);

    public static readonly CVarDef<bool> UseNightVisionColor =
        CVarDef.Create("triad.use_night_vision_color", false, CVar.CLIENTONLY | CVar.ARCHIVE, "If a custom night vision color should be used instead of the default.");

    public static readonly CVarDef<string> NightVisionColor =
        CVarDef.Create("triad.night_vision_color", "#00FF00", CVar.CLIENTONLY | CVar.ARCHIVE, "The tint/phosphor color of night vision.");

    // Triad: atmos
    /// <summary>
    /// Whether atmos input devices (scrubbers, siphoning vents, passive vents, intakes) may pull gas
    /// out of a map's own atmosphere. Off by default, which limits them to the sector map and ships
    /// in FTL, so expedition planets can no longer be drained for free gas.
    /// </summary>
    public static readonly CVarDef<bool> AllowMapGasExtraction =
        CVarDef.Create("triad.atmos.allow_map_gas_extraction", false, CVar.SERVER | CVar.REPLICATED);

    // Triad: market data
    // The queue knobs mirror the admin log ones, which solve the same problem at production volume
    // on this server: adminlogs.queue_send_delay_seconds, queue_max, pre_round_queue_max and
    // drop_threshold. Defaults are deliberately identical so the two behave alike under load.

    /// <summary>
    /// Master switch. Off means nothing is recorded and no database work happens at all. On its own
    /// this does not create the tables; the migration does that whether or not this is set.
    /// </summary>
    public static readonly CVarDef<bool> MarketDataEnabled =
        CVarDef.Create("triad.market.enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Whether to record per-item line rows as well as transaction headers. Lines are the entire
    /// input to the price rollup, so turning this off leaves a Grafana-only feature with no in-game
    /// consumer. Separate from the master switch because line capture is the expensive half and
    /// wants to be disableable on its own if a sale on a full pallet ever costs real frame time.
    /// </summary>
    public static readonly CVarDef<bool> MarketDataLinesEnabled =
        CVarDef.Create("triad.market.lines_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// How long the writer waits between flushes.
    /// </summary>
    public static readonly CVarDef<float> MarketDataQueueSendDelay =
        CVarDef.Create("triad.market.queue_send_delay_seconds", 5f, CVar.SERVERONLY);

    /// <summary>
    /// Queue depth that forces a flush before the delay elapses.
    /// </summary>
    public static readonly CVarDef<int> MarketDataQueueMax =
        CVarDef.Create("triad.market.queue_max", 5000, CVar.SERVERONLY);

    /// <summary>
    /// Cap on the separate pre-round queue. Rows can be created before a round has an id, so they
    /// are held and stamped once it exists; character spawn sits exactly on that boundary.
    /// </summary>
    public static readonly CVarDef<int> MarketDataPreRoundQueueMax =
        CVarDef.Create("triad.market.pre_round_queue_max", 5000, CVar.SERVERONLY);

    /// <summary>
    /// Depth past which rows are dropped rather than queued. Dropping is the correct behaviour for
    /// telemetry: the tick must never stall for it. Drops are counted and logged, never silent.
    /// </summary>
    public static readonly CVarDef<int> MarketDataDropThreshold =
        CVarDef.Create("triad.market.drop_threshold", 20000, CVar.SERVERONLY);

    /// <summary>
    /// How long raw transactions, splits and lines are kept. The daily price rollup is permanent and
    /// unaffected. Zero or less disables the purge entirely and keeps everything.
    /// </summary>
    public static readonly CVarDef<int> MarketDataRetentionDays =
        CVarDef.Create("triad.market.retention_days", 90, CVar.SERVERONLY);
}

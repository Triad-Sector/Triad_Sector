using Content.Shared.Atmos;
using Content.Shared._Triad.Atmos;
using Robust.Shared.Audio;

namespace Content.Server.Atmos.Components;

[RegisterComponent]
public sealed partial class HeatExchangerComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("inlet")]
    public string InletName { get; set; } = "inlet";

    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("outlet")]
    public string OutletName { get; set; } = "outlet";

    /// <summary>
    /// Pipe conductivity (mols/kPa/sec).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("conductivity")]
    public float G { get; set; } = 1f;

    /// <summary>
    /// Thermal convection coefficient (J/degK/sec).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("convectionCoefficient")]
    public float K { get; set; } = 8000f;

    /// <summary>
    /// Thermal radiation coefficient. Number of "effective" tiles this
    /// radiator radiates compared to superconductivity tile losses.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("radiationCoefficient")]
    public float alpha { get; set; } = 140f;

    // Triad: body-as-hub thermal model. The radiator body couples both pipe
    // nets and the tile environment; all exchange is temperature-driven.

    /// <summary>
    /// Triad: temperature of the radiator body itself (fins and shell), K.
    /// Both pipe nets and the environment exchange heat with this, giving the
    /// device thermal inertia. Drives the glow bucket and the contact hazard.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("bodyTemperature")]
    public float BodyTemperature = Atmospherics.T20C;

    /// <summary>
    /// Triad: heat capacity of the radiator body (J/K). Larger = more lag
    /// between gas temperature and body temperature (and glow).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("bodyHeatCapacity")]
    public float BodyHeatCapacity = 2500f;

    /// <summary>
    /// Triad: gas-to-body conductance at rated flow (J/K/sec). Forced
    /// convection: actual conductance scales between
    /// <see cref="StaticConductanceFloor"/> and 1x with flow through the device.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("pipeConductance")]
    public float PipeConductance = 8000f;

    /// <summary>
    /// Triad: fraction of <see cref="PipeConductance"/> available at zero flow
    /// (natural convection). Keeps static setups honest but weak; this is what
    /// makes pumped loops worth building.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("staticConductanceFloor")]
    public float StaticConductanceFloor = 0.1f;

    /// <summary>
    /// Triad: flow (mol/sec) at which gas-to-body conductance reaches full
    /// strength.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("ratedFlow")]
    public float RatedFlow = 10f;

    /// <summary>
    /// Triad: cap on the environment-density multiplier applied to the
    /// convection coefficient, so extreme-pressure rooms don't scale K absurdly.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("maxDensityFactor")]
    public float MaxDensityFactor = 5f;

    /// <summary>
    /// Triad: body-to-body conductance (J/K/sec) toward a radiator directly
    /// connected on a port. Lets arrays share thermal mass: heat walks fin to
    /// fin, so big arrays spool up and cool down as a unit.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("bodyLinkConductance")]
    public float BodyLinkConductance = 800f;

    /// <summary>
    /// Triad: radiator directly connected on the inlet port, if any.
    /// Runtime cache, refreshed each atmos update from the node graph.
    /// </summary>
    [ViewVariables]
    public EntityUid? InletNeighbor;

    /// <summary>
    /// Triad: radiator directly connected on the outlet port, if any.
    /// Runtime cache, refreshed each atmos update from the node graph.
    /// </summary>
    [ViewVariables]
    public EntityUid? OutletNeighbor;

    /// <summary>
    /// Triad: last connection state pushed to appearance; dirty-on-change only.
    /// </summary>
    [ViewVariables]
    public RadiatorConnectionState ConnectionState = RadiatorConnectionState.Isolated;

    /// <summary>
    /// Triad: current quantized thermal bucket of the body. Recomputed each
    /// atmos update; appearance is dirtied only when this changes.
    /// </summary>
    [ViewVariables]
    public RadiatorThermalBucket Bucket = RadiatorThermalBucket.Neutral;

    /// <summary>
    /// Triad: metallic creak played on bucket transitions (thermal expansion).
    /// Null disables.
    /// </summary>
    /// <remarks>
    /// Variation is the engine's own pitch jitter: it adds a normally
    /// distributed offset to the pitch scale per play, so a handful of source
    /// files stop sounding like the same file retriggering. Combined with the
    /// collection's random file pick and the one-in-four roll on transitions,
    /// that is enough variety without cutting extra assets.
    /// </remarks>
    [DataField("creakSound")]
    public SoundSpecifier? CreakSound = new SoundCollectionSpecifier("RadiatorCreak")
    {
        Params = AudioParams.Default.WithVolume(-6f).WithVariation(0.125f),
    };

    /// <summary>
    /// Triad: body-to-mob contact conductance (J/K/sec) for entities standing
    /// on the radiator. Deliberately small so heat feeds body temperature
    /// slowly enough to react to before damage thresholds.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("contactConductance")]
    public float ContactConductance = 30f;

    /// <summary>
    /// Triad: structural damage per second while white-hot, when the overheat
    /// cvar is enabled.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("overheatDamagePerSecond")]
    public float OverheatDamagePerSecond = 5f;

    /// <summary>
    /// Triad: entities currently standing on the radiator, maintained by
    /// step-trigger events. Contact heat transfer only runs while non-empty.
    /// </summary>
    [ViewVariables]
    public readonly HashSet<EntityUid> Standing = new();
}


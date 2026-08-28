using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Atmos.Piping.Unary.Components;
using Content.Server.Atmos;
using Content.Server.Atmos.Components;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.NodeContainer;
using System.Linq; // Triad
using Content.Server.Temperature.Components; // Triad
using Content.Server.Temperature.Systems; // Triad
using Content.Shared.Atmos.Piping;
using Content.Shared.Atmos;
using Content.Shared.CCVar;
using Content.Shared.Damage; // Triad
using Content.Shared.Damage.Prototypes; // Triad
using Content.Shared.Interaction;
using Content.Shared.StepTrigger.Systems; // Triad
using Content.Shared._Triad.Atmos; // Triad
using Content.Shared._Triad.CCVar; // Triad
using JetBrains.Annotations;
using Robust.Shared.Audio; // Triad
using Robust.Shared.Audio.Systems; // Triad
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects; // Triad
using Robust.Shared.Prototypes; // Triad
using Robust.Shared.Random; // Triad

namespace Content.Server.Atmos.EntitySystems;

public sealed partial class HeatExchangerSystem : EntitySystem
{
    [Dependency] private AtmosphereSystem _atmosphereSystem = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private NodeContainerSystem _nodeContainer = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!; // Triad
    [Dependency] private SharedAudioSystem _audio = default!; // Triad
    [Dependency] private TemperatureSystem _temperature = default!; // Triad
    [Dependency] private DamageableSystem _damageable = default!; // Triad
    [Dependency] private IPrototypeManager _proto = default!; // Triad
    [Dependency] private IRobustRandom _random = default!; // Triad
    [Dependency] private SharedPointLightSystem _pointLight = default!; // Triad

    // Triad: bucket boundaries (K), index i is the floor of bucket i+1 in
    // RadiatorThermalBucket order. The real incandescence ladder, no
    // readability compression. Calculated 2026-08-21:
    // - Glow onset = the Draper point, 525 C = 798 K (Wikipedia, Draper 1847).
    // - Colour floors from Chapman's Workshop Technology (Wikipedia "Red
    //   heat"), C + 273 -> K: cherry 815 C = 1088, orange 982 C = 1255,
    //   yellow 1093 C = 1366, white 1315 C = 1588.
    // - Cross-check, Stirling 1905 (same article): cherry 1073, deep orange
    //   1373, white heat 1573 - brackets Chapman; the sources disagree by
    //   ~100 K per band, so these floors are good to about +/-50 K.
    // A fin below 798 K shows NO glow no matter how much it hurts - the
    // contact hazard is gated on temperature separately (see
    // HazardHotThreshold), because metal burns long before it glows.
    private static readonly float[] BucketBoundaries = { 200f, 798f, 1088f, 1255f, 1366f, 1588f };

    // Triad: deadband so a body temperature hovering on a boundary doesn't
    // flap the bucket (appearance spam + creak spam).
    private const float BucketHysteresis = 3f;

    // Triad: odds that a bucket transition actually creaks.
    private const float CreakChance = 0.25f;

    float tileLoss;
    private bool _overheatDamage; // Triad

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HeatExchangerComponent, AtmosDeviceUpdateEvent>(OnAtmosUpdate);

        // Triad: contact hazard bookkeeping; the attempt gate means radiators
        // outside the hazard temperature band never even trigger.
        SubscribeLocalEvent<HeatExchangerComponent, StepTriggerAttemptEvent>(OnStepTriggerAttempt);
        SubscribeLocalEvent<HeatExchangerComponent, StepTriggeredOnEvent>(OnStepOn);
        SubscribeLocalEvent<HeatExchangerComponent, StepTriggeredOffEvent>(OnStepOff);

        // Triad: dropping a radiator out of the graph must clear its link
        // visuals immediately; neighbors self-correct on their next update.
        SubscribeLocalEvent<HeatExchangerComponent, AnchorStateChangedEvent>(OnAnchorChanged);

        // Getting CVars is expensive, don't do it every tick
        Subs.CVar(_cfg, CCVars.SuperconductionTileLoss, CacheTileLoss, true);
        Subs.CVar(_cfg, TriadCCVars.RadiatorOverheatDamage, v => _overheatDamage = v, true); // Triad
    }

    // Triad: contact-hazard thresholds (K), decoupled from the glow ladder.
    // The ladder sits at real incandescence temperatures (onset 798 K), but
    // metal burns skin long before it glows — a 700 K fin is 427 C and must
    // hurt while showing no light. 450 K preserves the burn onset the hazard
    // has had since the rework; 200 K is the chill floor, matching Cold.
    private const float HazardHotThreshold = 450f;
    private const float HazardColdThreshold = 200f;

    private static bool IsHazardTemperature(HeatExchangerComponent comp)
        => comp.BodyTemperature >= HazardHotThreshold || comp.BodyTemperature <= HazardColdThreshold;

    private void OnStepTriggerAttempt(EntityUid uid, HeatExchangerComponent comp, ref StepTriggerAttemptEvent args)
    {
        if (IsHazardTemperature(comp))
            args.Continue = true;
    }

    private void OnStepOn(EntityUid uid, HeatExchangerComponent comp, ref StepTriggeredOnEvent args)
    {
        comp.Standing.Add(args.Tripper);
    }

    private void OnStepOff(EntityUid uid, HeatExchangerComponent comp, ref StepTriggeredOffEvent args)
    {
        comp.Standing.Remove(args.Tripper);
    }

    private void CacheTileLoss(float val)
    {
        tileLoss = val;
    }

    private void OnAtmosUpdate(EntityUid uid, HeatExchangerComponent comp, ref AtmosDeviceUpdateEvent args)
    {
        // make sure that the tile the device is on isn't blocked by a wall or something similar.
        if (args.Grid is {} grid
            && _transform.TryGetGridTilePosition(uid, out var tile)
            && _atmosphereSystem.IsTileAirBlocked(grid, tile))
        {
            return;
        }

        if (!_nodeContainer.TryGetNodes(uid, comp.InletName, comp.OutletName, out PipeNode? inlet, out PipeNode? outlet))
            return;

        var dt = args.dt;

        // Goobstation
        var isInline = inlet == outlet;

        // Triad: refresh port links (array membership) from the node graph.
        // ReachableNodes is the engine's live direct-connection set, so this
        // is a 1-3 entry scan, and parallel snakes one tile apart can never
        // false-positive (their nodes never join).
        UpdateLinks(uid, comp, inlet, outlet, isInline);

        // Triad: body-as-hub rework. The old law cooled only the packet of gas
        // transiting the device, gated on ΔP (zero flow = zero cooling, which is
        // not how radiators work). Now the radiator body exchanges with both
        // pipe nets and the tile environment purely on temperature difference;
        // flow through the device scales the gas↔body conductance (forced vs
        // natural convection), which is what keeps pumped loops far ahead of
        // static stubs.

        // Gas movement first (ΔP throttle, upstream math unchanged). The moles
        // moved this tick double as the forced-convection signal.
        float n = 0f;
        if (!isInline)
        {
            // Let n = moles(inlet) - moles(outlet), really a Δn
            var P = inlet.Air.Pressure - outlet.Air.Pressure; // really a ΔP
            // Such that positive P causes flow from the inlet to the outlet.

            // We want moles transferred to be proportional to the pressure difference, i.e.
            // dn/dt = G*P

            // To solve this we need to write dn in terms of P. Since PV=nRT, dP/dn=RT/V.
            // This assumes that the temperature change from transferring dn moles is negligible.
            // Since we have P=Pi-Po, then dP/dn = dPi/dn-dPo/dn = R(Ti/Vi - To/Vo):
            float dPdn = Atmospherics.R * (outlet.Air.Temperature / outlet.Air.Volume + inlet.Air.Temperature / inlet.Air.Volume);

            // Multiplying both sides of the differential equation by dP/dn:
            // dn/dt * dP/dn = dP/dt = G*P * (dP/dn)
            // Which is a first-order linear differential equation with constant (heh...) coefficients:
            // dP/dt + kP = 0, where k = -G*(dP/dn).
            // This differential equation has a closed-form solution, namely:
            // Triad: guard dPdn against two empty/zero-temperature nets (0/0 -> NaN).
            if (dPdn > 0f)
            {
                float Pfinal = P * MathF.Exp(-comp.G * dPdn * dt);

                // Finally, back out n, the moles transferred in this tick:
                n = (P - Pfinal) / dPdn;

                // Triad: move the gas unmodified; heat exchange happens below via
                // the body, not by cooling the packet mid-flight.
                if (n > 0)
                    _atmosphereSystem.Merge(outlet.Air, inlet.Air.Remove(n));
                else if (n < 0)
                    _atmosphereSystem.Merge(inlet.Air, outlet.Air.Remove(-n));
            }
        }

        // Triad: forced vs natural convection. Conductance scales from the
        // static floor up to full strength with flow through the device.
        var flowFactor = comp.RatedFlow > 0f
            ? Math.Clamp(MathF.Abs(n) / (comp.RatedFlow * dt), 0f, 1f)
            : 1f;
        var pipeConductance = comp.PipeConductance *
            (comp.StaticConductanceFloor + (1f - comp.StaticConductanceFloor) * flowFactor);

        // Triad: gas ↔ body. Inline legacy units have one net; exchange once.
        ExchangeGasWithBody(comp, inlet.Air, pipeConductance, dt);
        if (!isInline)
            ExchangeGasWithBody(comp, outlet.Air, pipeConductance, dt);

        // Triad: body ↔ environment. Radiation toward TCMB (vacuum) or the
        // environment temperature; convection only in atmosphere, scaled by
        // environment density (dense rooms convect better).
        var radTemp = Atmospherics.TCMB;

        var environment = _atmosphereSystem.GetContainingMixture(uid, true, true);
        bool hasEnv = false;
        float CEnv = 0f;
        float densityFactor = 0f;
        if (environment != null)
        {
            CEnv = _atmosphereSystem.GetHeatCapacity(environment, true);
            hasEnv = CEnv >= Atmospherics.MinimumHeatCapacity && environment.TotalMoles > 0f;
            if (hasEnv)
            {
                radTemp = environment.Temperature;
                densityFactor = Math.Clamp(environment.Pressure / Atmospherics.OneAtmosphere, 0f, comp.MaxDensityFactor);
            }
        }

        // How ΔT' scales in respect to heat transferred
        // Triad: the exchanging mass is now the radiator body, not a gas packet.
        float TdivQ = 1f / comp.BodyHeatCapacity;
        // Since it's ΔT, also account for the environment's temperature change
        if (hasEnv)
            TdivQ += 1f / CEnv;

        // Radiation
        float dTR = comp.BodyTemperature - radTemp; // Triad: body temperature, was packet temperature
        float dTRA = MathF.Abs(dTR);
        float a0 = tileLoss / MathF.Pow(Atmospherics.T20C, 4);
        // ΔT' = -kΔT^4, k = -ΔT'/ΔT^4
        float kR = comp.alpha * a0 * TdivQ;
        // Based on the fact that ((3t)^(-1/3))' = -(3t)^(-4/3) = -((3t)^(-1/3))^4, and ΔT' = -kΔT^4.
        float dT2R = dTR * MathF.Pow((1f + 3f * kR * dt * dTRA * dTRA * dTRA), -1f/3f);
        float dER = (dTR - dT2R) / TdivQ;
        comp.BodyTemperature -= dER / comp.BodyHeatCapacity; // Triad: was AddHeat(xfer, -dER)
        if (hasEnv && environment != null)
        {
            _atmosphereSystem.AddHeat(environment, dER);

            // Convection

            // Positive dT is from pipe to surroundings
            float dT = comp.BodyTemperature - environment.Temperature; // Triad: body temperature
            // ΔT' = -kΔT, k = -ΔT' / ΔT
            float k = comp.K * densityFactor * TdivQ; // Triad: density-scaled
            float dT2 = dT * MathF.Exp(-k * dt);
            float dE = (dT - dT2) / TdivQ;
            comp.BodyTemperature -= dE / comp.BodyHeatCapacity; // Triad: was AddHeat(xfer, -dE)
            _atmosphereSystem.AddHeat(environment, dE);
        }

        // Triad: body ↔ linked-neighbor bodies. Arrays share thermal mass:
        // heat walks fin to fin, so a long run spools up and cools down as a
        // unit (and one white-hot fin drags its neighbors toward the ceiling).
        ExchangeBodyWithNeighbor(uid, comp, comp.InletNeighbor, dt);
        ExchangeBodyWithNeighbor(uid, comp, comp.OutletNeighbor, dt);

        // Triad: quantized thermal skin, dirty-on-change only.
        UpdateBucket(uid, comp);

        // Triad: contact heat/chill for anyone standing on the fins, and the
        // cvar-gated overheat ceiling. Both gate on the bucket, so neutral
        // radiators cost nothing here.
        ContactHazard(comp, dt);
        OverheatDamage(uid, comp, dt);
    }

    /// <summary>
    /// Triad: recompute which ports connect directly to another radiator and
    /// push the sprite state on change. Inline legacy units never link.
    /// </summary>
    private void UpdateLinks(EntityUid uid, HeatExchangerComponent comp, PipeNode inlet, PipeNode outlet, bool isInline)
    {
        comp.InletNeighbor = isInline ? null : FindRadiatorNeighbor(uid, inlet);
        comp.OutletNeighbor = isInline ? null : FindRadiatorNeighbor(uid, outlet);

        var state = (comp.InletNeighbor, comp.OutletNeighbor) switch
        {
            (null, null) => RadiatorConnectionState.Isolated,
            (null, not null) => RadiatorConnectionState.CapIn,
            (not null, null) => RadiatorConnectionState.CapOut,
            _ => RadiatorConnectionState.Middle,
        };

        if (state == comp.ConnectionState)
            return;

        comp.ConnectionState = state;
        _appearance.SetData(uid, RadiatorVisuals.Connections, state);
    }

    private EntityUid? FindRadiatorNeighbor(EntityUid uid, PipeNode port)
    {
        foreach (var node in port.ReachableNodes)
        {
            if (node.Owner != uid && HasComp<HeatExchangerComponent>(node.Owner))
                return node.Owner;
        }

        return null;
    }

    private void OnAnchorChanged(EntityUid uid, HeatExchangerComponent comp, ref AnchorStateChangedEvent args)
    {
        if (args.Anchored)
            return; // links rebuild on the next atmos update

        comp.InletNeighbor = null;
        comp.OutletNeighbor = null;

        // Triad: an unanchored radiator stops exchanging entirely, so drop it to
        // the unlit bucket. The client fades the glow and its spill light out
        // from there; going through the bucket keeps that the single thermal
        // signal rather than reaching at the light directly. Done before the
        // connection-state early-out below, since an already-isolated fin can
        // still be glowing hot when it is pulled up.
        if (comp.Bucket != RadiatorThermalBucket.Neutral)
        {
            comp.Bucket = RadiatorThermalBucket.Neutral;
            _appearance.SetData(uid, RadiatorVisuals.Bucket, RadiatorThermalBucket.Neutral);
        }

        if (comp.ConnectionState == RadiatorConnectionState.Isolated)
            return;

        comp.ConnectionState = RadiatorConnectionState.Isolated;
        _appearance.SetData(uid, RadiatorVisuals.Connections, RadiatorConnectionState.Isolated);
    }

    /// <summary>
    /// Triad: Newton's-law contact transfer between the radiator body and
    /// mobs standing on it. Feeds body temperature (the existing temperature
    /// system provides the telegraphed warning ramp), never direct damage.
    /// </summary>
    private void ContactHazard(HeatExchangerComponent comp, float dt)
    {
        if (comp.Standing.Count == 0 || !IsHazardTemperature(comp))
            return;

        // Snapshot: standers can be deleted without a step-off event.
        foreach (var mob in comp.Standing.ToArray())
        {
            if (Deleted(mob) || !TryComp<TemperatureComponent>(mob, out var temp))
            {
                comp.Standing.Remove(mob);
                continue;
            }

            var dT = comp.BodyTemperature - temp.CurrentTemperature;
            var dE = comp.ContactConductance * dT * dt;
            _temperature.ChangeHeat(mob, dE, false, temp);
            comp.BodyTemperature -= dE / comp.BodyHeatCapacity;
        }
    }

    /// <summary>
    /// Triad: sustained white-hot operation slowly destroys the radiator
    /// (rupture via the normal pipe destruction path). Cvar-gated.
    /// </summary>
    private void OverheatDamage(EntityUid uid, HeatExchangerComponent comp, float dt)
    {
        if (!_overheatDamage || comp.Bucket < RadiatorThermalBucket.White)
            return;

        var damage = new DamageSpecifier(_proto.Index<DamageTypePrototype>("Structural"),
            comp.OverheatDamagePerSecond * dt);
        _damageable.TryChangeDamage(uid, damage, ignoreResistances: true);
    }

    /// <summary>
    /// Triad: recompute the thermal bucket with hysteresis; on change, update
    /// appearance and play the thermal-expansion creak.
    /// </summary>
    private void UpdateBucket(EntityUid uid, HeatExchangerComponent comp)
    {
        var current = comp.Bucket;
        // To rise, the temperature minus the deadband must still clear the
        // boundary; to fall, plus the deadband must still be under it.
        var up = ComputeBucket(comp.BodyTemperature - BucketHysteresis);
        var down = ComputeBucket(comp.BodyTemperature + BucketHysteresis);
        var next = current;
        if (up > current)
            next = up;
        else if (down < current)
            next = down;

        if (next == current)
            return;

        comp.Bucket = next;
        _appearance.SetData(uid, RadiatorVisuals.Bucket, next);

        SetGlowLight(uid, next);

        // Triad: the creak only ever fires here, on a bucket change, never per
        // tick. Rolled to one in four on top of that: a long array crossing a
        // boundary together would otherwise creak in unison, and sparse ticking
        // reads as metal settling rather than a chorus.
        // Play with the specifier's own params (volume + pitch variation), so a
        // prototype can retune the creak in YAML. Passing an override here would
        // discard whatever the datafield carries.
        if (comp.CreakSound != null && _random.Prob(CreakChance))
            _audio.PlayPvs(comp.CreakSound, uid);
    }

    /// <summary>
    /// Triad: write the bucket's resting light values. This is what keeps a hot
    /// radiator lit: <c>PointLight</c> is networked and server-authoritative, so
    /// if only the client set it, the next state application would revert it to
    /// the prototype's disabled default the moment a fade finished. The client
    /// interpolates on top of these during a transition; these are where it
    /// lands. Only ever called on a bucket change, so nothing dirties per tick.
    /// </summary>
    private void SetGlowLight(EntityUid uid, RadiatorThermalBucket bucket)
    {
        if (!_pointLight.TryGetLight(uid, out var light))
            return;

        if (!RadiatorGlowRamp.IsLit(bucket))
        {
            _pointLight.SetEnabled(uid, false, light);
            return;
        }

        var (tint, energy, radius) = RadiatorGlowRamp.Get(bucket);
        _pointLight.SetColor(uid, RadiatorGlowRamp.ToLightColor(tint), light);
        _pointLight.SetEnergy(uid, energy, light);
        _pointLight.SetRadius(uid, radius, light);
        _pointLight.SetEnabled(uid, true, light);
    }

    private static RadiatorThermalBucket ComputeBucket(float temperature)
    {
        byte bucket = 0;
        foreach (var boundary in BucketBoundaries)
        {
            if (temperature < boundary)
                break;
            bucket++;
        }
        return (RadiatorThermalBucket)bucket;
    }

    /// <summary>
    /// Triad: Newton's-law heat exchange between a pipe net's gas and the
    /// radiator body. Closed-form exponential, never overshoots.
    /// </summary>
    private void ExchangeGasWithBody(HeatExchangerComponent comp, GasMixture gas, float conductance, float dt)
    {
        var cGas = _atmosphereSystem.GetHeatCapacity(gas, true);
        if (cGas < Atmospherics.MinimumHeatCapacity || comp.BodyHeatCapacity <= 0f)
            return;

        float TdivQ = 1f / cGas + 1f / comp.BodyHeatCapacity;
        float dT = gas.Temperature - comp.BodyTemperature;
        float dT2 = dT * MathF.Exp(-conductance * TdivQ * dt);
        float dE = (dT - dT2) / TdivQ;
        _atmosphereSystem.AddHeat(gas, -dE);
        comp.BodyTemperature += dE / comp.BodyHeatCapacity;
    }

    /// <summary>
    /// Triad: Newton's-law body-to-body exchange across one port link.
    /// The lower uid owns the pair so each link settles exactly once per
    /// update. Closed-form exponential, never overshoots, strictly conserves.
    /// </summary>
    private void ExchangeBodyWithNeighbor(EntityUid uid, HeatExchangerComponent comp, EntityUid? neighborUid, float dt)
    {
        if (neighborUid is not { } other || uid.CompareTo(other) >= 0)
            return;

        if (!TryComp<HeatExchangerComponent>(other, out var otherComp))
            return;

        if (comp.BodyHeatCapacity <= 0f || otherComp.BodyHeatCapacity <= 0f)
            return;

        var conductance = MathF.Min(comp.BodyLinkConductance, otherComp.BodyLinkConductance);
        if (conductance <= 0f)
            return;

        float TdivQ = 1f / comp.BodyHeatCapacity + 1f / otherComp.BodyHeatCapacity;
        float dT = comp.BodyTemperature - otherComp.BodyTemperature;
        float dT2 = dT * MathF.Exp(-conductance * TdivQ * dt);
        float dE = (dT - dT2) / TdivQ;
        comp.BodyTemperature -= dE / comp.BodyHeatCapacity;
        otherComp.BodyTemperature += dE / otherComp.BodyHeatCapacity;
    }
}

using Content.Server.Atmos.EntitySystems;
using Content.Shared._FarHorizons.Materials.Systems;
using Content.Shared._FarHorizons.Power.Generation.FissionGenerator;
using Content.Shared.Atmos;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._FarHorizons.Power.Generation.FissionGenerator;

// Ported and modified from goonstation by Jhrushbe.
// CC-BY-NC-SA-3.0
// https://github.com/goonstation/goonstation/blob/ff86b044/code/obj/nuclearreactor/reactorcomponents.dm

public sealed partial class ReactorPartSystem : SharedReactorPartSystem
{
    [Dependency] private AtmosphereSystem _atmosphereSystem = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IRobustRandom _random = default!;

    /// <summary>
    /// Processing multiplier based on atmospherics time and speedup cvar
    /// </summary>
    public float ProcMult => _atmosphereSystem.AtmosTime * _atmosphereSystem.Speedup * 6; // The 6 is a magic number to make things work at a reasonable rate

    /// <summary>
    /// Processes gas flowing through a reactor part.
    /// </summary>
    /// <param name="reactorPart">The reactor part.</param>
    /// <param name="reactor">The entity representing the reactor this part is inserted into.</param>
    /// <param name="inGas">The gas to be processed.</param>
    /// <returns></returns>
    public GasMixture? ProcessGas(ReactorPartComponent reactorPart, EntityUid reactor, GasMixture inGas)
    {
        if (!reactorPart.HasRodType(ReactorPartComponent.RodTypes.GasChannel))
            return null;

        GasMixture? ProcessedGas = null;
        if (reactorPart.AirContents != null)
        {
            var compTemp = reactorPart.Temperature;
            var gasTemp = reactorPart.AirContents.Temperature;

            var DeltaT = compTemp - gasTemp;
            var DeltaTr = (compTemp + gasTemp) * (compTemp - gasTemp) * (Math.Pow(compTemp, 2) + Math.Pow(gasTemp, 2));

            var k = MaterialSystem.CalculateHeatTransferCoefficient(reactorPart.Properties, null);
            var A = reactorPart.GasThermalCrossSection * ProcMult;

            var ThermalEnergy = _atmosphereSystem.GetThermalEnergy(reactorPart.AirContents);

            var Hottest = Math.Max(gasTemp, compTemp);
            var Coldest = Math.Min(gasTemp, compTemp);

            var MaxDeltaE = Math.Clamp((k * A * DeltaT) + (5.67037442e-8 * A * DeltaTr),
                (compTemp * reactorPart.ThermalMass) - (Hottest * reactorPart.ThermalMass),
                (compTemp * reactorPart.ThermalMass) - (Coldest * reactorPart.ThermalMass));

            reactorPart.AirContents.Temperature = (float)Math.Clamp(gasTemp +
                (MaxDeltaE / _atmosphereSystem.GetHeatCapacity(reactorPart.AirContents, true)), Coldest, Hottest);

            reactorPart.Temperature = (float)Math.Clamp(compTemp -
                ((_atmosphereSystem.GetThermalEnergy(reactorPart.AirContents) - ThermalEnergy) / reactorPart.ThermalMass), Coldest, Hottest);

            if (gasTemp < 0 || compTemp < 0)
                throw new Exception("Reactor part temperature went below 0k.");

            if (reactorPart.Melted)
            {
                var T = _atmosphereSystem.GetTileMixture(reactor, excite: true);
                if (T != null)
                    _atmosphereSystem.Merge(T, reactorPart.AirContents);
            }
            else
                ProcessedGas = reactorPart.AirContents;
        }

        if (inGas != null && _atmosphereSystem.GetThermalEnergy(inGas) > 0)
        {
            reactorPart.AirContents = inGas.RemoveVolume(reactorPart.GasVolume);
            reactorPart.AirContents.Volume = reactorPart.GasVolume;

            if (reactorPart.AirContents != null && reactorPart.AirContents.TotalMoles < 1)
            {
                if (ProcessedGas != null)
                {
                    _atmosphereSystem.Merge(ProcessedGas, reactorPart.AirContents);
                    reactorPart.AirContents.Clear();
                }
                else
                {
                    ProcessedGas = reactorPart.AirContents;
                    reactorPart.AirContents.Clear();
                }
            }
        }
        return ProcessedGas;
    }

    /// <summary>
    /// Processes heat transfer within the reactor grid.
    /// </summary>
    /// <param name="reactorPart">Reactor part applying the calculations.</param>
    /// <param name="reactorEnt">Reactor housing the reactor part.</param>
    /// <param name="AdjacentComponents">List of reactor parts next to the reactorPart.</param>
    /// <param name="reactorSystem">The SharedNuclearReactorSystem.</param>
    /// <exception cref="Exception">Calculations resulted in a sub-zero value.</exception>
    public void ProcessHeat(ReactorPartComponent reactorPart, Entity<NuclearReactorComponent> reactorEnt, ReactorPartComponent?[] AdjacentComponents, NuclearReactorSystem reactorSystem)
    {
        var reactor = reactorEnt.Comp;

        // Intercomponent calculation
        foreach (var RC in AdjacentComponents)
        {
            if (RC == null)
                continue;

            var DeltaT = reactorPart.Temperature - RC.Temperature;
            var k = MaterialSystem.CalculateHeatTransferCoefficient(reactorPart.Properties, RC.Properties);
            var A = Math.Min(reactorPart.ThermalCrossSection, RC.ThermalCrossSection);

            reactorPart.Temperature = (float)(reactorPart.Temperature - (k * A * ProcMult / reactorPart.ThermalMass * DeltaT));
            RC.Temperature = (float)(RC.Temperature - (k * A * ProcMult / RC.ThermalMass * -DeltaT));

            if (RC.Temperature < 0 || reactorPart.Temperature < 0)
                throw new Exception("ReactorPart-ReactorPart temperature calculation resulted in sub-zero value.");

            ProcessHeatEffects(RC);
            ProcessHeatEffects(reactorPart);
        }

        // Component-Reactor calculation
        if (reactor != null)
        {
            var DeltaT = reactorPart.Temperature - reactor.Temperature;

            var k = MaterialSystem.CalculateHeatTransferCoefficient(reactorPart.Properties, _proto.Index(reactor.Material).Properties);
            var A = reactorPart.ThermalCrossSection;

            reactorPart.Temperature = (float)(reactorPart.Temperature - (k * A * ProcMult / reactorPart.ThermalMass * DeltaT));
            reactor.Temperature = (float)(reactor.Temperature - (k * A * ProcMult / reactor.ThermalMass * -DeltaT));

            if (reactor.Temperature < 0 || reactorPart.Temperature < 0)
                throw new Exception("Reactor-ReactorPart temperature calculation resulted in sub-zero value.");

            ProcessHeatEffects(reactorPart);
        }
        if (reactorPart.Temperature > reactorPart.MeltingPoint && reactorPart.MeltHealth > 0)
            reactorPart.MeltHealth -= _random.Next(10, 50 + 1);
        if (reactorPart.MeltHealth <= 0)
            Melt(reactorPart, reactorEnt, reactorSystem);

        return;

        // I would really like for these to be defined by the MaterialPrototype, like GasReactionPrototype, but it caused the client and server to fight when I tried
        // Also, function in a function because I found it funny
        void ProcessHeatEffects(ReactorPartComponent part)
        {
            switch (part.Material)
            {
                case "Plasma":
                    PlasmaTemperatureEffects(part);
                    break;
                default:
                    break;
            }
        }

        void PlasmaTemperatureEffects(ReactorPartComponent part)
        {
            var temperatureThreshold = Atmospherics.T0C + 80;
            if (part.Temperature <= temperatureThreshold || part.Properties.ActivePlasma <= 0)
                return;

            var molesPerUnit = 100f; // Arbitrary value for how much gaseous plasma is in each unit of active plasma

            var payload = new GasMixture();
            payload.SetMoles(Gas.Plasma, (float)Math.Min(part.Properties.ActivePlasma * molesPerUnit, Math.Log(((part.Temperature - temperatureThreshold) / 100) + 1)));
            payload.Temperature = part.Temperature;
            part.Properties.ActivePlasma -= payload.GetMoles(Gas.Plasma) / molesPerUnit;

            reactor.AirContents ??= new GasMixture();
            _atmosphereSystem.Merge(reactor.AirContents, payload);
        }
    }

    /// <summary>
    /// Melts the related ReactorPart.
    /// </summary>
    /// <param name="reactorPart">Reactor part to be melted</param>
    /// <param name="reactorEnt">Reactor housing the reactor part</param>
    /// <param name="reactorSystem">The SharedNuclearReactorSystem</param>
    public void Melt(ReactorPartComponent reactorPart, Entity<NuclearReactorComponent> reactorEnt, NuclearReactorSystem reactorSystem)
    {
        if (reactorPart.Melted)
            return;

        reactorPart.Melted = true;
        reactorPart.IconStateCap += "_melted_" + _random.Next(1, 4 + 1);
        reactorSystem.UpdateGridVisual(reactorEnt);
        reactorPart.NeutronCrossSection = 5f;
        reactorPart.ThermalCrossSection = 20f;
        reactorPart.IsControlRod = false;

        if(reactorPart.HasRodType(ReactorPartComponent.RodTypes.GasChannel))
            reactorPart.GasThermalCrossSection = 0.1f;
    }

    /// <summary>
    /// Returns a list of neutrons from the interation of the given ReactorPart and initial neutrons.
    /// </summary>
    /// <param name="reactorPart">Reactor part applying the calculations.</param>
    /// <param name="neutrons">List of neutrons to be processed.</param>
    /// <param name="thermalEnergy">Thermal energy released from the process.</param>
    /// <returns>Post-processing list of neutrons.</returns>
    public List<ReactorNeutron> ProcessNeutrons(ReactorPartComponent reactorPart, List<ReactorNeutron> neutrons, out float thermalEnergy)
    {
        var preCalcTemp = reactorPart.Temperature;
        var result = new List<ReactorNeutron>(neutrons.Count); // Avoid Remove: build new list

        foreach (var neutron in neutrons)
        {
            if (Prob(reactorPart.Properties.Density * ReactionRate * reactorPart.NeutronCrossSection * NeutronReactionBias))
            {
                if (neutron.velocity <= 4 && Prob(ReactionRate * reactorPart.Properties.NeutronRadioactivity * NeutronReactionBias * 2.0f)) // neutron stimulated emission, requires thermal neutrons.
                {
                    reactorPart.Properties.NeutronRadioactivity -= ReactionReactant;
                    reactorPart.Properties.Radioactivity += ReactionProduct * 1.25f; //Triad- N fuel makes a lot of R-fuel. (125%)
                    reactorPart.Properties.Fertility += ReactionProduct * 0.2f; //Triad- some of the N-fuel is made into fertility. (20%)
                    reactorPart.Properties.FissileIsotopes += ReactionProduct * 0.1f; //Triad- some of the N-fuel is made into plutonium. (10%)
                    reactorPart.Temperature += 35f * StimulatedHeatingFactor; //Triad- a bit less heat per fission, to help things be more thermally controllable.
                    for (var i = 0; i < _random.Next(4, 6 + 1); i++) //Triad- made N-fuel fission produce 4-6 neutrons.
                    {
                        result.Add(new() { dir = _random.NextAngle().GetDir(), velocity = _random.Next(5, 7 + 1) }); //Triad- made N-fuel fission produce very fast neutrons.
                    }
                }
                else if (neutron.velocity <= 5 && Prob(ReactionRate * reactorPart.Properties.Radioactivity * NeutronReactionBias)) // stimulated emission, requires mostly thermal neutrons.
                {
                    reactorPart.Properties.Fertility += ReactionProduct * 0.2f; //Triad- some of the R-fuel is made into fertility. (20%)
                    reactorPart.Properties.NeutronRadioactivity += ReactionProduct * 0.1f; //Triad- some of the R-fuel is made into N-fuel (capture). (10%)
                    reactorPart.Properties.Radioactivity -= ReactionReactant * 1.5f; //Triad- R-fuel is burned off quickly by fission.
                    reactorPart.Temperature += 35f * StimulatedHeatingFactor; //Triad- less heat.
                    for (var i = 0; i < _random.Next(1, 3 + 1); i++) //Triad- made R-fuel fission produce less neutrons, 1-3
                    {
                        result.Add(new() { dir = _random.NextAngle().GetDir(), velocity = _random.Next(2, 4 + 1) }); //Triad- made R-fuel fission produce slower neutrons
                    }

                } //Triad Start - Fast Capture.
                else if (neutron.velocity >= 4 && Prob(ReactionRate * reactorPart.Properties.Fertility * 0.5f * NeutronReactionBias)) // fast capture; makes no neutrons, but makes heat, plutonium, and needs fast neutrons. Can have a higher multiplier, since it happens *after* all other fission checks
                {
                    reactorPart.Properties.Fertility -= ReactionReactant * 1.5f;
                    reactorPart.Properties.NeutronRadioactivity += ReactionProduct * 0.2f; //some of the capture makes into N-fuel. (20%)
                    reactorPart.Properties.Radioactivity += ReactionProduct * 0.1f; //some of the capture is made into R-fuel. (10%)
                    reactorPart.Properties.FissileIsotopes += ReactionProduct * 1.5f; //some of the capture is made into plutonium.
                    reactorPart.Temperature += 20f * StimulatedHeatingFactor; //less heat than actual fissions.
                } //Triad End
                else
                {
                    if (Prob(ReactionRate * reactorPart.Properties.Hardness)) // reflection, based on hardness
                        // A really complicated way of saying do a 180 or a 180+/-45
                        neutron.dir = (neutron.dir.GetOpposite().ToAngle() + (_random.NextAngle() / 4) - (MathF.Tau / 8)).GetDir();
                    else if (reactorPart.IsControlRod)
                        neutron.velocity = 0;
                    else
                        neutron.velocity--;

                    if (neutron.velocity > 0)
                        result.Add(neutron);

                    reactorPart.Temperature += 0.5f * StimulatedHeatingFactor; //Triad- made non-fission neutron interactions produce only a little bit of heat.
                }
            }
            else
            {
                result.Add(neutron);
            }
        }
        if (Prob(ReactionRate * ((2.0f * reactorPart.Properties.NeutronRadioactivity - 1.5f) / (0.15f * reactorPart.Properties.NeutronRadioactivity + 1.5f) + 1f))) // Triad- made N-decay events less affected by very low fuel, resulting in more consistentent decay rates, even at low fuel.
        {
            reactorPart.Properties.NeutronRadioactivity -= ReactionReactant * SpontaneousReactionConsumptionMultiplier * 0.5f; // Triad- neturon decay consumes less N-fuel (50%)
            reactorPart.Properties.Radioactivity += ReactionProduct * SpontaneousReactionConsumptionMultiplier * 0.5f; // Triad- neturon decay makes more R-fuel (100%, per N-fuel)
            reactorPart.Properties.Fertility += ReactionProduct * SpontaneousReactionConsumptionMultiplier * 0.1f; // Triad- neturon decay makes some fertility (20%)
            reactorPart.Temperature += 10f * SpontaneousHeatingFactor; //Triad- decreased heat output of N-fuel decay (less consumed)
            for (var i = 0; i < _random.Next(2, 4 + 1); i++) //Triad- produces 2-4 neutrons
            {
                result.Add(new() { dir = _random.NextAngle().GetDir(), velocity = _random.Next(4, 6 + 1) }); //Triad- made N-decay events produce non-discrete neutrons that can be fast- 4-6.
            }
        }
        if (Prob(ReactionRate * ((4f * reactorPart.Properties.Radioactivity - 2f) / (0.5f * reactorPart.Properties.Radioactivity + 2f) + 1f))) // Triad- made R-decay events happen much slower at lower levels. If you want R-fuel to go down faster, expose it to neutrons to fission it away. R-Fuel lasts much longer on its own now
        {
            reactorPart.Properties.Radioactivity -= ReactionReactant * SpontaneousReactionConsumptionMultiplier * 0.25f; //Triad- made radioactive decay consume much less R-fuel.
            reactorPart.Properties.FissileIsotopes += ReactionProduct * SpontaneousReactionConsumptionMultiplier * 0.05f; // Triad- made radioactive decay produce very little plutonium- but still *some*.
            reactorPart.Properties.Fertility += ReactionProduct * SpontaneousReactionConsumptionMultiplier * 0.1f; // Triad- made radioactive decay produce a little fertile isotopes- but still *some*, to represent heavier isotopes like neptunium.
            reactorPart.Temperature += 5f * SpontaneousHeatingFactor; //Triad- Made decay produce only a little bit of heat (less than it seems, makes very few neutrons).
            if (_random.NextDouble() >= 0.8) //Triad- 20% chance of producing one neutron- very low rate.
            {
                result.Add(new() { dir = _random.NextAngle().GetDir(), velocity = 3 }); //Triad- made R-decay events produce relatively slow neutrons
            }
        }

        if (reactorPart.HasRodType(ReactorPartComponent.RodTypes.ControlRod))
        {
            if (!reactorPart.Melted && (reactorPart.NeutronCrossSection != reactorPart.ConfiguredInsertionLevel))
            {
                if (reactorPart.ConfiguredInsertionLevel < reactorPart.NeutronCrossSection)
                    reactorPart.NeutronCrossSection -= Math.Min(0.1f, reactorPart.NeutronCrossSection - reactorPart.ConfiguredInsertionLevel);
                else
                    reactorPart.NeutronCrossSection += Math.Min(0.1f, reactorPart.ConfiguredInsertionLevel - reactorPart.NeutronCrossSection);
            }
        }

        if (reactorPart.HasRodType(ReactorPartComponent.RodTypes.GasChannel))
            result = ProcessNeutronsGas(reactorPart, result);

        thermalEnergy = (reactorPart.Temperature - preCalcTemp) * reactorPart.ThermalMass;
        return result;
    }

    /// <summary>
    /// Processes neutrons interacting with gas in a reactor part.
    /// </summary>
    /// <param name="reactorPart">The reactor part to process neutrons for.</param>
    /// <param name="neutrons">The list of neutrons to process.</param>
    /// <returns>The updated list of neutrons after processing.</returns>
    private List<ReactorNeutron> ProcessNeutronsGas(ReactorPartComponent reactorPart, List<ReactorNeutron> neutrons)
    {
        if (reactorPart.AirContents == null) return neutrons;

        var result = new List<ReactorNeutron>(neutrons.Count + 8);
        foreach (var neutron in neutrons)
        {
            if (neutron.velocity <= 0)
                continue;

            var neutronCount = GasNeutronInteract(reactorPart);
            if (neutronCount > 1)
            {
                for (var i = 0; i < neutronCount; i++)
                    result.Add(new() { dir = _random.NextAngle().GetDir(), velocity = _random.Next(1, 3 + 1) });
            }
            else if (neutronCount >= 1)
            {
                result.Add(neutron);
            }
        }

        return result;
    }

    /// <summary>
    /// Performs neutron interactions with the gas in the reactor part.
    /// </summary>
    /// <param name="reactorPart">The reactor part to process neutron interactions for.</param>
    /// <returns>Change in number of neutrons.</returns>
    private int GasNeutronInteract(ReactorPartComponent reactorPart)
    {
        if (reactorPart.AirContents == null)
            return 1;

        var neutronCount = 1;
        var gas = reactorPart.AirContents;
        //Triad: Reordered: process co2 before the moderator gasses, so it can actually, y'know. Reduce the neutron levels.
        if (gas.GetMoles(Gas.CarbonDioxide) > 1)
        {
            var reactMolPerLiter = 0.4;
            var reactMol = reactMolPerLiter * gas.Volume;

            var co2 = gas.GetMoles(Gas.CarbonDioxide);
            var co2ReactCount = (int)Math.Round((co2 - (co2 % reactMol)) / reactMol) + (Prob(co2 - (co2 % reactMol)) ? 1 : 0);
            co2ReactCount = _random.Next(0, co2ReactCount + 1);
            reactorPart.Temperature += Math.Min(co2ReactCount, neutronCount);
            neutronCount -= Math.Min(co2ReactCount, neutronCount);
        }

        if (gas.GetMoles(Gas.Tritium) > 1) //Process trit before plasma, since plasma... produces trit.
        {
            var reactMolPerLiter = 0.5;
            var reactMol = reactMolPerLiter * gas.Volume;

            var tritium = gas.GetMoles(Gas.Tritium);
            var tritiumReactCount = (int)Math.Round((tritium - (tritium % reactMol)) / reactMol) + (Prob(tritium - (tritium % reactMol)) ? 1 : 0);
            tritiumReactCount = _random.Next(0, tritiumReactCount + 1);
            if (tritiumReactCount > 0)
            {
                gas.AdjustMoles(Gas.Tritium, -1 * tritiumReactCount);
                reactorPart.Temperature += 1 * tritiumReactCount;
                switch (_random.Next(0, 5))
                {
                    case 0:
                        gas.AdjustMoles(Gas.Oxygen, 0.5f * tritiumReactCount);
                        break;
                    case 1:
                        gas.AdjustMoles(Gas.Nitrogen, 0.5f * tritiumReactCount);
                        break;
                    case 2:
                        gas.AdjustMoles(Gas.Ammonia, 0.1f * tritiumReactCount);
                        break;
                    case 3:
                        gas.AdjustMoles(Gas.NitrousOxide, 0.1f * tritiumReactCount);
                        break;
                    case 4:
                        gas.AdjustMoles(Gas.Frezon, 0.1f * tritiumReactCount);
                        break;
                    default:
                        break;
                }
            }
        }

        if (gas.GetMoles(Gas.Plasma) > 1) //process plasma last, so it doesn't eat up neutrons before co2, and doesn't have its tritium get eated.
        {
            var reactMolPerLiter = 0.25;
            var reactMol = reactMolPerLiter * gas.Volume;

            var plasma = gas.GetMoles(Gas.Plasma);
            var plasmaReactCount = (int)Math.Round((plasma - (plasma % reactMol)) / reactMol) + (Prob(plasma - (plasma % reactMol)) ? 1 : 0);
            plasmaReactCount = _random.Next(0, plasmaReactCount + 1);
            gas.AdjustMoles(Gas.Plasma, plasmaReactCount * -0.5f);
            gas.AdjustMoles(Gas.Tritium, plasmaReactCount * 2);
            neutronCount += plasmaReactCount;
        }
        //End Triad

        return neutronCount;
    }

    /// <summary>
    /// Probablity check that accepts chances > 100%
    /// </summary>
    /// <param name="chance">The chance percentage between 0 and 100.</param>
    private bool Prob(double chance) => _random.NextDouble() <= chance / 100;
}

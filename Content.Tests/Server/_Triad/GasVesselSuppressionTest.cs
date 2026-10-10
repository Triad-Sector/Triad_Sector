using Content.Server._Triad.Atmos.EntitySystems;
using Content.Shared.Atmos;
using NUnit.Framework;

namespace Content.Tests.Server._Triad;

[TestFixture, TestOf(typeof(GasVesselSuppressionSystem))]
[Parallelizable(ParallelScope.All)]
public sealed class GasVesselSuppressionTest
{
    private static GasMixture Mix(float temperature, params (Gas Gas, float Moles)[] contents)
    {
        var mixture = new GasMixture(5f);
        foreach (var (gas, moles) in contents)
        {
            mixture.SetMoles(gas, moles);
        }

        mixture.Temperature = temperature;
        return mixture;
    }

    [Test]
    public void HotFlammableMixTripsTheGate()
    {
        // The classic maxcap fuel mix: ~50/50 plasma/tritium held just above plasma ignition temperature.
        var fuel = Mix(383f, (Gas.Plasma, 100f), (Gas.Tritium, 100f));
        Assert.That(GasVesselSuppressionSystem.NeedsSuppression(fuel), Is.True);
    }

    [Test]
    public void ColdFlammableMixDoesNotTrip()
    {
        var storage = Mix(293.15f, (Gas.Plasma, 500f));
        Assert.That(GasVesselSuppressionSystem.NeedsSuppression(storage), Is.False);
    }

    [Test]
    public void HotInertCargoDoesNotTrip()
    {
        // A pure oxygen canister cooked by an external fire must survive: it cannot burn on its own.
        var cargo = Mix(600f, (Gas.Oxygen, 1000f));
        Assert.That(GasVesselSuppressionSystem.NeedsSuppression(cargo), Is.False);
    }

    [Test]
    public void TraceFlammablesDoNotTrip()
    {
        var contaminated = Mix(600f, (Gas.Oxygen, 1000f), (Gas.Plasma, 0.05f));
        Assert.That(GasVesselSuppressionSystem.NeedsSuppression(contaminated), Is.False);
    }

    [Test]
    public void GateOpensJustBelowIgnition()
    {
        var threshold = Atmospherics.PlasmaMinimumBurnTemperature
            * GasVesselSuppressionSystem.SuppressionTemperatureFraction;

        var below = Mix(threshold - 1f, (Gas.Plasma, 100f));
        var above = Mix(threshold + 1f, (Gas.Plasma, 100f));

        Assert.Multiple(() =>
        {
            Assert.That(GasVesselSuppressionSystem.NeedsSuppression(below), Is.False);
            Assert.That(GasVesselSuppressionSystem.NeedsSuppression(above), Is.True);
            Assert.That(threshold, Is.LessThan(Atmospherics.PlasmaMinimumBurnTemperature),
                "suppression must fire before a plasma fire can exist inside the vessel");
        });
    }

    [Test]
    public void SuppressionConservesMolesAndLowersPressure()
    {
        var fuel = Mix(383f, (Gas.Plasma, 100f), (Gas.Tritium, 100f));
        var molesBefore = fuel.TotalMoles;
        var pressureBefore = fuel.Pressure;

        GasVesselSuppressionSystem.SuppressMixture(fuel);

        Assert.Multiple(() =>
        {
            Assert.That(fuel.TotalMoles, Is.EqualTo(molesBefore).Within(0.001f));
            Assert.That(fuel.GetMoles(Gas.WaterVapor), Is.EqualTo(molesBefore).Within(0.001f));
            Assert.That(fuel.GetMoles(Gas.Plasma), Is.Zero);
            Assert.That(fuel.GetMoles(Gas.Tritium), Is.Zero);
            Assert.That(fuel.Temperature, Is.EqualTo(Atmospherics.T20C).Within(0.001f));
            Assert.That(fuel.Pressure, Is.LessThan(pressureBefore));
            Assert.That(GasVesselSuppressionSystem.NeedsSuppression(fuel), Is.False,
                "a suppressed mixture must never re-trip the gate");
        });
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Random.Helpers;
using NUnit.Framework;

namespace Content.Tests.Shared._Triad;

// The weighted picks in SharedRandomExtensions walk a running float total and return the first entry
// whose total reaches the roll. That loop could select nothing and fall through to a throw, which on
// prod escaped through BiomeSystem's chunk loader once in fourteen days and, because that loader was
// not exception safe, killed biome chunk loading for the rest of the round.
//
// Two independent float facts combine to get there:
//   - Enumerable.Sum over floats accumulates in a double and casts back, while the loop's total is a
//     running float, so for fractional weights the running total can finish just BELOW the sum it is
//     being compared against.
//   - NextFloatValue() can return exactly 1.0f, which lands the roll exactly on that sum.
//
// Clamping the roll below 1.0 does not close the gap, so the fall-through itself has to be handled.
[TestFixture]
[TestOf(typeof(SharedRandomExtensions))]
public sealed class WeightedPickFallThroughTest
{
    /// <summary>
    /// Always returns the largest value <see cref="Random.Next()"/> can produce. int.MaxValue - 1
    /// needs more significant bits than a float mantissa holds, so it widens UP to 2^31 and cancels
    /// NextFloatValue's 2^-31 multiplier exactly, making the roll land on the sum.
    /// </summary>
    private sealed class TopDrawRandom : Random
    {
        public override int Next() => int.MaxValue - 1;
    }

    /// <summary>
    /// MonoPlanetmapOreSand's child weights, copied from Prototypes/_Mono/Planets/ore.yml. This is
    /// the table family the prod failure came out of, reached via BiomeSystem's entity spawning.
    /// </summary>
    private static readonly float[] OreSandWeights =
        { 0.67f, 0.04f, 0.12f, 0.042f, 0.025f, 0.025f, 0.0075f, 0.01f };

    private static Dictionary<string, float> OreSandTable()
    {
        var table = new Dictionary<string, float>();

        for (var i = 0; i < OreSandWeights.Length; i++)
        {
            table.Add($"entry{i}", OreSandWeights[i]);
        }

        return table;
    }

    [Test]
    public void NextFloatValueIsClosedAtOne()
    {
        // NextFloatValue's remarks document a closed interval. If this ever starts coming back below
        // 1, the endpoint half of the bug is gone and only the Sum() shortfall remains.
        Assert.That(new TopDrawRandom().NextFloatValue(), Is.EqualTo(1.0f));
    }

    [Test]
    public void RunningFloatTotalFallsShortOfReportedSum()
    {
        // The precondition, asserted against shipped weights rather than assumed. Sum() accumulates
        // in a double; the pick loop does not.
        var reported = OreSandWeights.Sum();

        var running = 0f;
        foreach (var weight in OreSandWeights)
        {
            running += weight;
        }

        Assert.That(running, Is.LessThan(reported),
            "the running float total is expected to finish below Sum()'s value for these weights");
    }

    [Test]
    public void TopDrawPicksLastEntryInsteadOfThrowing()
    {
        // The regression. Before the fall-through was handled this threw InvalidOperationException
        // out of GroupSelector, through map init, and into the biome chunk loader.
        var table = OreSandTable();

        var pick = SharedRandomExtensions.Pick(table, new TopDrawRandom());

        Assert.That(pick, Is.EqualTo($"entry{OreSandWeights.Length - 1}"));
    }

    [Test]
    public void EmptyTableStillThrows()
    {
        // A table with nothing in it has no answer to give, so this one keeps throwing.
        var table = new Dictionary<string, float>();

        Assert.That(() => SharedRandomExtensions.Pick(table, new TopDrawRandom()),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void OrdinaryDrawsStayInsideTheTable()
    {
        // Control: the happy path is unchanged and every draw is a real key.
        var table = OreSandTable();
        var random = new Random(1234);

        for (var i = 0; i < 10_000; i++)
        {
            Assert.That(table.ContainsKey(SharedRandomExtensions.Pick(table, random)));
        }
    }

    [Test]
    public void EveryEntryIsReachable()
    {
        // Control: guarding the tail must not collapse the distribution onto one entry.
        var table = OreSandTable();
        var random = new Random(5678);
        var seen = new HashSet<string>();

        for (var i = 0; i < 200_000; i++)
        {
            seen.Add(SharedRandomExtensions.Pick(table, random));
        }

        Assert.That(seen, Is.EquivalentTo(table.Keys));
    }
}

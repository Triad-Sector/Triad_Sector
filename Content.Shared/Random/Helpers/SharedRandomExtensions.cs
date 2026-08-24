using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared.Dataset;
using Content.Shared.FixedPoint;
using Robust.Shared.Random;

namespace Content.Shared.Random.Helpers
{
    public static class SharedRandomExtensions
    {
        // Triad: replacement for the engine's obsolete System.Random.NextFloat() extension.
        // The obsoletion asks callers to move to IRobustRandom, but these call sites need a
        // standalone seeded stream (salvage missions, dungeon gen, worldgen debris, parallax,
        // tile variants) and are pinned to System.Random by signatures such as
        // NumberSelector.Get, IWeightedRandomPrototype.Pick and TileSystem.PickVariant.
        // TileSystem itself unwraps IRobustRandom via GetRandom() to feed them, so migrating
        // the signatures would fight the design rather than follow it.
        //
        // The derivation below is the engine's, verbatim, so seeded sequences are bit-for-bit
        // unchanged. Named distinctly because the engine extension stays in scope wherever
        // Robust.Shared.Random is imported, and a matching signature would be ambiguous.
        // Only for a System.Random receiver: an IRobustRandom has NextFloat() of its own.

        /// <summary>
        /// Returns a random float in [0, 1], matching the engine's obsolete
        /// <c>System.Random.NextFloat()</c> derivation exactly.
        /// </summary>
        /// <remarks>
        /// Triad: the interval is CLOSED at 1.0, despite what the engine's name suggests and what
        /// this summary used to claim. <c>Next()</c> tops out at <c>int.MaxValue - 1</c>, which needs
        /// more significant bits than a float mantissa holds and therefore widens UP to 2^31, while
        /// the multiplier rounds to exactly 2^-31, so the product is exactly 1.0f for the top 63
        /// draws (about 2.9e-8 of them). Callers that scale by a count and index, or that walk a
        /// running total, have to tolerate landing on the endpoint. The arithmetic is deliberately
        /// left alone: it is the engine's, verbatim, and seeded sequences depend on it.
        /// </remarks>
        public static float NextFloatValue(this System.Random random)
        {
            return random.Next() * 4.6566128752458E-10f;
        }

        /// <summary>
        /// Returns a random float in [<paramref name="minValue"/>, <paramref name="maxValue"/>],
        /// matching the engine's obsolete <c>System.Random.NextFloat(float, float)</c> derivation.
        /// </summary>
        /// <remarks>
        /// Triad: closed at <paramref name="maxValue"/> for the same reason as the overload above,
        /// so a caller flooring this to get an integer in a range can see one past the top of it.
        /// </remarks>
        public static float NextFloatValue(this System.Random random, float minValue, float maxValue)
        {
            return random.NextFloatValue() * (maxValue - minValue) + minValue;
        }

        // Triad: every weighted pick in this file walks a running float total and returns the first
        // entry whose total reaches `rand`. That loop can finish having selected NOTHING, and each
        // fall-through used to throw. Two independent float facts get it there:
        //
        //   - `Sum()` over floats accumulates in a double and casts back, while `accumulated` is a
        //     running float, so for fractional weights the running total can finish just BELOW the
        //     reported sum. 16 of the 295 weight lists in Prototypes do exactly that, among them the
        //     planet ore and fauna tables.
        //   - `rand` is itself a float multiply, so it can round UP onto or past that shortfall.
        //     NextFloatValue() can also return exactly 1.0f (see its remarks), landing `rand`
        //     squarely on the sum.
        //
        // Clamping the roll below 1.0 does not close this: 3 of those 16 lists still fall through at
        // the largest float below 1.0, so the tail has to be handled here. Measured on prod at one
        // occurrence in 14 days; it surfaced inside BiomeSystem's chunk loader, which before being
        // made exception safe then lost biome loading for the rest of the round.
        //
        // The entry the loop was reaching for when it ran out is the last one, so hand that back.
        // Only a genuinely empty table has no answer to give.
        private static T WeightedPickFallThrough<T>(IReadOnlyCollection<T> keys, string what)
        {
            if (keys.Count == 0)
                throw new InvalidOperationException($"Invalid weighted pick: {what} has no entries to pick from!");

            return keys.Last();
        }

        public static string Pick(this IRobustRandom random, DatasetPrototype prototype)
        {
            return random.Pick(prototype.Values);
        }

        /// <summary>
        /// Randomly selects an entry from <paramref name="prototype"/>, attempts to localize it, and returns the result.
        /// </summary>
        public static string Pick(this IRobustRandom random, LocalizedDatasetPrototype prototype)
        {
            var index = random.Next(prototype.Values.Count);
            return Loc.GetString(prototype.Values[index]);
        }

        public static string Pick(this IWeightedRandomPrototype prototype, System.Random random)
        {
            var picks = prototype.Weights;
            var sum = picks.Values.Sum();
            var accumulated = 0f;

            var rand = random.NextFloatValue() * sum;

            foreach (var (key, weight) in picks)
            {
                accumulated += weight;

                if (accumulated >= rand)
                {
                    return key;
                }
            }

            // Triad: this does happen, see WeightedPickFallThrough.
            // throw new InvalidOperationException($"Invalid weighted pick for {prototype.ID}!");
            return WeightedPickFallThrough(picks.Keys, prototype.ID);
        }

        public static string Pick(this IWeightedRandomPrototype prototype, IRobustRandom? random = null)
        {
            IoCManager.Resolve(ref random);
            var picks = prototype.Weights;
            var sum = picks.Values.Sum();
            var accumulated = 0f;

            var rand = random.NextFloat() * sum;

            foreach (var (key, weight) in picks)
            {
                accumulated += weight;

                if (accumulated >= rand)
                {
                    return key;
                }
            }

            // Triad: this does happen, see WeightedPickFallThrough.
            // throw new InvalidOperationException($"Invalid weighted pick for {prototype.ID}!");
            return WeightedPickFallThrough(picks.Keys, prototype.ID);
        }

        public static T Pick<T>(this IRobustRandom random, Dictionary<T, float> weights)
            where T: notnull
        {
            var sum = weights.Values.Sum();
            var accumulated = 0f;

            var rand = random.NextFloat() * sum;

            foreach (var (key, weight) in weights)
            {
                accumulated += weight;

                if (accumulated >= rand)
                {
                    return key;
                }
            }

            // Triad: see WeightedPickFallThrough.
            // throw new InvalidOperationException("Invalid weighted pick");
            return WeightedPickFallThrough(weights.Keys, $"a {typeof(T).Name} weight table");
        }

        public static T PickAndTake<T>(this IRobustRandom random, Dictionary<T, float> weights)
            where T : notnull
        {
            var pick = Pick(random, weights);
            weights.Remove(pick);
            return pick;
        }

        public static bool TryPickAndTake<T>(this IRobustRandom random, Dictionary<T, float> weights, [NotNullWhen(true)] out T? pick)
            where T : notnull
        {
            if (weights.Count == 0)
            {
                pick = default;
                return false;
            }
            pick = PickAndTake(random, weights);
            return true;
        }

        public static T Pick<T>(Dictionary<T, float> weights, System.Random random)
            where T : notnull
        {
            var sum = weights.Values.Sum();
            var accumulated = 0f;

            var rand = random.NextFloatValue() * sum;

            foreach (var (key, weight) in weights)
            {
                accumulated += weight;

                if (accumulated >= rand)
                {
                    return key;
                }
            }

            // Triad: this is the overload GroupSelector uses, and the one that threw on prod out of
            // BiomeSystem's chunk loader. See WeightedPickFallThrough.
            // throw new InvalidOperationException("Invalid weighted pick");
            return WeightedPickFallThrough(weights.Keys, $"a {typeof(T).Name} weight table");
        }

        public static (string reagent, FixedPoint2 quantity) Pick(this WeightedRandomFillSolutionPrototype prototype, IRobustRandom? random = null)
        {
            var randomFill = prototype.PickRandomFill(random);

            IoCManager.Resolve(ref random);

            var sum = randomFill.Reagents.Count;
            var accumulated = 0f;

            var rand = random.NextFloat() * sum;

            foreach (var reagent in randomFill.Reagents)
            {
                accumulated += 1f;

                if (accumulated >= rand)
                {
                    return (reagent, randomFill.Quantity);
                }
            }

            // Triad: this loop accumulates 1f per reagent against an integer count, so unlike the
            // others it is exact and only an empty Reagents list reaches here. Routed through
            // WeightedPickFallThrough anyway, for the clearer message and so the whole file behaves
            // one way.
            // throw new InvalidOperationException($"Invalid weighted pick for {prototype.ID}!");
            return (WeightedPickFallThrough(randomFill.Reagents, prototype.ID), randomFill.Quantity);
        }

        public static RandomFillSolution PickRandomFill(this WeightedRandomFillSolutionPrototype prototype, IRobustRandom? random = null)
        {
            IoCManager.Resolve(ref random);

            var fills = prototype.Fills;
            Dictionary<RandomFillSolution, float> picks = new();

            foreach (var fill in fills)
            {
                picks[fill] = fill.Weight;
            }

            var sum = picks.Values.Sum();
            var accumulated = 0f;

            var rand = random.NextFloat() * sum;

            foreach (var (randSolution, weight) in picks)
            {
                accumulated += weight;

                if (accumulated >= rand)
                {
                    return randSolution;
                }
            }

            // Triad: see WeightedPickFallThrough.
            // throw new InvalidOperationException($"Invalid weighted pick for {prototype.ID}!");
            return WeightedPickFallThrough(picks.Keys, prototype.ID);
        }

        /// <inheritdoc cref="HashCodeCombine(IReadOnlyCollection{int})"/>
        public static int HashCodeCombine(params int[] values)
        {
            return HashCodeCombine((IReadOnlyCollection<int>)values);
        }

        /// <summary>
        /// A very simple, deterministic djb2 hash function for generating a combined seed for the random number generator.
        /// We can't use HashCode.Combine because that is initialized with a random value, creating different results on the server and client.
        /// </summary>
        /// <example>
        /// Combine the current game tick with a NetEntity Id in order to not get the same random result if this is called multiple times in the same tick.
        /// <code>
        /// var seed = SharedRandomExtensions.HashCodeCombine((int)_timing.CurTick.Value, GetNetEntity(ent).Id);
        /// </code>
        /// </example>
        public static int HashCodeCombine(IReadOnlyCollection<int> values)
        {
            int hash = 5381;
            foreach (var value in values)
            {
                hash = (hash << 5) + hash + value;
            }
            return hash;
        }
    }
}

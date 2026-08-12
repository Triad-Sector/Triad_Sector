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
        /// Returns a random float in [0, 1), matching the engine's obsolete
        /// <c>System.Random.NextFloat()</c> derivation exactly.
        /// </summary>
        public static float NextFloatValue(this System.Random random)
        {
            return random.Next() * 4.6566128752458E-10f;
        }

        /// <summary>
        /// Returns a random float in [<paramref name="minValue"/>, <paramref name="maxValue"/>),
        /// matching the engine's obsolete <c>System.Random.NextFloat(float, float)</c> derivation.
        /// </summary>
        public static float NextFloatValue(this System.Random random, float minValue, float maxValue)
        {
            return random.NextFloatValue() * (maxValue - minValue) + minValue;
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

            // Shouldn't happen
            throw new InvalidOperationException($"Invalid weighted pick for {prototype.ID}!");
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

            // Shouldn't happen
            throw new InvalidOperationException($"Invalid weighted pick for {prototype.ID}!");
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

            throw new InvalidOperationException("Invalid weighted pick");
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

            throw new InvalidOperationException("Invalid weighted pick");
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

            // Shouldn't happen
            throw new InvalidOperationException($"Invalid weighted pick for {prototype.ID}!");
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

            // Shouldn't happen
            throw new InvalidOperationException($"Invalid weighted pick for {prototype.ID}!");
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

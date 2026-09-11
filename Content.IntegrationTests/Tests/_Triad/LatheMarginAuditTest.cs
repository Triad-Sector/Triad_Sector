using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Cargo.Systems;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Research.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// Prices every lathe recipe's feedstock against what its output actually sells for, and reports the
/// ratio. This exists because a printed item is the cheapest bulk entity in the game to produce, so any
/// recipe whose output prices far above its inputs is a money printer the moment someone notices: on
/// 2026-08-28 a single prototype at a ratio near nineteen accounted for 3.47M credits of sales in a day.
///
/// Deliberately a report and not yet a guard. The ratio distribution across the whole recipe corpus is
/// the thing needed to pick an honest threshold, and nobody has seen that distribution yet. Once the
/// number is chosen this becomes an assertion over <see cref="RecipeMargin.Ratio"/> and the sweep below
/// does not change.
///
/// The output side is priced by spawning the result and running the real <see cref="PricingSystem"/>,
/// not by reading StaticPrice out of YAML. Price is an event with twenty-odd subscribers (artifacts,
/// solutions, batteries, gas tanks, ballistics, armor, vending, trade crates), so a prototype's declared
/// price and its sale price are routinely different numbers, and the expensive cases are exactly the ones
/// where they diverge.
/// </summary>
[TestFixture]
[TestOf(typeof(LatheRecipePrototype))]
public sealed class LatheMarginAuditTest
{
    /// <summary>
    /// Recipes priced per <c>WaitPost</c>. Spawning is synchronous on the server thread, so this only
    /// bounds how long any single post occupies it.
    /// </summary>
    private const int ChunkSize = 200;

    /// <summary>How many rows the ranked table prints. The buckets below cover the rest.</summary>
    private const int ReportRows = 60;

    /// <summary>Ratio bands for the distribution summary, read as "up to and including".</summary>
    private static readonly double[] Buckets = [1, 2, 5, 10, 25, 50, 100];

    private sealed record RecipeMargin(string Recipe, string Result, double InputCost, double OutputValue)
    {
        /// <summary>
        /// Output value over feedstock value. A recipe that costs nothing to run is unbounded rather
        /// than infinitely profitable, and is called out separately in the report.
        /// </summary>
        public double Ratio => InputCost > 0 ? OutputValue / InputCost : double.PositiveInfinity;

        public double Profit => OutputValue - InputCost;
    }

    [Test]
    public async Task ReportLatheRecipeMargins()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var proto = server.ResolveDependency<IPrototypeManager>();
        var pricing = entMan.System<PricingSystem>();

        var testMap = await pair.CreateTestMap();

        var recipes = proto.EnumeratePrototypes<LatheRecipePrototype>().OrderBy(r => r.ID).ToList();
        var priced = new List<RecipeMargin>();
        var skipped = new List<string>();

        // Spawning every lathe result trips engine error logs on a handful of prototypes (a StorageFill
        // that overflows its own Storage grid is the usual one), and the pool fails any test that logged
        // an error when the pair is returned. Spawn validity is not what this test measures, and
        // SpawnAndDeleteAllEntitiesInTheSameSpot already owns that question, so the failure threshold is
        // lifted for the sweep and restored after. Anything that throws is named in the report instead of
        // taking the run down with it.
        var failureLevel = pair.ServerLogHandler.FailureLevel;
        pair.ServerLogHandler.FailureLevel = null;
        try
        {
            foreach (var chunk in recipes.Chunk(ChunkSize))
            {
                await server.WaitPost(() =>
                {
                    foreach (var recipe in chunk)
                    {
                        var input = FeedstockValue(proto, recipe);
                        var output = MaterialAndReagentValue(proto, recipe);
                        var resultName = "(materials and reagents only)";

                        if (recipe.Result is { } result)
                        {
                            resultName = result.Id;
                            if (!TryPriceSpawned(entMan, pricing, result.Id, testMap.GridCoords,
                                    out var spawnedValue, out var error))
                            {
                                skipped.Add($"{recipe.ID} -> {result.Id}: {error}");
                                continue;
                            }

                            output += spawnedValue;
                        }

                        priced.Add(new RecipeMargin(recipe.ID, resultName, input, output));
                    }
                });
            }
        }
        finally
        {
            pair.ServerLogHandler.FailureLevel = failureLevel;
        }

        WriteReport(recipes.Count, priced, skipped);

        // Not a margin assertion, a liveness one: if the sweep silently priced nothing the report above is
        // a page of zeroes that reads exactly like a clean bill of health.
        Assert.That(priced, Is.Not.Empty, "No lathe recipe could be priced at all, so this report proves nothing.");

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// What the recipe costs to run on a stock lathe. A multiplier of one makes
    /// <see cref="SharedLatheSystem.AdjustMaterial"/> the identity, which is deliberate: that is the most
    /// expensive a recipe can ever be, so every upgraded lathe in the game only widens the margin reported
    /// here. Feedstock is valued at the material's own price, which is what selling the raw sheets pays.
    /// </summary>
    private static double FeedstockValue(IPrototypeManager proto, LatheRecipePrototype recipe)
    {
        var cost = 0.0;
        foreach (var (material, needed) in recipe.Materials)
        {
            if (!proto.TryIndex(material, out var materialProto))
                continue;

            cost += SharedLatheSystem.AdjustMaterial(needed, recipe.MaterialDiscountScale, 1f) * materialProto.Price;
        }

        return cost;
    }

    /// <summary>
    /// The part of a recipe's output that is not an entity. Recipes may emit raw material, reagents, or
    /// both alongside (or instead of) a spawned result.
    /// </summary>
    private static double MaterialAndReagentValue(IPrototypeManager proto, LatheRecipePrototype recipe)
    {
        var value = 0.0;

        foreach (var (material, produced) in recipe.MaterialResult)
        {
            if (proto.TryIndex(material, out var materialProto))
                value += produced * materialProto.Price;
        }

        if (recipe.ResultReagents is { } reagents)
        {
            foreach (var (reagent, amount) in reagents)
            {
                if (proto.TryIndex(reagent, out var reagentProto))
                    value += (reagentProto.PricePerUnit * amount).Double();
            }
        }

        return value;
    }

    /// <summary>
    /// Spawns one result, prices it the way a pallet console would, and deletes it. Uses
    /// <see cref="PricingSystem.GetPrice"/> with contents included, because a printed item that arrives
    /// pre-filled (a loaded magazine, a stocked crate) sells for what is inside it too.
    /// </summary>
    private static bool TryPriceSpawned(
        IEntityManager entMan,
        PricingSystem pricing,
        string protoId,
        EntityCoordinates coords,
        out double price,
        out string error)
    {
        price = 0;
        error = string.Empty;
        var spawned = EntityUid.Invalid;

        try
        {
            spawned = entMan.SpawnEntity(protoId, coords);
            price = pricing.GetPrice(spawned);
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
        finally
        {
            if (entMan.EntityExists(spawned))
                entMan.DeleteEntity(spawned);
        }
    }

    private static void WriteReport(int totalRecipes, List<RecipeMargin> priced, List<string> skipped)
    {
        var writer = TestContext.Out;

        writer.WriteLine($"Lathe margin audit: {priced.Count} of {totalRecipes} recipes priced, {skipped.Count} skipped.");
        writer.WriteLine();

        // The free lunches: no feedstock at all, but the output is worth something. These are not a ratio
        // problem, they are a "why does this cost nothing" problem, and they belong at the top.
        var free = priced.Where(r => r.InputCost <= 0 && r.OutputValue > 0)
            .OrderByDescending(r => r.OutputValue)
            .ToList();

        if (free.Count > 0)
        {
            writer.WriteLine($"Recipes with no feedstock cost and a saleable output ({free.Count}):");
            foreach (var row in free)
                writer.WriteLine($"  {row.Recipe,-48} {row.Result,-40} out {row.OutputValue,12:N2}");
            writer.WriteLine();
        }

        var ranked = priced.Where(r => r.InputCost > 0 && r.OutputValue > 0)
            .OrderByDescending(r => r.Ratio)
            .ThenByDescending(r => r.Profit)
            .ToList();

        writer.WriteLine("Ratio distribution (output value / feedstock value):");
        var previous = 0.0;
        foreach (var bucket in Buckets)
        {
            var count = ranked.Count(r => r.Ratio > previous && r.Ratio <= bucket);
            writer.WriteLine($"  {previous,6:N1} < ratio <= {bucket,6:N1} : {count,5}");
            previous = bucket;
        }

        writer.WriteLine($"  ratio > {previous,-19:N1} : {ranked.Count(r => r.Ratio > previous),5}");
        writer.WriteLine();

        writer.WriteLine($"Top {Math.Min(ReportRows, ranked.Count)} by ratio:");
        writer.WriteLine($"  {"Recipe",-48} {"Result",-40} {"In",12} {"Out",12} {"Ratio",9}");

        foreach (var row in ranked.Take(ReportRows))
        {
            writer.WriteLine(
                $"  {row.Recipe,-48} {row.Result,-40} {row.InputCost,12:N2} {row.OutputValue,12:N2} {row.Ratio,9:N2}");
        }

        // The same rows cut by absolute profit per print. Ratio finds the seeds and toys; this finds the
        // recipes worth farming, because a 40x margin on 20 credits is noise and a 3x margin on 10,000 is
        // an income stream.
        var byProfit = priced.Where(r => r.Profit > 0 && r.Ratio > 1)
            .OrderByDescending(r => r.Profit)
            .ToList();

        writer.WriteLine();
        writer.WriteLine($"Top {Math.Min(ReportRows, byProfit.Count)} by profit per print (credits):");
        writer.WriteLine($"  {"Recipe",-48} {"Result",-40} {"In",12} {"Out",12} {"Profit",12} {"Ratio",9}");

        foreach (var row in byProfit.Take(ReportRows))
        {
            writer.WriteLine(
                $"  {row.Recipe,-48} {row.Result,-40} {row.InputCost,12:N2} {row.OutputValue,12:N2} {row.Profit,12:N2} {row.Ratio,9:N2}");
        }

        if (skipped.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Skipped ({skipped.Count}), these are unpriced and could be hiding anything:");
            foreach (var line in skipped)
                writer.WriteLine($"  {line}");
        }
    }
}

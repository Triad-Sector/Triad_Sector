#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A drydock refusal writes its reason to the pressing player's chat through a key chosen from
    /// the outcome the pipeline or the store returned. A key with no string reads as the raw key id
    /// in chat, which is the silent failure this guards: every failure value of every outcome enum,
    /// and every verb label those lines open with, has to resolve.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(ShipyardSystem))]
    public sealed class DrydockConsoleErrorLocaleTest
    {
        [Test]
        public async Task EveryDrydockFailureHasALocaleString()
        {
            await using var pair = await PoolManager.GetServerClient();
            var loc = pair.Server.ResolveDependency<ILocalizationManager>();

            var missing = new List<string>();
            var checkedKeys = 0;

            void Check(string key, string source)
            {
                checkedKeys++;
                if (!loc.HasString(key))
                    missing.Add($"{source} -> {key}");
            }

            var verbs = Enum.GetValues<ShipyardSystem.DrydockConsoleVerb>();
            foreach (var verb in verbs)
                Check(ShipyardSystem.DrydockVerbKey(verb), $"verb {verb}");

            foreach (var result in Enum.GetValues<DrydockStoreResult>().Where(r => r != DrydockStoreResult.Success))
                Check(ShipyardSystem.DrydockStoreErrorKey(result), $"DrydockStoreResult.{result}");

            foreach (var result in Enum.GetValues<DrydockRetrieveResult>().Where(r => r != DrydockRetrieveResult.Success))
                Check(ShipyardSystem.DrydockRetrieveErrorKey(result), $"DrydockRetrieveResult.{result}");

            foreach (var verb in verbs)
            {
                foreach (var result in Enum.GetValues<DrydockBerthResult>().Where(r => r != DrydockBerthResult.Success))
                    Check(ShipyardSystem.DrydockBerthErrorKey(verb, result), $"{verb} + DrydockBerthResult.{result}");
            }

            // The control: an empty enum or a mapping loop that never ran would pass with nothing checked.
            Assert.That(checkedKeys, Is.GreaterThan(verbs.Length * 2),
                "The control: too few keys were checked for the mappings to have been walked.");

            Assert.That(missing, Is.Empty,
                "Drydock console failures whose locale key has no string:" + Environment.NewLine + string.Join(Environment.NewLine, missing));

            await pair.CleanReturnAsync();
        }
    }
}

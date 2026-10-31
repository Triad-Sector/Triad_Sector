#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The captured-key set is persisted in every manifest and hashed into every revision's
    /// <c>captured_key_hash</c>, so it has to be a function of the ship being stored and never of server
    /// history. The capture's verdict cache is process-wide, so the empty-collection shortcut is read
    /// before any cached verdict: a populated lathe queue probed and found unserializable must not make
    /// an empty queue on any other ship captured, or one hull files different keys depending on which
    /// hull the server stored before it.
    ///
    /// <para>A fresh server, because the verdict cache lives as long as the server does and a pooled one
    /// may already have seen a populated queue. The same empty lathe is captured before and after a
    /// populated one is, and both captures have to agree.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockFidelitySystem))]
    public sealed class DrydockCaptureVerdictOrderTest
    {
        private const string LatheProtoId = "Protolathe";
        private const string LatheRecipeId = "SheetSteel";
        private const string QueueKey = "LatheComponent|Queue";

        [Test]
        public async Task AnEmptyFieldsCaptureDoesNotDependOnWhatWasProbedBefore()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var fidelity = server.System<DrydockFidelitySystem>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            var emptyMap = await pair.CreateTestMap();
            var populatedMap = await pair.CreateTestMap();
            await pair.MakeCleanupImmune(emptyMap.Grid.Owner);
            await pair.MakeCleanupImmune(populatedMap.Grid.Owner);

            EntityUid populatedLathe = default;

            await server.WaitPost(() =>
            {
                entMan.SpawnEntity(LatheProtoId, emptyMap.GridCoords);
                populatedLathe = entMan.SpawnEntity(LatheProtoId, populatedMap.GridCoords);
            });

            await pair.RunTicksSync(5);

            DrydockFidelityCapture before = default!;
            DrydockFidelityCapture populated = default!;
            DrydockFidelityCapture after = default!;

            await server.WaitPost(() =>
            {
                // The empty lathe, on a server whose verdict cache has never seen a populated queue.
                before = Capture(fidelity, emptyMap.Grid.Owner);

                entMan.GetComponent<LatheComponent>(populatedLathe).Queue.Add(
                    new LatheRecipeBatch(protoMan.Index<LatheRecipePrototype>(LatheRecipeId), itemsPrinted: 0, itemsRequested: 3, actor: null));

                populated = Capture(fidelity, populatedMap.Grid.Owner);

                // The same empty lathe again, now that the cache holds the populated verdict.
                after = Capture(fidelity, emptyMap.Grid.Owner);
            });

            Assert.Multiple(() =>
            {
                Assert.That(populated.CapturedKeys, Does.Contain(QueueKey),
                    "The control: a populated queue has no serializer and must be captured, or the cache never learned the verdict this test is about.");

                Assert.That(before.CapturedKeys, Does.Not.Contain(QueueKey),
                    "An empty queue writes as it is and has nothing to capture.");

                Assert.That(after.CapturedKeys.ToList(), Is.EqualTo(before.CapturedKeys.ToList()),
                    "The empty lathe's captured keys changed after a populated queue was probed elsewhere: the key set depends on server history.");

                Assert.That(after.ComputeCapturedKeyHash(), Is.EqualTo(before.ComputeCapturedKeyHash()),
                    "The persisted captured-key hash has to agree with itself for the same ship.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// One capture of the whole grid, put straight back afterwards so the next capture sees the grid
        /// exactly as it was: the walk clears what it captures, and a second walk over blanked fields
        /// would compare nothing.
        /// </summary>
        private static DrydockFidelityCapture Capture(DrydockFidelitySystem fidelity, EntityUid grid)
        {
            var capture = new DrydockFidelityCapture();
            var walk = fidelity.CaptureAndStripSliced(grid, capture, new DrydockSyncSlice(DrydockPhases.Store));
            Assert.That(walk.IsCompleted, Is.True, "The synchronous slice never suspends, so the walk finishes inside this call.");
            walk.GetAwaiter().GetResult();

            fidelity.RestoreSnapshot(capture);
            return capture;
        }
    }
}

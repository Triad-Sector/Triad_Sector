#nullable enable

using System.Threading.Tasks;
using Content.Shared.Chemistry.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Triad
{
    /// <summary>
    /// A solution entity forced out of its holder's slot is deleted, not left on the grid.
    ///
    /// <para>The generic empty-every-container paths (the EmptyAllContainers construction action,
    /// the destructible behaviour of the same name) drop solution entities onto the deck as
    /// invisible orphans whose Container reference then dangles; a ship saved afterwards carries
    /// them and logs on every load. The dump here is the same forced empty those paths perform,
    /// on the slot directly, so the assertion is on the solution system's reaction and not on any
    /// one caller.</para>
    /// </summary>
    [TestFixture]
    public sealed class LooseSolutionTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: LooseSolutionHolder
  components:
  - type: SolutionContainerManager
    solutions:
      tank:
        maxVol: 50
";

        [Test]
        public async Task ASolutionDumpedFromItsSlotIsDeleted()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var solutions = server.System<SharedSolutionContainerSystem>();
            var containers = server.System<SharedContainerSystem>();
            var map = await pair.CreateTestMap();

            var dumped = EntityUid.Invalid;
            var dumpedSolution = EntityUid.Invalid;
            var control = EntityUid.Invalid;
            var controlSolution = EntityUid.Invalid;

            await server.WaitAssertion(() =>
            {
                dumped = entMan.SpawnEntity("LooseSolutionHolder", map.GridCoords);
                control = entMan.SpawnEntity("LooseSolutionHolder", map.GridCoords);

                Assert.That(solutions.TryGetSolution(dumped, "tank", out var dumpedEnt, out _), "the dumped holder has its solution");
                Assert.That(solutions.TryGetSolution(control, "tank", out var controlEnt, out _), "the control holder has its solution");
                dumpedSolution = dumpedEnt!.Value.Owner;
                controlSolution = controlEnt!.Value.Owner;

                // What EmptyAllContainers does to every container on the entity, applied to the one
                // slot that matters here.
                var slot = containers.GetContainer(dumped, "solution@tank");
                var removed = containers.EmptyContainer(slot, force: true);
                Assert.That(removed, Does.Contain(dumpedSolution), "the forced empty removed the solution entity");
            });

            await server.WaitRunTicks(2);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entMan.Deleted(dumpedSolution), "a solution entity dumped from its slot is deleted");
                    Assert.That(entMan.Deleted(dumped), Is.False, "the holder itself is untouched");
                    Assert.That(entMan.Deleted(controlSolution), Is.False, "a solution still in its slot is untouched");
                    Assert.That(entMan.Deleted(control), Is.False);
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}

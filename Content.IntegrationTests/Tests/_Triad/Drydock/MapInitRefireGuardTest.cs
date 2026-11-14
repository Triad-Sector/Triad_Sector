#nullable enable

using System.Threading.Tasks;
using Content.Server.Storage.Components;
using Content.Server.Storage.EntitySystems;
using Content.Shared._Triad.Drydock;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The fill guards act on a map-init re-raise and on nothing else. A container a mapper placed
    /// with something already inside still gets its fill on a first map init, exactly as upstream
    /// does it, and the drydock's re-raise over the same container adds nothing.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(MapInitRefireSystem))]
    public sealed class MapInitRefireGuardTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: TriadRefireFillItem
  components:
  - type: Item

- type: entity
  id: TriadRefireBin
  components:
  - type: Bin
    initialContents:
    - TriadRefireFillItem
    - TriadRefireFillItem

- type: entity
  id: TriadRefireLocker
  components:
  - type: EntityStorage
  - type: StorageFill
    contents:
    - id: TriadRefireFillItem
      amount: 2
";

        [Test]
        public async Task AFirstMapInitFillsOnTopOfPlacedContentsAndARefireAddsNothing()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var mapSys = server.System<SharedMapSystem>();
            var bins = server.System<BinSystem>();
            var storage = server.System<EntityStorageSystem>();
            var refire = server.System<MapInitRefireSystem>();

            EntityUid mapUid = default;
            EntityUid bin = default;
            EntityUid locker = default;

            // A mapper's map: nothing on it has been map-initialized, and each container already
            // holds one thing someone put there by hand.
            await server.WaitPost(() =>
            {
                mapUid = mapSys.CreateMap(out var mapId, runMapInit: false);
                var at = new MapCoordinates(0, 0, mapId);

                bin = entMan.SpawnEntity("TriadRefireBin", at);
                Assert.That(bins.TryInsertIntoBin(bin, entMan.SpawnEntity("TriadRefireFillItem", at)), "the hand-placed item went into the bin");

                locker = entMan.SpawnEntity("TriadRefireLocker", at);
                Assert.That(storage.Insert(entMan.SpawnEntity("TriadRefireFillItem", at), locker), "the hand-placed item went into the locker");
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(mapSys.IsInitialized(mapUid), Is.False, "The control: the map has not been map-initialized yet.");
                Assert.That(BinCount(), Is.EqualTo(1));
                Assert.That(LockerCount(), Is.EqualTo(1));
            });

            await server.WaitPost(() => mapSys.InitializeMap(mapUid));

            await server.WaitAssertion(() =>
            {
                Assert.That(refire.Refiring, Is.False, "The flag is down outside a raise.");
                Assert.That(BinCount(), Is.EqualTo(3), "A first map init fills a bin on top of what a mapper put in it.");
                Assert.That(LockerCount(), Is.EqualTo(3), "A first map init fills a locker on top of what a mapper put in it.");
            });

            await server.WaitPost(() =>
            {
                refire.Raise(bin);
                refire.Raise(locker);
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(refire.Refiring, Is.False, "The flag is down again after a raise.");
                Assert.That(BinCount(), Is.EqualTo(3), "A re-raise adds nothing to a bin that holds something.");
                Assert.That(LockerCount(), Is.EqualTo(3), "A re-raise adds nothing to a locker that holds something.");
            });

            await server.WaitPost(() =>
            {
                entMan.DeleteEntity(bin);
                entMan.DeleteEntity(locker);
                mapSys.DeleteMap(entMan.GetComponent<MapComponent>(mapUid).MapId);
            });

            await pair.CleanReturnAsync();

            int BinCount() => entMan.GetComponent<BinComponent>(bin).Items.Count;
            int LockerCount() => entMan.GetComponent<EntityStorageComponent>(locker).Contents.ContainedEntities.Count;
        }
    }
}

using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server.Procedural;
using Content.Shared.Procedural;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Procedural;

[TestOf(typeof(DungeonSystem))]
public sealed class DungeonTests
{
    // Triad TEMP: drives a real NFVGRoidBasalt generation so the SetTilesChunked measurement can confirm the noise
    // commit no longer stalls a tick. Delete with the measurement instrumentation.
    [Test]
    public async Task ProfileVgroidDungeonGen()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();

        async Task<Task<List<Dungeon>>> StartGen(string label)
        {
            Task<List<Dungeon>>? task = null;
            await server.WaitPost(() =>
            {
                var mapSys = entManager.System<SharedMapSystem>();
                var dungeonSys = entManager.System<DungeonSystem>();
                var config = server.ResolveDependency<IPrototypeManager>().Index<DungeonConfigPrototype>("NFVGRoidBasalt");

                var mapUid = mapSys.CreateMap(out var mapId);
                var gridUid = entManager.CreateEntityUninitialized(null, new EntityCoordinates(mapUid, Vector2i.Zero));
                var grid = entManager.AddComponent<MapGridComponent>(gridUid);
                entManager.InitializeAndStartEntity(gridUid, mapId);

                task = dungeonSys.GenerateDungeonAsync(config, label, gridUid, grid, Vector2i.Zero, 1337);
            });
            return task!;
        }

        async Task PumpUntil(Task<List<Dungeon>> task)
        {
            var ticks = 0;
            while (!task.IsCompleted && ticks < 20000)
            {
                await server.WaitRunTicks(10);
                ticks += 10;
            }

            if (task.IsFaulted)
                Assert.Fail($"Dungeon gen threw: {task.Exception}");
            Assert.That(task.IsCompletedSuccessfully, Is.True, $"Dungeon gen did not finish within {ticks} ticks");
        }

        await PumpUntil(await StartGen("COLD"));
        await PumpUntil(await StartGen("WARM"));
    }

    [Test]
    public async Task TestDungeonRoomPackBounds()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoManager = pair.Server.ResolveDependency<IPrototypeManager>();

        await pair.Server.WaitAssertion(() =>
        {
            var sizes = new HashSet<Vector2i>();

            foreach (var proto in protoManager.EnumeratePrototypes<DungeonRoomPrototype>())
            {
                sizes.Add(proto.Size);
                sizes.Add(new Vector2i(proto.Size.Y, proto.Size.X));
            }

            foreach (var pack in protoManager.EnumeratePrototypes<DungeonRoomPackPrototype>())
            {
                var rooms = new List<Box2>();

                for (var i = 0; i < pack.Rooms.Count; i++)
                {
                    var room = pack.Rooms[i];
                    var bounds = (Box2) room;

                    for (var j = 0; j < rooms.Count; j++)
                    {
                        var existing = rooms[j];
                        Assert.That(!existing.Intersects(bounds), $"Found overlapping rooms {i} and {j} in DungeonRoomPack {pack.ID}");
                    }

                    rooms.Add(bounds);

                    // Inclusive of upper bounds as it's the edge
                    Assert.That(room.Left >= 0 &&
                                room.Bottom >= 0 &&
                                room.Right <= pack.Size.X &&
                                room.Top <= pack.Size.Y, $"Found invalid room {room} on DungeonRoomPack {pack.ID}");

                    // Assert that anything exists at this size
                    var rotated = new Vector2i(room.Size.Y, room.Size.X);

                    Assert.That(sizes.Contains(room.Size) || sizes.Contains(rotated), $"Didn't find any dungeon room prototypes for {room.Size} on {pack.ID} index {i}");
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TestDungeonPresets()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoManager = pair.Server.ResolveDependency<IPrototypeManager>();

        await pair.Server.WaitAssertion(() =>
        {
            var sizes = new HashSet<Vector2i>();

            foreach (var pack in protoManager.EnumeratePrototypes<DungeonRoomPackPrototype>())
            {
                sizes.Add(pack.Size);
                sizes.Add(new Vector2i(pack.Size.Y, pack.Size.X));
            }

            foreach (var preset in protoManager.EnumeratePrototypes<DungeonPresetPrototype>())
            {
                for (var i = 0; i < preset.RoomPacks.Count; i++)
                {
                    var pack = preset.RoomPacks[i];

                    // Assert that anything exists at this size
                    var rotated = new Vector2i(pack.Size.Y, pack.Size.X);

                    Assert.Multiple(() =>
                    {
                        Assert.That(sizes.Contains(pack.Size) || sizes.Contains(rotated), $"Didn't find any dungeon room prototypes for {pack.Size} for {preset.ID} index {i}");
                        Assert.That(pack.Bottom, Is.GreaterThanOrEqualTo(0), "All dungeon room packs need their y-axis to be above 0!");
                    });
                }
            }
        });

        await pair.CleanReturnAsync();
    }
}

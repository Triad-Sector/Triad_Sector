#nullable enable
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Atmos.Components;
using Content.Shared.Hands.Components;
using Content.Shared.RCD;
using Content.Shared.RCD.Components;
using Content.Shared.RPD;
using Content.Shared.RPD.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.RPD;

/// <summary>
/// The client streams the ghost's direction and its aimed pipe layer to the tool. The tool must take them from any
/// hand the sender holds it in (the stream can reach the server in the same tick as the swap that made the tool
/// active, ahead of it) and echo the layer back so the client can tell when a select landed. Rules P3 (direction)
/// and P11 (layer) on the wiki page "Pipe Placement Rules".
/// </summary>
public sealed class RPDStreamTest : InteractionTest
{
    [Test]
    public async Task P3_P11_StreamsLandFromTheOffHand()
    {
        EntityUid held = default;
        EntityUid floor = default;

        await Server.WaitPost(() =>
        {
            var active = Hands.ActiveHand;
            HandSys.AddHand(SPlayer, "offhand", HandLocation.Left, Hands);
            held = SEntMan.SpawnEntity("RPD", SEntMan.GetCoordinates(PlayerCoords));
            floor = SEntMan.SpawnEntity("RPD", SEntMan.GetCoordinates(PlayerCoords));
            Assert.That(HandSys.TryPickup(SPlayer, held, "offhand", checkActionBlocker: false, handsComp: Hands), Is.True);
            HandSys.SetActiveHand(SPlayer, active, Hands);
        });

        await RunTicks(5);

        // Control: the tool is held, but not in the active hand, and the second tool is not held at all.
        Assert.Multiple(() =>
        {
            Assert.That(HandSys.IsHolding(SPlayer, held), Is.True);
            Assert.That(Hands.ActiveHandEntity, Is.Not.EqualTo(held));
            Assert.That(HandSys.IsHolding(SPlayer, floor), Is.False);
        });

        var heldNet = SEntMan.GetNetEntity(held);
        var floorNet = SEntMan.GetNetEntity(floor);
        var netMan = Client.ResolveDependency<IEntityNetworkManager>();

        await Client.WaitPost(() =>
        {
            netMan.SendSystemNetworkMessage(new RPDLayerSelectEvent(heldNet, AtmosPipeLayer.Tertiary));
            netMan.SendSystemNetworkMessage(new RCDConstructionGhostRotationEvent(heldNet, Direction.East));
            netMan.SendSystemNetworkMessage(new RPDLayerSelectEvent(floorNet, AtmosPipeLayer.Tertiary));
            netMan.SendSystemNetworkMessage(new RCDConstructionGhostRotationEvent(floorNet, Direction.East));
        });

        await RunTicks(10);

        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.GetComponent<RPDComponent>(held).CurrentLayer, Is.EqualTo(AtmosPipeLayer.Tertiary),
                "a layer select from the off hand was dropped");
            Assert.That(SEntMan.GetComponent<RCDComponent>(held).ConstructionDirection, Is.EqualTo(Direction.East),
                "a rotation from the off hand was dropped");
            Assert.That(CEntMan.GetComponent<RPDComponent>(ToClient(heldNet)).CurrentLayer, Is.EqualTo(AtmosPipeLayer.Tertiary),
                "the layer did not reach the client, so the client cannot reconcile its stream against it");
            Assert.That(SEntMan.GetComponent<RPDComponent>(floor).CurrentLayer, Is.EqualTo(AtmosPipeLayer.Primary),
                "a tool the sender is not holding took a layer");
            Assert.That(SEntMan.GetComponent<RCDComponent>(floor).ConstructionDirection, Is.EqualTo(Direction.South),
                "a tool the sender is not holding took a rotation");
        });
    }
}

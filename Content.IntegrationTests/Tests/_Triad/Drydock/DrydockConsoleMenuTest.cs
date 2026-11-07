#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Client._NF.Shipyard.UI;
using Content.Shared._NF.Shipyard.BUI;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The drydock tab of the shipyard console as it actually draws. These build the real window
    /// on a real client, feed it a console state, and read the control tree back, the way the
    /// admin panel's tests do. Controls are found by their XAML name so nothing is made public to
    /// be testable.
    ///
    /// <para>Every assertion here is one the canvas fixes: which berth offers Retrieve, what an
    /// escrow row and a landing berth say, that the card is absent when nothing is out, and that
    /// no label ships as its own locale key.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockConsoleMenuTest
    {
        private static readonly Guid Viewer = Guid.NewGuid();

        [Test]
        public async Task TheCardFollowsTheShipOnTheCard()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null));
                Assert.That(Named(menu, "DeedShipPanel").Visible, Is.False, "Nothing out, no card.");

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: Behir(minutesOut: 65), deedTitle: "Behir"));
                Assert.Multiple(() =>
                {
                    Assert.That(Named(menu, "DeedShipPanel").Visible, Is.True);
                    var card = ((RichTextLabel)Named(menu, "DeedShipLabel")).GetMessage();
                    Assert.That(card, Does.Contain("Behir"));
                    Assert.That(card, Does.Contain("Corvette"), "The class sits behind the name.");
                    Assert.That(card, Does.Not.Contain("out "), "How long it has been out is not drawn; the server still sends it.");
                    var free = Rows(menu).Single(r => r.BerthId == 31);
                    Assert.That(free.StoreButton.Visible, Is.True, "Store sits on the free berth the ship fits.");
                    Assert.That(free.StoreButton.Disabled, Is.False);
                    Assert.That(Rows(menu).Where(r => r.BerthId != 31).Any(r => r.StoreButton.Visible), Is.False, "Not on a berth with a ship in it.");
                    Assert.That(Named(menu, "StoreStatusLabel").Visible, Is.False, "Nothing to explain.");
                });

                // The same ship out in space: the card still draws it, but Store is greyed and the
                // card says why, since a store is a hand-over at a berth.
                menu.UpdateState(State(berths: ThreeBerths(), deedShip: Behir(minutesOut: 65, docked: false), deedTitle: "Behir"));
                Assert.Multiple(() =>
                {
                    var free = Rows(menu).Single(r => r.BerthId == 31);
                    var status = (Label)Named(menu, "StoreStatusLabel");
                    Assert.That(free.StoreButton.Disabled, Is.True, "Not docked here, so nothing to hand over.");
                    Assert.That(status.Visible, Is.True);
                    Assert.That(status.Text, Is.EqualTo(Loc.GetString("shipyard-console-store-not-docked-note")), "The card carries the reason.");
                });

            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The footer's sale button follows the deed: gone for a hull the drydock holds, Return for a
        /// voucher hull, Sell otherwise, and greyed with nothing on the card.
        /// </summary>
        [Test]
        public async Task TheFooterSaleButtonFollowsTheDeed()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };
                var button = (Button)Named(menu, "SellShipButton");

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null, deedSale: ShipyardDeedSale.None));
                Assert.Multiple(() =>
                {
                    Assert.That(button.Visible, Is.True);
                    Assert.That(button.Disabled, Is.True, "No deed, nothing to sell.");
                    Assert.That(button.Text, Is.EqualTo(Loc.GetString("shipyard-console-sell-button")));
                });

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: Behir(minutesOut: 5), deedTitle: "Behir", deedSale: ShipyardDeedSale.StoreFirst));
                Assert.That(button.Visible, Is.False, "A loaded hull is sold from the tab, not the footer.");

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null, deedTitle: "Grunder", deedSale: ShipyardDeedSale.Return));
                Assert.Multiple(() =>
                {
                    Assert.That(button.Visible, Is.True);
                    Assert.That(button.Disabled, Is.False);
                    Assert.That(button.Text, Is.EqualTo(Loc.GetString("shipyard-console-return-button")), "A voucher hull is returned, not sold.");
                });

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null, deedTitle: "Grunder", deedSale: ShipyardDeedSale.Sell));
                Assert.Multiple(() =>
                {
                    Assert.That(button.Visible, Is.True);
                    Assert.That(button.Disabled, Is.False);
                    Assert.That(button.Text, Is.EqualTo(Loc.GetString("shipyard-console-sell-button")), "The label goes back after a Return.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Retrieve is in the details, and only for a stored ship while nothing is out.</summary>
        [Test]
        public async Task RetrieveSitsOnlyWhereItWouldWork()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };
                var berths = ThreeBerths();

                // Card in, no deed on it: the stored ships can come out.
                menu.UpdateState(State(berths: berths, deedShip: null));
                var rows = Rows(menu);
                Assert.Multiple(() =>
                {
                    Assert.That(rows, Has.Count.EqualTo(3), "One row per berth, empty ones included.");
                    Assert.That(rows.Any(r => r.RetrieveButton.Visible || r.MenuButton.Visible || r.CancelButton.Visible), Is.False,
                        "A row only picks its berth; the verbs are in the details.");
                    Assert.That(Named(menu, "BerthDetailPlaceholder").Visible, Is.True, "Nothing picked yet.");
                    Assert.That(Named(menu, "BerthDetail").Visible, Is.False);
                });

                Pick(menu, berths[0]);
                Assert.That(Detail(menu, "RetrieveButton").Visible, Is.True, "A stored ship can come out.");

                Pick(menu, berths[2]);
                Assert.That(Detail(menu, "RetrieveButton").Visible, Is.False, "An empty berth has nothing to retrieve.");

                // A ship out on the card: nothing may come out.
                menu.UpdateState(State(berths: berths, deedShip: Behir(minutesOut: 48), deedTitle: "Behir"));
                Pick(menu, berths[0]);
                Assert.That(Detail(menu, "RetrieveButton").Visible, Is.False);

            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Selecting a berth loads its verbs (ship and berth verbs for a stored ship, berth verbs only
        /// for an empty one), each priced or showing why it's greyed. The selection persists across
        /// states until the berth is removed.
        /// </summary>
        [Test]
        public async Task TheDetailsFollowThePickedBerth()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };
                var berths = ThreeBerths();
                menu.UpdateState(State(berths: berths, deedShip: null));

                Pick(menu, berths[0]);
                Assert.Multiple(() =>
                {
                    Assert.That(Drawn(Detail(menu, "BerthTools")), Is.SupersetOf(new[]
                    {
                        Loc.GetString("shipyard-console-menu-rename"),
                        Loc.GetString("shipyard-console-menu-move"),
                        Loc.GetString("shipyard-console-menu-transfer"),
                    }));
                    Assert.That(Drawn(Detail(menu, "BerthActions")), Has.Some.EqualTo(Loc.GetString("shipyard-console-menu-sell")));
                    Assert.That(Drawn(Detail(menu, "BerthActions")), Has.Some.EqualTo("$8,400"), "What the ship sells for.");
                    Assert.That(Drawn(Detail(menu, "BerthActions")), Has.Some.EqualTo(Loc.GetString("shipyard-console-detail-upgrade-to", ("class", "Corvette"))), "The class is on the button.");
                    Assert.That(Drawn(Detail(menu, "BerthActions")), Has.Some.EqualTo("$2,500"), "With its price.");
                    Assert.That(Drawn(Detail(menu, "BerthActions")), Has.None.EqualTo(Loc.GetString("shipyard-console-menu-sell-berth")), "A berth that cannot be sold does not offer it.");
                    Assert.That(((RichTextLabel)Detail(menu, "BerthDetailTitle")).GetMessage(), Does.Contain("Kestrel"));
                    Assert.That(IsFramed(Rows(menu)[0], "#ffd688"), Is.True, "The picked row is marked in yellow.");
                    Assert.That(IsFramed(Rows(menu)[1], "#ffd688"), Is.False, "And only that row.");
                });

                Pick(menu, berths[2]);
                Assert.Multiple(() =>
                {
                    var verbs = Drawn(Detail(menu, "BerthActions")).ToList();
                    Assert.That(verbs, Has.None.EqualTo(Loc.GetString("shipyard-console-menu-sell")), "No ship to sell.");
                    Assert.That(verbs, Has.Some.EqualTo(Loc.GetString("shipyard-console-menu-sell-berth")));
                    Assert.That(verbs, Has.Some.EqualTo("+$1,250"), "An empty berth says what it sells for.");
                });

                // A fresh state keeps the pick by number, on the fresh rows.
                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null));
                Assert.That(IsFramed(Rows(menu)[2], "#ffd688"), Is.True);

                // The berth sold: nothing to show.
                menu.UpdateState(State(berths: ThreeBerths().Take(2).ToList(), deedShip: null));
                Assert.Multiple(() =>
                {
                    Assert.That(Named(menu, "BerthDetail").Visible, Is.False);
                    Assert.That(Named(menu, "BerthDetailPlaceholder").Visible, Is.True);
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>In escrow the row is marked and says who and how long, and Cancel stands alone in the details.</summary>
        [Test]
        public async Task AnEscrowRowShowsCancelInPlaceOfTheVerbs()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };
                var berths = ThreeBerths();
                berths[0] = Berth(12, "Cutter", ship: ("Kestrel", "Cutter", "InEscrow"), offeredTo: "Tomas Reyes", secondsLeft: 27 * 60);

                menu.UpdateState(State(berths: berths, deedShip: null));
                var escrow = Rows(menu)[0];
                var stored = Rows(menu)[1];

                Assert.Multiple(() =>
                {
                    Assert.That(escrow.OccupantLabel.GetMessage(), Does.Contain("offered to Tomas Reyes"));
                    Assert.That(escrow.OccupantLabel.GetMessage(), Does.Contain("27 m left"));
                    Assert.That(IsFramed(escrow, "#8a6a3a"), Is.True, "The escrow amber, not the resting frame.");
                    Assert.That(IsFramed(stored, "#8a6a3a"), Is.False);
                });

                Pick(menu, berths[0]);
                Assert.Multiple(() =>
                {
                    Assert.That(Detail(menu, "CancelTransferButton").Visible, Is.True);
                    Assert.That(Detail(menu, "RetrieveButton").Visible, Is.False);
                    Assert.That(Detail(menu, "BerthActions").ChildCount, Is.Zero, "Nothing else can be done to the ship.");
                    Assert.That(((RichTextLabel)Detail(menu, "BerthDetailStatus")).GetMessage(), Does.Contain("offered to Tomas Reyes"));
                    Assert.That(((RichTextLabel)Detail(menu, "BerthDetailStatus")).GetMessage(), Does.Contain("27 m left"));
                });

                // The control: an ordinary stored ship has none of that.
                Pick(menu, berths[1]);
                Assert.Multiple(() =>
                {
                    Assert.That(Detail(menu, "CancelTransferButton").Visible, Is.False);
                    Assert.That(Detail(menu, "BerthActions").ChildCount, Is.Positive);
                });

            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An offer addressed to the viewer is one alert line, and the berth it would land in says
        /// what is coming in place of "empty". The viewer's own offers never become alerts.
        /// </summary>
        [Test]
        public async Task AnOfferDrawsOneAlertAndMarksTheLandingBerth()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };
                var offers = new List<DrydockTransferOfferInfo>
                {
                    new(1, Guid.NewGuid(), "Sabine", "Cutter", "Mara Voss", Guid.NewGuid(), landsInBerthId: 31, secondsLeft: 27 * 60),
                    // Our own offer to someone else: the escrow row says it, the alerts do not.
                    new(2, Guid.NewGuid(), "Kestrel", "Cutter", "Me", Viewer, landsInBerthId: 12, secondsLeft: 60),
                };

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: Behir(minutesOut: 48), deedTitle: "Behir", offers: offers));

                var alerts = Named(menu, "Offers").Children.ToList();
                var landing = Rows(menu).Single(r => r.BerthId == 31);
                var other = Rows(menu).Single(r => r.BerthId == 12);

                Assert.Multiple(() =>
                {
                    Assert.That(alerts, Has.Count.EqualTo(1), "One alert for the offer addressed to us, none for our own.");
                    var text = Descendants(alerts[0]).OfType<RichTextLabel>().Single().GetMessage();
                    Assert.That(text, Does.Contain("Mara Voss"));
                    Assert.That(text, Does.Contain("into #31"));
                    Assert.That(Descendants(alerts[0]).OfType<Button>().Select(b => b.Text), Is.EqualTo(new[] { "Accept", "Decline" }));
                    // It takes our last free berth while Behir is out, so the warning is on the line.
                    Assert.That(Descendants(alerts[0]).OfType<Label>().Select(l => l.Text), Has.Some.Contains("Behir would have nowhere to dock"));

                    Assert.That(landing.OccupantLabel.GetMessage(), Does.Contain("Sabine, if accepted"));
                    Assert.That(IsFramed(landing, "#5b86b8"), Is.True, "The landing berth wears the incoming blue.");
                    Assert.That(other.OccupantLabel.GetMessage(), Does.Not.Contain("if accepted"), "Our own offer marks nothing on our side.");
                    Assert.That(IsFramed(other, "#5b86b8"), Is.False);
                });

                // And with the offer gone, so is everything it drew.
                menu.UpdateState(State(berths: ThreeBerths(), deedShip: Behir(minutesOut: 48), deedTitle: "Behir"));
                Assert.Multiple(() =>
                {
                    Assert.That(Named(menu, "Offers").ChildCount, Is.Zero);
                    // GetMessage reads back as markup, colour tags and all, so the text is matched inside it.
                    Assert.That(Rows(menu).Single(r => r.BerthId == 31).OccupantLabel.GetMessage(), Does.Contain("empty").And.Not.Contain("if accepted"));
                });

            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An impounded ship is a card above the berths, to the Impound artboard: name, class and
        /// tag, the reason it was taken, the fee, Reclaim, and Abandon…; Reclaim greys on a fee the
        /// balance cannot cover and only then does small print say why. The appraisal share and the
        /// no-deadline note are not drawn. A locked one draws the reason, says an admin holds it, and
        /// offers neither, to the ImpoundLocked artboard.
        /// </summary>
        [Test]
        public async Task AnImpoundedShipDrawsItsCardAboveTheBerths()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };

                // Affordable: the balance the state carries is 12,300.
                var sabine = Impounded(fee: 9000, appraisal: 18000);
                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null, impounded: new List<DrydockImpoundedShipInfo> { sabine }));
                var cards = Named(menu, "Impounds").Children.ToList();
                Assert.That(cards, Has.Count.EqualTo(1), "One card per impounded ship.");

                var drawn = Drawn(cards[0]).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
                var buttons = Descendants(cards[0]).OfType<Button>().ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(drawn, Has.Some.Contains("Sabine"));
                    Assert.That(drawn, Has.Some.Contains("impounded"));
                    Assert.That(drawn, Has.Some.Contains("Still in the world at the end of round 4112."));
                    Assert.That(drawn, Has.Some.Contains("$9,000"), "The fee.");
                    Assert.That(drawn, Has.None.Contains("appraisal"), "The share it was cut from is not drawn.");
                    Assert.That(drawn, Has.None.Contains("deadline"), "No standing small print on a card the owner can act on.");
                    Assert.That(buttons.Select(b => b.Text), Is.EqualTo(new[] { "Reclaim", "Abandon…" }), "No berth is chosen on the card.");
                    Assert.That(buttons[0].Disabled, Is.False, "Affordable, and a berth fits.");
                    Assert.That(Rows(menu).Any(r => r.StoreButton.Visible), Is.False, "Nothing on the rows until Reclaim is pressed.");
                });

                // Reclaim arms the free berths and turns into Cancel.
                SetReclaiming(menu, sabine);
                Assert.Multiple(() =>
                {
                    var free = Rows(menu).Single(r => r.BerthId == 31);
                    Assert.That(free.StoreButton.Visible, Is.True);
                    Assert.That(free.StoreButton.Text, Is.EqualTo(Loc.GetString("shipyard-console-store-reclaim-button")));
                    Assert.That(Descendants(Named(menu, "Impounds")).OfType<Button>().Select(b => b.Text), Is.EqualTo(new[] { "Cancel", "Abandon…" }));
                });

                // Pressing Cancel puts the rows back.
                SetReclaiming(menu, null);
                Assert.That(Rows(menu).Any(r => r.StoreButton.Visible), Is.False);

                // Unaffordable: Reclaim greys and the small print says why.
                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null, impounded: new List<DrydockImpoundedShipInfo> { Impounded(fee: 19000, appraisal: 38000) }));
                var card = Named(menu, "Impounds").Children.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(Descendants(card).OfType<Button>().Single(b => b.Text == "Reclaim").Disabled, Is.True);
                    Assert.That(Drawn(card), Has.Some.EqualTo("Not enough credits: reclaiming it costs $19,000."));
                });

                // Locked: the reason, the note, the pill, and no verbs at all.
                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null, impounded: new List<DrydockImpoundedShipInfo>
                {
                    Impounded(fee: 0, appraisal: 38000, redeemable: false, reason: "Rammed the Vantage arm twice, ticket #94."),
                }));
                card = Named(menu, "Impounds").Children.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(Descendants(card).OfType<Button>(), Is.Empty, "Neither reclaim nor abandon on a locked impound.");
                    Assert.That(Drawn(card), Has.Some.EqualTo("LOCKED"));
                    Assert.That(Drawn(card), Has.Some.Contains("Rammed the Vantage arm twice"));
                    Assert.That(Drawn(card), Has.Some.Contains("until an admin unlocks it"));
                });

                // Out of the lot, the card goes.
                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null));
                Assert.That(Named(menu, "Impounds").ChildCount, Is.Zero);
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>The lockout covers the tab for another account's card and for nothing else.</summary>
        [Test]
        public async Task TheLockoutFollowsTheAccountOnTheCard()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: Behir(48), deedTitle: "Behir", deedOwner: Guid.NewGuid()));
                Assert.That(Named(menu, "LockoutPanel").Visible, Is.True, "Someone else's ship on the card.");

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: Behir(48), deedTitle: "Behir", deedOwner: Viewer));
                Assert.That(Named(menu, "LockoutPanel").Visible, Is.False, "Our own ship.");

                menu.UpdateState(State(berths: ThreeBerths(), deedShip: null, deedOwner: null));
                Assert.That(Named(menu, "LockoutPanel").Visible, Is.False, "No deed at all.");

                // It lies over the drydock tab; a child of Tabs would be a tab of its own.
                Assert.That(Named(menu, "LockoutPanel").Parent, Is.SameAs(Named(menu, "DrydockTabRoot")));
                Assert.That(Named(menu, "Tabs").ChildCount, Is.EqualTo(2), "Purchase and Drydock.");

            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Nothing the tab or its prompts draw may come out as a raw Fluent key. Rich text is read
        /// back through GetMessage, since a scan of plain labels alone sees none of it.
        /// </summary>
        [Test]
        public async Task NothingIsDrawnAsARawLocaleKey()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var menu = new ShipyardConsoleMenu { LocalUserId = Viewer };
                var berths = ThreeBerths();
                berths[0] = Berth(12, "Cutter", ship: ("Kestrel", "Cutter", "InEscrow"), offeredTo: "Tomas Reyes", secondsLeft: 90);
                var offers = new List<DrydockTransferOfferInfo>
                {
                    new(1, Guid.NewGuid(), "Sabine", "Cutter", "Mara Voss", Guid.NewGuid(), landsInBerthId: 31, secondsLeft: 30),
                };
                menu.UpdateState(State(berths: berths, deedShip: Behir(minutesOut: 48), deedTitle: "Behir", offers: offers,
                    impounded: new List<DrydockImpoundedShipInfo> { Impounded(fee: 9000, appraisal: 18000) }));
                Pick(menu, berths[1]);

                // The prompts are built the way the details build them, from the same keys.
                var sell = new DrydockTextPrompt(
                    Loc.GetString("shipyard-console-sell-title", ("ship", "Kestrel")),
                    Loc.GetString("shipyard-console-sell-body", ("ship", "Kestrel"), ("price", "$8,400"), ("percent", 35), ("appraisal", "$24,000"), ("berth", 12)),
                    Loc.GetString("shipyard-console-sell-warning"),
                    Loc.GetString("shipyard-console-sell-placeholder", ("ship", "Kestrel")),
                    Loc.GetString("shipyard-console-sell-button"),
                    _ => false, null, destructive: true, _ => { });
                var rename = new DrydockTextPrompt(
                    Loc.GetString("shipyard-console-rename-title", ("ship", "Kestrel")),
                    Loc.GetString("shipyard-console-rename-body"),
                    null,
                    Loc.GetString("shipyard-console-rename-placeholder"),
                    Loc.GetString("shipyard-console-rename-button"),
                    _ => false, 30, destructive: false, _ => { });
                var transfer = new DrydockListPicker(
                    Loc.GetString("shipyard-console-transfer-picker-title", ("ship", "Kestrel")),
                    Loc.GetString("shipyard-console-transfer-picker-placeholder"),
                    Loc.GetString("shipyard-console-transfer-picker-body", ("ship", "Kestrel"), ("berth", 12), ("minutes", 30)),
                    Loc.GetString("shipyard-console-transfer-picker-button"),
                    Loc.GetString("shipyard-console-transfer-picker-empty"),
                    new[]
                    {
                        new DrydockMenuButton.Item("Mara Voss", Loc.GetString("shipyard-console-transfer-picker-berths", ("count", 2)), true, () => { }),
                        new DrydockMenuButton.Item("Ilse Varga", Loc.GetString("shipyard-console-transfer-picker-no-berth"), false, () => { }),
                    });

                var abandon = new DrydockTextPrompt(
                    Loc.GetString("shipyard-console-abandon-title", ("ship", "Kestrel")),
                    Loc.GetString("shipyard-console-abandon-body", ("ship", "Kestrel")),
                    Loc.GetString("shipyard-console-abandon-warning"),
                    Loc.GetString("shipyard-console-abandon-placeholder", ("ship", "Kestrel")),
                    Loc.GetString("shipyard-console-abandon-button"),
                    _ => false, null, destructive: true, _ => { });

                var drawn = new[] { (Control)menu, sell, rename, transfer, abandon }
                    .SelectMany(Drawn)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Select(t => t!)
                    .ToList();

                Assert.Multiple(() =>
                {
                    Assert.That(drawn.Where(t => t.Contains("shipyard-console-")), Is.Empty, "A key drawn as text is a key with no entry in the ftl.");
                    Assert.That(drawn, Has.Some.Contains("35% of its $24,000 appraisal"), "The sale sentence is composed from its parts.");
                    Assert.That(drawn, Has.Some.EqualTo("2 berths free"), "The plural form of the picker's detail.");
                    Assert.That(drawn, Has.Some.EqualTo("Cannot be undone."));

                    // Control: an absent key resolves to itself, which is what the scan looks for.
                    Assert.That(Loc.GetString("shipyard-console-not-a-real-key"), Is.EqualTo("shipyard-console-not-a-real-key"));
                });

            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The Transfer… and Move… pickers size to their contents, and their list sits in a scroll
        /// box, which measures to zero unless told to report its content. Lay a picker out the way a
        /// window is and demand the rows, and the empty line when there are none, have height.
        /// </summary>
        [Test]
        public async Task APickersListHasHeightWhenItIsLaidOut()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var picker = new DrydockListPicker("Transfer Kestrel", "Filter", null, "Offer", "No one else is online.", new[]
                {
                    new DrydockMenuButton.Item("Mara Voss", "2 berths free", true, () => { }),
                    new DrydockMenuButton.Item("Ilse Varga", "no berth fits", false, () => { }),
                });
                var empty = new DrydockListPicker("Transfer Kestrel", "Filter", null, "Offer", "No one else is online.",
                    Array.Empty<DrydockMenuButton.Item>());

                LayOut(picker);
                LayOut(empty);

                Assert.Multiple(() =>
                {
                    Assert.That(LabelWith(picker, "Mara Voss").Height, Is.GreaterThan(0f), "A captain row draws.");
                    Assert.That(LabelWith(picker, "Ilse Varga").Height, Is.GreaterThan(0f), "A greyed captain row draws too.");
                    Assert.That(Descendants(picker).OfType<ScrollContainer>().Single().Height, Is.GreaterThan(40f),
                        "The list has room for both rows.");
                    Assert.That(LabelWith(empty, "No one else is online.").Height, Is.GreaterThan(0f), "The empty line draws.");
                });
            });

            await pair.CleanReturnAsync();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A window sized to its contents, measured and arranged the way the UI root does it.</summary>
        private static void LayOut(Control window)
        {
            window.Measure(new System.Numerics.Vector2(430, float.PositiveInfinity));
            window.Arrange(UIBox2.FromDimensions(System.Numerics.Vector2.Zero, window.DesiredSize));
        }

        private static Label LabelWith(Control root, string text)
            => Descendants(root).OfType<Label>().Single(l => l.Text == text);

        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (var child in root.Children)
            {
                yield return child;
                foreach (var deeper in Descendants(child))
                    yield return deeper;
            }
        }

        private static Control Named(Control root, string name)
            => Descendants(root).Single(c => c.Name == name);

        private static List<DrydockBerthRow> Rows(Control menu)
            => Named(menu, "Berths").Children.SelectMany(c => c.Children).OfType<DrydockBerthRow>().ToList();

        /// <summary>
        /// Picks a berth as pressing its row does. The row's button only takes a click on a laid-out
        /// window and these are never opened, so the handler behind it is called directly.
        /// </summary>
        private static void Pick(ShipyardConsoleMenu menu, DrydockBerthInfo berth)
            => typeof(ShipyardConsoleMenu)
                .GetMethod("SelectBerth", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(menu, new object?[] { berth });

        /// <summary>Arms the free berths as pressing Reclaim does, for the reason <see cref="Pick"/> calls straight in.</summary>
        private static void SetReclaiming(ShipyardConsoleMenu menu, DrydockImpoundedShipInfo? ship)
            => typeof(ShipyardConsoleMenu)
                .GetMethod("SetReclaiming", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(menu, new object?[] { ship });

        /// <summary>A control of the details by name; the rows beside them reuse some of the names.</summary>
        private static Control Detail(Control menu, string name)
            => Named(Named(menu, "BerthDetail"), name);

        private static IEnumerable<string?> Drawn(Control root)
            => Descendants(root).OfType<Label>().Select(l => l.Text)
                .Concat(Descendants(root).OfType<Button>().Select(b => b.Text))
                .Concat(Descendants(root).OfType<RichTextLabel>().Select(r => r.GetMessage()))
                .Concat(Descendants(root).OfType<LineEdit>().Select(e => e.PlaceHolder));

        private static bool IsFramed(PanelContainer row, string hex)
            => row.PanelOverride is StyleBoxFlat box && box.BorderColor == Color.FromHex(hex);

        /// <summary>The ship out on the card, docked here. #31 is the one empty berth in <see cref="ThreeBerths"/>.</summary>
        private static DrydockDeedShipInfo Behir(int minutesOut, bool docked = true)
            => new("Behir", "Corvette", minutesOut, defaultBerthId: 31, fittingBerthIds: new List<int> { 31 }, docked: docked);

        private static DrydockBerthInfo Berth(int id, string maxClass, (string Name, string Class, string State)? ship = null, string? offeredTo = null, int? secondsLeft = null)
            => new(id, maxClass, sellValue: 1250, upgradePrice: 2500, upgradeClass: "Corvette",
                occupantShipId: ship != null ? Guid.NewGuid() : null,
                occupantName: ship?.Name, occupantSizeClass: ship?.Class, occupantState: ship?.State,
                occupantSellPrice: ship != null ? 8400 : null,
                occupantTransferId: offeredTo != null ? 1 : null,
                occupantOfferedTo: offeredTo, occupantOfferSecondsLeft: secondsLeft,
                occupantAppraisal: ship != null ? 24000 : null);

        /// <summary>A Cutter in the lot that fits #31, the one empty berth in <see cref="ThreeBerths"/>.</summary>
        private static DrydockImpoundedShipInfo Impounded(int fee, int? appraisal, bool redeemable = true, string? reason = "Still in the world at the end of round 4112.")
            => new(Guid.NewGuid(), "Sabine", "Cutter", fee, appraisal, redeemable, reason, defaultBerthId: 31, fittingBerthIds: new List<int> { 31 });

        /// <summary>#12 Cutter with Kestrel, #15 Frigate with Pelican, #31 Cutter empty.</summary>
        private static List<DrydockBerthInfo> ThreeBerths() => new()
        {
            Berth(12, "Cutter", ship: ("Kestrel", "Cutter", "Stored")),
            Berth(15, "Frigate", ship: ("Pelican", "Corvette", "Stored")),
            Berth(31, "Cutter"),
        };

        private static ShipyardConsoleInterfaceState State(
            List<DrydockBerthInfo> berths,
            DrydockDeedShipInfo? deedShip,
            string? deedTitle = null,
            Guid? deedOwner = null,
            List<DrydockTransferOfferInfo>? offers = null,
            List<DrydockImpoundedShipInfo>? impounded = null,
            ShipyardDeedSale deedSale = ShipyardDeedSale.None)
        {
            var ships = berths.Where(b => b.OccupantShipId != null)
                .Select(b => new StoredShipInfo(b.OccupantShipId!.Value, b.OccupantName!, b.OccupantSizeClass, b.OccupantState!, b.BerthId))
                .ToList();
            if (deedShip != null)
                ships.Add(new StoredShipInfo(Guid.NewGuid(), deedShip.Name, deedShip.SizeClass, "CheckedOut", null));

            var state = new ShipyardConsoleInterfaceState(
                balance: 12300,
                accessGranted: true,
                shipDeedTitle: deedTitle,
                shipSellValue: 0,
                isTargetIdPresent: true,
                uiKey: 0,
                shipyardPrototypes: (new List<string>(), new List<string>()),
                shipyardName: "Shipyard",
                freeListings: false,
                sellRate: 0.35f)
            {
                Drydock = new DrydockTabState(
                    drydockEnabled: true,
                    storedShips: ships,
                    berths: berths,
                    berthPrices: new Dictionary<string, int> { ["Cutter"] = 2500, ["SuperCapital"] = 80000 },
                    transferOffers: offers ?? new List<DrydockTransferOfferInfo>(),
                    captains: new List<DrydockCaptainInfo>(),
                    deedOwnerUserId: deedOwner ?? (deedShip != null ? Viewer : null),
                    deedShip: deedShip,
                    transferOfferMinutes: 30)
                {
                    ImpoundedShips = impounded ?? new List<DrydockImpoundedShipInfo>(),
                    DeedSale = deedSale,
                    // The deed ship is out in the world, which is what the tab counts ships out by.
                    ShipsOut = deedShip != null
                        ? new List<DrydockReissueShipInfo> { new(NetEntity.Invalid, deedShip.Name, deedShip.SizeClass) }
                        : new List<DrydockReissueShipInfo>(),
                },
            };
            return state;
        }
    }
}

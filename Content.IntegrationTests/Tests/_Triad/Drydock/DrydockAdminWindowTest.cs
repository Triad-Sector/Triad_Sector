#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Client._NF.Shipyard.UI;
using Content.Client._Triad.Drydock.Admin;
using Content.Shared._Triad.Drydock.Admin;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The drydock admin panel as it actually draws. These build the real window on a real client
    /// and read the control tree back. They pin what the canvas fixes (the card's four lines, every
    /// verb greyed rather than absent, the escrow card's one fact, the stripes, the empty state) and
    /// one thing the canvas cannot see: that nothing runs past the window's edge at its minimum size.
    /// A screenshot beside the artboard is still the only proof of the look.
    ///
    /// <para>Controls are found by their XAML name rather than by making fields public, so the
    /// production surface is unchanged by being tested.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockAdminWindowTest
    {
        private static readonly string[] VerbKeys =
        {
            "impound", "release", "restore-to", "restore-from-sale", "cancel-offer",
        };

        [Test]
        public async Task ExactlyOneStateChipIsEverLit()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(Ship("Kestrel EXP-123", "Stored")));

                var chips = Named(window, "ChipGrid").Children.OfType<ContainerButton>().ToList();
                Assert.That(chips, Has.Count.EqualTo(9), "All, the seven states, and Stranded, three to a row.");

                // The chip in force is the one drawn filled. Two filled at once is the bug this
                // catches: it is what a toggle button's own pressed state did before they were
                // drawn by hand.
                Assert.That(chips.Count(IsLit), Is.EqualTo(1), "Exactly one chip is ever lit, and at rest it is All.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Every verb is always in the row, in one order, so an admin sees everything the panel can
        /// do; the state decides which are live. A greyed verb is drawn greyed (its own box, not the
        /// style sheet's, which vanishes on this panel) and its tooltip is the reason.
        /// </summary>
        [Test]
        [TestCase("Stored", new[] { "impound" })]
        [TestCase("CheckedOut", new[] { "impound", "restore-to" })]
        // An impounded hull offers the two ways out and never a second taking.
        [TestCase("Impounded", new[] { "release", "restore-to" })]
        // Escrow offers the withdrawal and nothing the server would refuse until it is withdrawn.
        [TestCase("InEscrow", new[] { "cancel-offer" })]
        // The terminal three never offer impound: their state is the verdict. A sale comes back only
        // through the reversal, which decides about the money first.
        [TestCase("Sold", new[] { "restore-from-sale" })]
        [TestCase("Destroyed", new[] { "restore-to" })]
        [TestCase("Abandoned", new[] { "restore-to" })]
        public async Task EveryVerbIsInTheRowAndTheStateDecidesWhichAreLive(string state, string[] live)
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                var berth = state is "Stored" or "InEscrow" ? 12 : (int?)null;
                window.UpdateState(StateWith(Ship("Kestrel EXP-123", state, berth), escrow: state == "InEscrow", sold: state == "Sold"));

                var verbs = Named(window, "VerbRow").Children.OfType<Button>().ToList();
                Assert.That(verbs, Has.Count.EqualTo(VerbKeys.Length + 1), "The five verbs and the overflow menu.");

                Assert.Multiple(() =>
                {
                    for (var i = 0; i < VerbKeys.Length; i++)
                    {
                        var key = VerbKeys[i];
                        var button = verbs[i];
                        Assert.That(button.Text, Does.StartWith(Text($"drydock-admin-{key}")), $"{key} sits in its fixed place.");

                        var shouldBeLive = live.Contains(key);
                        Assert.That(button.Disabled, Is.EqualTo(!shouldBeLive), $"{state}: {key} is {(shouldBeLive ? "live" : "greyed")}.");
                        Assert.That(button.StyleBoxOverride != null, Is.EqualTo(!shouldBeLive),
                            $"{state}: a greyed {key} draws the panel's own greyed box, a live one the style sheet's.");
                        if (!shouldBeLive)
                            Assert.That(button.ToolTip, Does.Not.StartWith("drydock-admin-"), $"{state}: a greyed {key} says why.");
                    }

                    Assert.That(verbs[^1].Disabled, Is.False, "The overflow menu always has something in it.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A card is four lines: callsign and name, class, owner, status. The callsign comes first
        /// when the name ends in one by the deed's own rule, and a name that does not keeps its shape.
        /// Status is the state, the berth when it holds one, then the state's own figure.
        /// </summary>
        [Test]
        public async Task ACardReadsCallsignClassOwnerAndStatus()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(
                    Ship("Kestrel EXP-123", "Stored", berth: 12),
                    escrow: false, sold: false,
                    Ship("Behir EXP-632", "Impounded", berth: null) with { ImpoundFee = 19000, ImpoundRedeemable = false },
                    Ship("Pelican EXP-058", "Sold", berth: null) with { LastSalePrice = 8400 },
                    Ship("Marlin CIV-904", "CheckedOut", berth: null),
                    Ship("Harrier", "Stored", berth: 31),
                    Ship("Sleipnir SCAV-123", "Abandoned", berth: null)));

                var cards = Named(window, "ShipContainer").Children.Select(CardText).ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(cards[0], Is.EqualTo(new[] { "EXP-123 | Kestrel", "Class:", "Cutter", "Owner:", "Mara Voss", "Status:", "Stored, Berth 12" }));
                    Assert.That(cards[1][^1], Is.EqualTo("Impounded, $19,000, Locked"), "The fee, and a locked impound says so.");
                    Assert.That(cards[2][^1], Is.EqualTo("Sold, $8,400"));
                    Assert.That(cards[3][^1], Is.EqualTo("Out, Round 4112"));
                    Assert.That(cards[4][0], Is.EqualTo("Harrier"), "A name with no callsign keeps its shape.");
                    Assert.That(cards[5][0], Is.EqualTo("Sleipnir SCAV-123"),
                        "Eight characters is past the deed's suffix limit, so the deed never split it and neither does the card.");
                });

                // Escrow carries its clock after the berth, so the two numbers never sit side by side.
                window.UpdateState(StateWith(Ship("Kestrel EXP-123", "InEscrow", berth: 12), escrow: true));
                var escrow = CardText(Named(window, "ShipContainer").Children.First());
                Assert.That(escrow[^1], Does.StartWith("Escrow, Berth 12, Expires in "));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>An escrow hull's card says one thing, where it would land, and goes with the offer.</summary>
        [Test]
        public async Task AnEscrowHullSaysWhereItLands()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(Ship("Kestrel EXP-123", "InEscrow", berth: 12), escrow: true));

                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "EscrowPanel").Visible, Is.True);
                    Assert.That(((Label) Named(window, "EscrowLandsLabel")).Text, Is.EqualTo("Berth 40"));
                });

                // The berth row the escrow ship sits in wears the stripes, and only that row.
                var rows = Named(window, "BerthContainer").Children.OfType<DrydockBerthRow>().ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(HasStripes(rows.Single(r => r.BerthId == 12)), Is.True, "The escrow berth is striped instead of badged.");
                    Assert.That(HasStripes(rows.Single(r => r.BerthId == 15)), Is.False, "An empty berth is not.");
                });

                window.UpdateState(StateWith(Ship("Kestrel EXP-123", "Stored", berth: 12)));
                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "EscrowPanel").Visible, Is.False, "The card does not outlive the offer.");
                    Assert.That(HasStripes(Named(window, "BerthContainer").Children.OfType<DrydockBerthRow>().Single(r => r.BerthId == 12)), Is.False);
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An impounded hull draws no card: the fee is on its list card and the rest is on the
        /// timeline. The one fact that lived nowhere else, where Release puts it, is on the empty
        /// berth it came from.
        /// </summary>
        [Test]
        public async Task AnImpoundedHullsLastBerthSaysSo()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(Ship("Behir EXP-632", "Impounded", berth: null, lastBerth: 15) with { ImpoundFee = 19000, ImpoundRedeemable = true }));

                var rows = Named(window, "BerthContainer").Children.OfType<DrydockBerthRow>().ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(Descendants(window).Any(c => c.Name is "ImpoundPanel" or "HeaderRow"), Is.False,
                        "No impound card and no header: everything they said lives elsewhere.");
                    Assert.That(rows.Single(r => r.BerthId == 15).OccupantLabel.GetMessage(), Does.Contain("Behir's last berth"),
                        "The callsign is dropped where the name is possessive.");
                    Assert.That(rows.Single(r => r.BerthId == 12).OccupantLabel.GetMessage(), Does.Not.Contain("last berth"));
                });
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task TheListsDrawOneRowPerThingTheyWereGiven()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                var state = StateWith(Ship("Kestrel EXP-123", "Stored"), escrow: false, sold: false,
                    Ship("Behir EXP-632", "CheckedOut", berth: null), Ship("Pelican EXP-058", "Impounded", berth: null));
                window.UpdateState(state);

                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "ShipContainer").ChildCount, Is.EqualTo(3), "One card per hull that matched.");
                    Assert.That(Named(window, "BerthContainer").Children.OfType<DrydockBerthRow>().Count(),
                        Is.EqualTo(state.OwnerBerths.Count),
                        "The owner's berths, drawn by the player's own row control.");
                    Assert.That(Named(window, "TimelineContainer").ChildCount,
                        Is.EqualTo(state.Selected!.Timeline.Count),
                        "One line per audit entry; a revision is a row here, not a panel of its own.");
                });

                // Rows in a box are not rows on screen. Lay the window out at its size and demand
                // the berth list has height: a scroll box with no vertical expand measures to
                // nothing and drew every berth into zero pixels.
                window.Measure(new Vector2(1400, 800));
                window.Arrange(new UIBox2(0, 0, 1400, 800));
                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "TimelineScroll").Height, Is.GreaterThan(40f), "Control: the timeline scroll box, which always expanded.");
                    Assert.That(Named(window, "BerthScroll").Height, Is.GreaterThan(40f), "The berth scroll box has room to draw its rows.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Prev and Next exist only when there is more than one page; on the first page Prev is
        /// greyed. The count is plural-aware.
        /// </summary>
        [Test]
        public async Task ThePagerOnlyAppearsWhenThereIsSomewhereToGo()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());

                var one = StateWith(Ship("Kestrel EXP-123", "Stored"));
                window.UpdateState(one);
                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "PrevPageButton").Visible, Is.False);
                    Assert.That(Named(window, "NextPageButton").Visible, Is.False);
                    Assert.That(((Label) Named(window, "CountLabel")).Text, Is.EqualTo("1 match"));
                });

                var many = StateWith(Ship("Kestrel EXP-123", "Stored"));
                many.TotalShips = 142;
                window.UpdateState(many);
                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "PrevPageButton").Visible, Is.True);
                    Assert.That(((Button) Named(window, "PrevPageButton")).Disabled, Is.True, "Page one has nowhere before it.");
                    Assert.That(((Button) Named(window, "NextPageButton")).Disabled, Is.False);
                    Assert.That(((Label) Named(window, "CountLabel")).Text, Is.EqualTo("142 matches"));
                    Assert.That(((Label) Named(window, "PageLabel")).Text, Is.EqualTo("Page 1 of 3"));
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// With nothing picked the right-hand side is one empty panel, rather than empty berths, an
        /// empty timeline and a notes box for a hull that is not there.
        /// </summary>
        [Test]
        public async Task WithNothingSelectedThereIsNothingToActOn()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(new DrydockAdminEuiState { TotalShips = 0 });

                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "EmptyPanel").Visible, Is.True);
                    Assert.That(Named(window, "DetailPanel").Visible, Is.False, "No verbs, berths, notes or timeline to see.");
                    Assert.That(Named(window, "VerbRow").ChildCount, Is.Zero);
                    Assert.That(((TextEdit) Named(window, "NotesInput")).Editable, Is.False,
                        "Notes belong to a hull, so there is nothing to type into.");
                });

                // And selecting a hull swaps them.
                window.UpdateState(StateWith(Ship("Kestrel EXP-123", "Stored")));
                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "EmptyPanel").Visible, Is.False);
                    Assert.That(Named(window, "DetailPanel").Visible, Is.True);
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The rule the first build broke: a row wider than the window does not shrink or wrap, it
        /// runs off the edge. Lay the window out at its minimum size, with the widest states the
        /// panel draws, and demand every control of the finding column and the verb row ends inside.
        /// </summary>
        [Test]
        public async Task NothingRunsPastTheWindowEdgeAtItsMinimumSize()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                var state = StateWith(Ship("Kestrel EXP-123", "CheckedOut", berth: null));
                state.TotalShips = 142;
                window.UpdateState(state);

                // A window lays out at its set size whatever it is offered, so shrink it the way
                // dragging its corner does before laying it out.
                var min = window.MinSize;
                LayOut(window, min);

                var watched = new List<Control>
                {
                    Named(window, "SearchInput"),
                    Named(window, "ChipGrid"),
                    Named(window, "PrevPageButton"),
                    Named(window, "PageLabel"),
                    Named(window, "NextPageButton"),
                    Named(window, "ReasonInput"),
                    Named(window, "GrantBerthButton"),
                };
                watched.AddRange(Named(window, "ChipGrid").Children);
                watched.AddRange(Named(window, "VerbRow").Children);

                Assert.Multiple(() =>
                {
                    foreach (var control in watched)
                    {
                        var right = RightEdge(control, window);
                        Assert.That(right, Is.LessThanOrEqualTo(min.X + 0.5f),
                            $"{control.Name ?? control.GetType().Name} ends at {right}, past the {min.X} wide window.");
                    }

                });

                // Control: the same layout and measure catch a control that really is past the edge.
                // A fresh window, because a child added after layout is only re-measured on the next
                // frame, and this test has no frames.
                var control = new DrydockAdminWindow(new DrydockAdminEui());
                control.UpdateState(state);
                var wide = new Control { MinWidth = min.X + 50 };
                Named(control, "VerbRow").AddChild(wide);
                LayOut(control, min);
                Assert.That(RightEdge(wide, control), Is.GreaterThan(min.X), "Control: an over-wide child is caught.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Nothing the panel draws may come out as a raw Fluent key. This is the check that a
        /// missing label fails loudly instead of shipping as "drydock-admin-action-ShipSold" in
        /// front of an admin.
        /// </summary>
        [Test]
        public async Task NothingIsDrawnAsARawLocaleKey()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(Ship("Kestrel EXP-123", "InEscrow", berth: 12), escrow: true));

                // Rich text has to be read back through GetMessage: the timeline and the berth rows
                // are RichTextLabels, and a scan of plain Labels alone sees none of them. That
                // blindness let a deliberately deleted key pass this test once.
                var drawn = Descendants(window).OfType<Label>().Select(l => l.Text)
                    .Concat(Descendants(window).OfType<Button>().Select(b => b.Text))
                    .Concat(Descendants(window).OfType<RichTextLabel>().Select(r => r.GetMessage()))
                    .Concat(Descendants(window).Select(c => c.ToolTip))
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                var raw = drawn
                    .Where(t => t!.Contains("drydock-admin-") || t.Contains("shipyard-console-"))
                    .ToList();

                Assert.That(raw, Is.Empty, "A key drawn as text is a key with no entry in the ftl.");

                // Every audit action and every state has a label, including the ones this sample
                // does not happen to contain. The tree scan can only see the rows it was given.
                var unlabelled = Enum.GetNames<Content.Server.Database.DrydockAuditAction>()
                    .Select(a => $"drydock-admin-action-{a}")
                    .Concat(Enum.GetNames<Content.Server.Database.DrydockShipState>().Select(s => $"drydock-admin-chip-{s}"))
                    .Where(key => Text(key) == key)
                    .ToList();
                Assert.That(unlabelled, Is.Empty, "An action or state with no label renders as its own key.");

                // Control: an absent key resolves to itself, which is what both checks look for.
                Assert.That(Text("drydock-admin-not-a-real-key"), Is.EqualTo("drydock-admin-not-a-real-key"));
            });

            await pair.CleanReturnAsync();
        }

        // ------------------------------------------------------------------ helpers

        private static string Text(string key) => Loc.GetString(key);

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

        private static void LayOut(Control window, Vector2 size)
        {
            window.SetSize = size;
            window.Measure(size);
            window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
        }

        /// <summary>A control's right edge in the window's space, summed up the parent chain.</summary>
        private static float RightEdge(Control control, Control window)
        {
            var x = control.Position.X + control.Width;
            for (var parent = control.Parent; parent != null && parent != window; parent = parent.Parent)
                x += parent.Position.X;
            return x;
        }

        /// <summary>A card's text, top to bottom, left to right: the title, then each label and value.</summary>
        private static string[] CardText(Control card)
            => Descendants(card).OfType<Label>().Select(l => l.Text ?? string.Empty).ToArray();

        private static bool HasStripes(DrydockBerthRow row)
            => row.Children.OfType<PanelContainer>().Any(p => p.PanelOverride is StyleBoxTexture);

        /// <summary>A chip is lit when its panel is drawn in the accent rather than the resting fill.</summary>
        private static bool IsLit(ContainerButton chip)
        {
            var panel = chip.Children.OfType<PanelContainer>().Single();
            return panel.PanelOverride is StyleBoxFlat box
                   && box.BackgroundColor != Color.FromHex("#222226");
        }

        private static DrydockAdminShipDto Ship(string name, string state, int? berth = 12, int? lastBerth = 12) => new(
            Guid.NewGuid(), name, Guid.NewGuid(), "Mara Voss", state,
            "Cutter", "TestVessel", BerthId: berth, LastBerthId: lastBerth, CheckedOutRoundId: 4112,
            DateTime.UtcNow, CurrentRevision: 7, LiveThisRound: false,
            EscrowExpiresAt: state == "InEscrow" ? DateTime.UtcNow.AddMinutes(27) : null);

        private static DrydockAdminEuiState StateWith(DrydockAdminShipDto selected)
            => StateWith(selected, false, false);

        private static DrydockAdminEuiState StateWith(DrydockAdminShipDto selected, bool escrow, bool sold = false, params DrydockAdminShipDto[] others)
        {
            var ships = new List<DrydockAdminShipDto> { selected };
            ships.AddRange(others);

            var timeline = new List<DrydockAdminAuditDto>
            {
                new(1, DateTime.UtcNow, "Store", Guid.NewGuid(), "Mara Voss", null, null, 7, 12, 4112, null, selected.Name),
                new(2, DateTime.UtcNow.AddMinutes(-5), "AccessRefused", Guid.NewGuid(), "Dov Ashkenazi", selected.OwnerUserId, "Mara Voss", null, null, 4112, null, selected.Name),
            };

            // Berth 12 holds the selected hull when it is berthed there; berth 15 is always empty.
            var inTwelve = selected.BerthId == 12;
            return new DrydockAdminEuiState
            {
                Ships = ships,
                TotalShips = ships.Count,
                CurrentRoundId = 4112,
                OwnerBerths =
                {
                    new DrydockAdminBerthDto(12, "Cutter", "Purchased", 2500,
                        inTwelve ? selected.ShipGuid : Guid.NewGuid(),
                        inTwelve ? selected.Name : "Harrier MIL-317",
                        "Cutter",
                        inTwelve ? selected.State : "Stored"),
                    new DrydockAdminBerthDto(15, "Frigate", "Purchased", 10000, null, null, null, null),
                },
                Selected = new DrydockAdminShipDetailDto(
                    selected,
                    "ticket #91",
                    new List<DrydockAdminRevisionDto>
                    {
                        new(7, "PlayerStore", DateTime.UtcNow, 4112, null, null, 2048, true, null, 24000),
                    },
                    timeline,
                    escrow
                        ? new DrydockAdminEscrowDto(1, selected.OwnerUserId, "Mara Voss", Guid.NewGuid(), "Tomas Reyes",
                            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(27), 40)
                        : null,
                    sold ? new DrydockAdminSaleDto(8400, DateTime.UtcNow, 12300) : null),
            };
        }
    }
}

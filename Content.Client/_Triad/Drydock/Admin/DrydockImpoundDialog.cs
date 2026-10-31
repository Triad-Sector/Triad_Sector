// Triad: the drydock admin panel.
using System;
using System.Numerics;
using Content.Client._NF.Shipyard.UI;
using Content.Client.Resources;
using Content.Client.Stylesheets;
using Content.Shared._NF.Bank;
using Content.Shared._Triad.Drydock;
using Content.Shared._Triad.Drydock.Admin;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;

namespace Content.Client._Triad.Drydock.Admin;

/// <summary>
/// Taking a hull into the impound lot, drawn to the "Drydock Surfaces" canvas. The fee is the
/// decision, so it is a slider with the credits it comes to beside it, quoted against the appraisal
/// the server will charge against; the reason is what the owner will read; and whether the owner may
/// act on it at all is a box that opens ticked, because the courtesy case is the common one and an
/// adjudication is the one an admin has to say out loud.
///
/// <para>Every rule here is applied again on the server. The dialog exists so the admin sees the
/// figures before deciding, not to enforce anything.</para>
/// </summary>
public sealed class DrydockImpoundDialog : DefaultWindow
{
    private readonly Slider _share;
    private readonly Label _percent;
    private readonly Label _credits;
    private readonly LineEdit _reason;
    private readonly CheckBox _redeemable;

    private readonly int? _appraisal;

    /// <param name="appraisal">
    /// The current revision's appraisal, or null when none is on file. For a hull that is still in
    /// the world this is only the last store's figure: the pipeline appraises it again as it is taken.
    /// </param>
    /// <param name="defaultPercent">
    /// Where the slider opens: the round-end sweep's share, carried on the state because its cvar is
    /// server-only, so an impound by hand charges what the sweep would have unless the admin moves it.
    /// </param>
    public DrydockImpoundDialog(DrydockAdminShipDto ship, int? appraisal, int defaultPercent, Action<int, bool, string?> onConfirm)
    {
        Title = Loc.GetString("drydock-admin-impound-title", ("ship", ship.Name));
        // A NaN height is "measure the contents"; a zero height is a fixed zero, which the
        // window then clips its contents to.
        SetSize = new Vector2(476, float.NaN);

        _appraisal = appraisal;

        var fonts = IoCManager.Resolve<IResourceCache>();
        var bold = fonts.NotoStack("Bold", 12);
        var boldLarge = fonts.NotoStack("Bold", 14);
        var small = fonts.NotoStack(size: 11);

        var column = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(14, 12) };

        column.AddChild(new Label { Text = Loc.GetString("drydock-admin-impound-body", ("ship", ship.Name)) });

        // The fee: label, slider, the share, and what it comes to.
        var feeRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalAlignment = VAlignment.Center,
        };
        feeRow.AddChild(new Label { Text = Loc.GetString("drydock-admin-impound-fee"), MinWidth = 52, Modulate = DrydockText.Dim });

        _share = new Slider
        {
            MinValue = 0,
            MaxValue = DrydockImpoundFee.MaxPercent,
            Value = DrydockImpoundFee.ClampPercent(defaultPercent),
            Rounded = true,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 10, 0),
        };
        feeRow.AddChild(_share);

        _percent = new Label { MinWidth = 44, Align = Label.AlignMode.Right, FontOverride = bold };
        feeRow.AddChild(_percent);

        _credits = new Label { MinWidth = 96, Align = Label.AlignMode.Right, FontOverride = boldLarge, Modulate = DrydockText.Impound };
        feeRow.AddChild(_credits);
        column.AddChild(feeRow);

        column.AddChild(new Label
        {
            Text = FeeNote(ship),
            FontOverride = small,
            Modulate = DrydockText.Dim,
            Margin = new Thickness(60, 0, 0, 0),
        });

        // The reason, which the owner reads.
        var reasonRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalAlignment = VAlignment.Center,
        };
        reasonRow.AddChild(new Label { Text = Loc.GetString("drydock-admin-impound-reason"), MinWidth = 52, Modulate = DrydockText.Dim });
        _reason = new LineEdit
        {
            PlaceHolder = Loc.GetString("drydock-admin-impound-reason-placeholder"),
            HorizontalExpand = true,
        };
        reasonRow.AddChild(_reason);
        column.AddChild(reasonRow);

        column.AddChild(new Label
        {
            Text = Loc.GetString("drydock-admin-impound-reason-note"),
            FontOverride = small,
            Modulate = DrydockText.Dim,
            Margin = new Thickness(60, 0, 0, 0),
        });

        // Whether the owner may act on it. Ticked by default: an admin who means "frozen until I
        // say otherwise" has to say so.
        var redeemBox = new PanelContainer
        {
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#16161a"),
                BorderColor = Color.FromHex("#3a3a3e"),
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = 10,
                ContentMarginRightOverride = 10,
                ContentMarginTopOverride = 8,
                ContentMarginBottomOverride = 8,
            },
            Margin = new Thickness(0, 10, 0, 0),
        };
        var redeemColumn = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        var redeemRow = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, VerticalAlignment = VAlignment.Center };
        _redeemable = new CheckBox
        {
            Text = Loc.GetString("drydock-admin-impound-redeemable"),
            Pressed = true,
        };
        redeemRow.AddChild(_redeemable);
        redeemRow.AddChild(new Label { Text = Loc.GetString("drydock-admin-impound-redeemable-detail"), Modulate = DrydockText.Dim, Margin = new Thickness(4, 0, 0, 0) });
        redeemColumn.AddChild(redeemRow);
        redeemColumn.AddChild(new Label
        {
            Text = Loc.GetString("drydock-admin-impound-redeemable-note"),
            FontOverride = small,
            Modulate = DrydockText.Dim,
            Margin = new Thickness(22, 0, 0, 0),
        });
        redeemBox.AddChild(redeemColumn);
        column.AddChild(redeemBox);

        var buttons = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var confirm = new Button
        {
            Text = Loc.GetString("drydock-admin-impound-confirm"),
            StyleClasses = { "ButtonSquare", "ButtonCaution" },
            MinWidth = 110,
        };
        var cancel = new Button
        {
            Text = Loc.GetString("drydock-admin-impound-cancel"),
            StyleClasses = { "ButtonSquare" },
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
        };
        buttons.AddChild(confirm);
        buttons.AddChild(cancel);
        column.AddChild(buttons);

        Contents.AddChild(column);

        cancel.OnPressed += _ => Close();
        _share.OnValueChanged += _ => Refresh();
        confirm.OnPressed += _ =>
        {
            onConfirm(
                (int)_share.Value,
                _redeemable.Pressed,
                string.IsNullOrWhiteSpace(_reason.Text) ? null : _reason.Text);
            Close();
        };

        Refresh();
    }

    /// <summary>
    /// The share and what it comes to, recomputed as the knob moves. The arithmetic is the server's
    /// own, so the number shown is the number charged for a hull taken from its berth; a hull still
    /// in the world is appraised again as it is taken, and the note beside the slider says so.
    /// </summary>
    private void Refresh()
    {
        var percent = DrydockImpoundFee.ClampPercent((int)_share.Value);
        _percent.Text = $"{percent}%";
        _credits.Text = BankSystemExtensions.ToSpesoString(DrydockImpoundFee.Against(_appraisal ?? 0, percent));
    }

    private string FeeNote(DrydockAdminShipDto ship)
    {
        if (_appraisal is not { } appraisal)
            return Loc.GetString("drydock-admin-impound-fee-note-unknown");

        return Loc.GetString(ship.LiveThisRound ? "drydock-admin-impound-fee-note-live" : "drydock-admin-impound-fee-note",
            ("appraisal", BankSystemExtensions.ToSpesoString(appraisal)));
    }
}

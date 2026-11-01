// Triad: the drydock admin panel.
using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Client._NF.Shipyard.UI;
using Content.Shared._NF.Bank;
using Content.Shared._Triad.Drydock.Admin;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;

namespace Content.Client._Triad.Drydock.Admin;

/// <summary>
/// Undoing a sale. The money is the decision, so it is stated before anything else: what was paid,
/// when, and whether it comes back. Taking it back is ticked by default and unticks itself when
/// the owner's balance cannot cover it, which is the one case that needs a reason on the record.
///
/// <para>Every rule here is applied again on the server. The dialog exists so the admin sees the
/// figures before deciding, not to enforce anything.</para>
/// </summary>
public sealed class DrydockRestoreSaleDialog : DefaultWindow
{
    private readonly CheckBox _takeMoney;
    private readonly LineEdit _reason;
    private readonly Button _confirm;
    private readonly OptionButton _berths;
    private readonly List<int> _berthIds = new();

    /// <summary>True when the balance was read and falls short, which is what forces a reason.</summary>
    private readonly bool _cannotCover;

    public DrydockRestoreSaleDialog(
        DrydockAdminShipDto ship,
        DrydockAdminSaleDto sale,
        IReadOnlyList<int> berthTargets,
        int? preferredBerth,
        Action<int, bool, string?> onConfirm)
    {
        Title = Loc.GetString("drydock-admin-sale-title", ("ship", ship.Name));
        // A NaN height is "measure the contents"; a zero height is a fixed zero, which the
        // window then clips its contents to.
        SetSize = new Vector2(460, float.NaN);

        _cannotCover = sale.OwnerBalance is { } balance && balance < sale.Price;

        var column = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(8) };

        column.AddChild(new Label
        {
            Text = Loc.GetString("drydock-admin-sale-body",
                ("price", BankSystemExtensions.ToSpesoString(sale.Price)),
                ("at", $"{sale.At:yyyy-MM-dd HH:mm}")),
        });

        column.AddChild(new Label
        {
            Text = sale.OwnerBalance is { } known
                ? Loc.GetString("drydock-admin-sale-balance", ("balance", BankSystemExtensions.ToSpesoString(known)))
                : Loc.GetString("drydock-admin-sale-balance-unknown"),
            Modulate = _cannotCover ? Color.FromHex("#ff6a6a") : DrydockText.Dim,
            Margin = new Thickness(0, 2, 0, 8),
        });

        _takeMoney = new CheckBox
        {
            Text = Loc.GetString("drydock-admin-sale-take-back", ("price", BankSystemExtensions.ToSpesoString(sale.Price))),
            // Ticked by default, except where the balance is known to fall short: there the
            // server would refuse it, so the dialog opens on the choice that can succeed.
            Pressed = !_cannotCover,
            ToolTip = Loc.GetString("drydock-admin-sale-take-back-tooltip"),
        };
        column.AddChild(_takeMoney);

        var berthRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VAlignment.Center,
        };
        berthRow.AddChild(new Label { Text = Loc.GetString("drydock-admin-sale-berth"), Margin = new Thickness(0, 0, 6, 0) });

        _berths = new OptionButton { HorizontalExpand = true };
        foreach (var berth in berthTargets)
        {
            _berths.AddItem($"#{berth}", _berthIds.Count);
            _berthIds.Add(berth);
        }

        // Default to where it last sat, when that berth is still one of the offered targets.
        var preferred = preferredBerth != null ? _berthIds.IndexOf(preferredBerth.Value) : -1;
        if (_berthIds.Count > 0)
            _berths.SelectId(preferred >= 0 ? preferred : 0);

        _berths.OnItemSelected += args => _berths.SelectId(args.Id);
        berthRow.AddChild(_berths);
        column.AddChild(berthRow);

        _reason = new LineEdit
        {
            PlaceHolder = Loc.GetString("drydock-admin-sale-reason-placeholder"),
            HorizontalExpand = true,
            Margin = new Thickness(0, 8, 0, 0),
        };
        column.AddChild(_reason);

        var buttons = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HAlignment.Right,
        };
        var cancel = new Button { Text = Loc.GetString("drydock-admin-sale-cancel"), StyleClasses = { "ButtonSquare" }, MinWidth = 90 };
        _confirm = new Button
        {
            Text = Loc.GetString("drydock-admin-sale-confirm"),
            StyleClasses = { "ButtonSquare", "ButtonCaution" },
            MinWidth = 90,
            Margin = new Thickness(4, 0, 0, 0),
        };
        buttons.AddChild(cancel);
        buttons.AddChild(_confirm);
        column.AddChild(buttons);

        Contents.AddChild(column);

        cancel.OnPressed += _ => Close();
        _takeMoney.OnToggled += _ => Refresh();
        _reason.OnTextChanged += _ => Refresh();
        _confirm.OnPressed += _ =>
        {
            if (_berthIds.Count == 0)
                return;

            onConfirm(
                _berthIds[Math.Clamp(_berths.SelectedId, 0, _berthIds.Count - 1)],
                _takeMoney.Pressed,
                string.IsNullOrWhiteSpace(_reason.Text) ? null : _reason.Text);
            Close();
        };

        Refresh();
    }

    /// <summary>
    /// Confirm needs a berth to restore into, and a reason whenever the money stays with the
    /// owner. Both are the server's rules; failing them here just saves a round trip.
    /// </summary>
    private void Refresh()
    {
        var reasonGiven = !string.IsNullOrWhiteSpace(_reason.Text);
        _confirm.Disabled = _berthIds.Count == 0 || (!_takeMoney.Pressed && !reasonGiven);
    }
}

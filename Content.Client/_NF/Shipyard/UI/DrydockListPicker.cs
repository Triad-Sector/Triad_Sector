// Triad: drydock tab.
using System.Linq;
using System.Numerics;
using Content.Client.Stylesheets;
using Content.Client.UserInterface.Controls;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// A window that lists choices and takes one: a filter box when the list can be long, the rows,
/// a sentence about what picking does, then the verb and Cancel. The transfer picker (captains
/// online, with their free berths) and the move picker (empty berths that fit) are both this. A
/// row that cannot be taken stays listed, greyed, with the reason as its detail.
///
/// <para>Picking selects; the verb commits. The server checks the choice again, so a list that
/// went stale between the state and the click is a refusal, never a wrong outcome.</para>
/// </summary>
public sealed class DrydockListPicker : FancyWindow
{
    private static readonly Color ListBorder = Color.FromHex("#333333");

    private readonly LineEdit? _filter;
    private readonly Button _confirm;
    private readonly List<(DrydockMenuButton.Item Item, ContainerButton Row, StyleBoxFlat Fill, PanelContainer Rule)> _rows = new();
    private DrydockMenuButton.Item? _selected;

    /// <param name="filterPlaceholder">Placeholder for the filter box, or null for no box.</param>
    /// <param name="body">A sentence under the list about what the verb does, or null.</param>
    /// <param name="items">The rows, in order. An item's OnPressed runs when the verb commits it; DividerAbove is not drawn here.</param>
    public DrydockListPicker(string title, string? filterPlaceholder, string? body, string confirmLabel, string emptyText, IReadOnlyList<DrydockMenuButton.Item> items)
    {
        Title = title;
        // A NaN height is "measure the contents"; a zero height is a fixed zero, which the
        // window then clips its contents to.
        SetSize = new Vector2(430, float.NaN);

        var column = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(14, 12) };

        if (filterPlaceholder != null)
        {
            _filter = new LineEdit { PlaceHolder = filterPlaceholder, HorizontalExpand = true, Margin = new Thickness(0, 0, 0, 10) };
            _filter.OnTextChanged += _ => Filter(_filter.Text);
            column.AddChild(_filter);
        }

        var listPanel = new PanelContainer
        {
            PanelOverride = new StyleBoxFlat { BackgroundColor = DrydockText.ListBackground, BorderColor = ListBorder, BorderThickness = new Thickness(1) },
            HorizontalExpand = true,
        };
        var list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };

        if (items.Count == 0)
            list.AddChild(new Label { Text = emptyText, Modulate = DrydockText.Dim, Margin = new Thickness(10, 7) });

        foreach (var item in items)
        {
            // The row carries its rule inside it, so a row the filter hides takes its rule along.
            var rule = DrydockText.RulePanel(DrydockText.RowRule);
            var row = DrydockText.ItemRow(item, out var fill, rule);
            var picked = item;
            row.OnPressed += _ => Select(picked);

            _rows.Add((item, row, fill, rule));
            list.AddChild(row);
        }

        listPanel.AddChild(list);

        // ReturnMeasure makes the scroll box report its content's height, up to MaxHeight. Without it
        // a scroll box measures to zero, and in this content-sized window the list drew into 0px:
        // Transfer… and Move… opened with no rows and no empty line either.
        var scroll = new ScrollContainer { HorizontalExpand = true, HScrollEnabled = false, MaxHeight = 220, ReturnMeasure = true };
        scroll.AddChild(listPanel);
        column.AddChild(scroll);

        if (body != null)
        {
            var sentence = new RichTextLabel { Margin = new Thickness(0, 10, 0, 0) };
            sentence.SetMessage(FormattedMessage.FromUnformatted(body));
            column.AddChild(sentence);
        }

        var buttons = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _confirm = new Button
        {
            Text = confirmLabel,
            StyleClasses = { "ButtonSquare", StyleNano.ButtonPrimary },
            MinWidth = 110,
            Disabled = true,
        };
        var cancel = new Button { Text = Loc.GetString("shipyard-console-prompt-cancel"), StyleClasses = { "ButtonSquare" }, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        buttons.AddChild(_confirm);
        buttons.AddChild(cancel);
        column.AddChild(buttons);

        ContentsContainer.AddChild(column);

        cancel.OnPressed += _ => Close();
        _confirm.OnPressed += _ =>
        {
            if (_selected == null)
                return;

            _selected.OnPressed?.Invoke();
            Close();
        };

        RedrawRules();
    }

    protected override void Opened()
    {
        base.Opened();
        _filter?.GrabKeyboardFocus();
    }

    private void Select(DrydockMenuButton.Item item)
    {
        _selected = item;
        _confirm.Disabled = !item.Enabled;
        foreach (var (candidate, _, fill, _) in _rows)
            fill.BackgroundColor = ReferenceEquals(candidate, item) ? DrydockText.RowHighlight : Color.Transparent;
    }

    /// <summary>Hides the rows whose label does not contain the text; the selection is dropped if it goes.</summary>
    private void Filter(string text)
    {
        text = text.Trim();
        foreach (var (item, row, _, _) in _rows)
            row.Visible = text.Length == 0 || item.Label.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (_selected != null && _rows.Any(r => ReferenceEquals(r.Item, _selected) && !r.Row.Visible))
        {
            _selected = null;
            _confirm.Disabled = true;
            foreach (var (_, _, fill, _) in _rows)
                fill.BackgroundColor = Color.Transparent;
        }

        RedrawRules();
    }

    /// <summary>A rule under every visible row but the last, so the list ends on its border.</summary>
    private void RedrawRules()
    {
        var last = _rows.LastOrDefault(r => r.Row.Visible);
        foreach (var entry in _rows)
            entry.Rule.Visible = entry.Row.Visible && !ReferenceEquals(entry.Row, last.Row);
    }
}

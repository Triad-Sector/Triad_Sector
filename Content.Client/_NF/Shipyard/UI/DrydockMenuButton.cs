// Triad: drydock tab.
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// A button that drops a short menu. On the shipyard console: a berth row's three dots, Store in
/// #N, Buy berth, and an impound card's Into #N picker; on the drydock admin panel: Restore to…,
/// the overflow menu, Grant berth, and the admin berth rows' three dots. The rare verbs live here
/// so a row itself carries one button.
///
/// <para>The owning window replaces the items on every state, but a menu that is already open was
/// built from the items as they stood when it opened. Its popup sits in the modal root rather than
/// under this button, so it keeps those entries and their callbacks until it closes, even across a
/// state that rebuilt the button or removed it. The server checks every verb again on receipt.</para>
/// </summary>
public sealed class DrydockMenuButton : Button
{
    /// <summary>
    /// One entry of a dropdown or of a <see cref="DrydockListPicker"/>: a label on the left, a
    /// detail on the right, and whether it can be taken. A disabled entry stays visible with its
    /// detail saying why, read without hovering. <paramref name="OnPressed"/> runs when a dropdown
    /// entry is pressed, or when a picker's verb commits the selected row.
    /// <paramref name="DividerAbove"/> draws the heavier rule that separates the ship verbs from the
    /// berth verbs in a dropdown; a picker ignores it.
    /// </summary>
    public sealed record Item(string Label, string? Detail, bool Enabled, Action? OnPressed, bool DividerAbove = false);

    private const float MenuWidth = 200f;

    private static readonly Color MenuBorder = Color.FromHex("#5a5a5a");
    private static readonly Color SectionRule = Color.FromHex("#3a3a3a");

    private readonly List<Item> _items = new();

    /// <summary>Open the menu flush with the button's right edge rather than its left.</summary>
    public bool AlignRight { get; set; }

    /// <summary>
    /// A line of warning drawn under the entries, or null. Store uses it when one of the berths
    /// listed is where an offer on the tab would land.
    /// </summary>
    public string? Note { get; set; }

    public DrydockMenuButton()
    {
        OnPressed += _ => OpenMenu();
    }

    public void SetItems(IEnumerable<Item> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Disabled = _items.Count == 0;
    }

    private void OpenMenu()
    {
        if (_items.Count == 0 || Root is not { } root)
            return;

        var popup = new Popup();
        var panel = new PanelContainer
        {
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = DrydockText.ListBackground,
                BorderColor = MenuBorder,
                BorderThickness = new Thickness(1),
            },
        };
        var list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, MinWidth = MenuWidth };
        panel.AddChild(list);
        popup.AddChild(panel);

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];

            // A rule between entries; the section rule is heavier and stands in for the row one.
            if (i > 0)
                list.AddChild(DrydockText.RulePanel(item.DividerAbove ? SectionRule : DrydockText.RowRule));

            // A flat row, not a button; the hover fill is painted by hand on the box inside.
            var entry = DrydockText.ItemRow(item, out var box);

            if (item.Enabled)
            {
                entry.OnMouseEntered += _ => box.BackgroundColor = DrydockText.RowHighlight;
                entry.OnMouseExited += _ => box.BackgroundColor = Color.Transparent;
            }

            var pressed = item.OnPressed;
            entry.OnPressed += _ =>
            {
                popup.Close();
                pressed?.Invoke();
            };
            list.AddChild(entry);
        }

        if (Note != null)
        {
            list.AddChild(new Label
            {
                Text = Note,
                Modulate = DrydockText.Warning,
                Margin = new Thickness(10, 6),
            });
        }

        // Added by hand rather than OpenAtMouse, which is the only path the engine orphans for
        // us, so the hide hook takes it out of the modal root again.
        popup.OnPopupHide += () => popup.Orphan();
        root.ModalRoot.AddChild(popup);

        var width = Math.Max(Width, MenuWidth);
        var origin = AlignRight
            ? new Vector2(GlobalPosition.X + Width - width, GlobalPosition.Y + Height)
            : new Vector2(GlobalPosition.X, GlobalPosition.Y + Height);
        popup.Open(UIBox2.FromDimensions(origin, new Vector2(width, 0)));
    }
}

// Triad: drydock tab.
using System.Text;
using Content.Client.Stylesheets;
using Content.Shared._Triad.Drydock;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// The one place the drydock tab writes a clock, a class name or a weighted name, so the card, the
/// rows, the alerts and the prompts all agree on the shape of the same fact, plus the small widgets
/// and the palette the drydock windows share.
/// </summary>
internal static class DrydockText
{
    public static readonly Color Dim = Color.FromHex("#999999");
    public static readonly Color Empty = Color.FromHex("#777777");
    public static readonly Color Disabled = Color.FromHex("#666666");
    public static readonly Color Warning = Color.FromHex("#d9a441");
    public static readonly Color Incoming = Color.FromHex("#8fb4dc");

    // The two marked frames: amber for anything in escrow (a row, the alert), blue for what an
    // offer would bring in.
    public static readonly Color AmberBorder = Color.FromHex("#8a6a3a");
    public static readonly Color AmberFill = Color.FromHex("#2a2418");
    public static readonly Color BlueBorder = Color.FromHex("#5b86b8");
    public static readonly Color BlueFill = Color.FromHex("#1a2430");

    // The impound lot: the tag, the reason on a locked card, the small print, and the two frames.
    // A card the owner can act on is a shade brighter than one an admin holds, as the canvas draws
    // them.
    public static readonly Color Impound = Color.FromHex("#cf4f4f");
    public static readonly Color ImpoundReason = Color.FromHex("#c07a7a");
    public static readonly Color Sub = Color.FromHex("#8a8a8a");
    public static readonly Color ImpoundBorder = Color.FromHex("#8a3f3f");
    public static readonly Color ImpoundLockedBorder = Color.FromHex("#7a3f3f");
    public static readonly Color ImpoundFill = Color.FromHex("#2a1a1a");

    /// <summary>The caret the two dropdown buttons wear, so a label reads as a menu and not a verb.</summary>
    public const string Caret = " ▾";

    /// <summary>How long an offer has left: whole minutes while there are any, seconds after that.</summary>
    public static string Left(int seconds)
    {
        return seconds >= 60
            ? Loc.GetString("shipyard-console-transfer-minutes-left", ("minutes", (int)Math.Ceiling(seconds / 60.0)))
            : Loc.GetString("shipyard-console-transfer-seconds-left", ("seconds", Math.Max(0, seconds)));
    }

    /// <summary>
    /// A size class as a label: the stored text with a space before each inner capital, so the
    /// enum's SuperCapital reads as Super Capital. Anything unparseable is shown as it came, since a
    /// row filed by an older build can name a class this build does not have.
    /// </summary>
    public static string Class(string? sizeClass)
    {
        if (string.IsNullOrEmpty(sizeClass))
            return "?";

        var sb = new StringBuilder(sizeClass.Length + 2);
        for (var i = 0; i < sizeClass.Length; i++)
        {
            var c = sizeClass[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(sizeClass[i - 1]))
                sb.Append(' ');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Appends text in the bold face at the size in force.</summary>
    public static void AddBold(this FormattedMessage message, string text)
    {
        message.PushTag(new MarkupNode("bold", null, null));
        message.AddText(text);
        message.Pop();
    }

    /// <summary>Appends text in the given colour.</summary>
    public static void AddColored(this FormattedMessage message, string text, Color color)
    {
        message.PushColor(color);
        message.AddText(text);
        message.Pop();
    }

    /// <summary>Appends bold text at a point size of its own; the card's ship name is the one user.</summary>
    public static void AddBoldSized(this FormattedMessage message, string text, int size)
    {
        message.PushTag(new MarkupNode("font", null, new Dictionary<string, MarkupParameter> { ["size"] = new MarkupParameter((long)size) }));
        message.AddBold(text);
        message.Pop();
    }

    // ---------------------------------------------------------------- Filter chips and cards

    /// <summary>A filter chip's label colour when it is not the active one.</summary>
    public static readonly Color ChipText = Color.FromHex("#b0b0b0");

    /// <summary>A filter chip's fill: the primary button blue when active, the panel's own grey otherwise.</summary>
    public static StyleBoxFlat ChipBox(bool on) => new()
    {
        BackgroundColor = on ? StyleNano.ButtonColorPrimaryDefault : Color.FromHex("#222226"),
        BorderColor = on ? BlueBorder : Color.FromHex("#3a3a3e"),
        BorderThickness = new Thickness(1),
    };

    /// <summary>Repaints one chip cell for whether it is the active filter.</summary>
    public static void RepaintChip(PanelContainer panel, Label label, bool on)
    {
        panel.PanelOverride = ChipBox(on);
        label.FontColorOverride = on ? Color.White : ChipText;
    }

    // ---------------------------------------------------------------- Dropdown and picker rows

    /// <summary>The fill behind the rows of a dropdown and of a list picker.</summary>
    public static readonly Color ListBackground = Color.FromHex("#141414");

    /// <summary>The 1px rule between two rows of a dropdown or a list picker.</summary>
    public static readonly Color RowRule = Color.FromHex("#262626");

    /// <summary>The fill of a dropdown row under the mouse, and of the selected row in a list picker.</summary>
    public static readonly Color RowHighlight = Color.FromHex("#2a3a4c");

    /// <summary>A 1px rule panel in the given colour: the row divider both the menu button and the list picker draw.</summary>
    public static PanelContainer RulePanel(Color color)
    {
        return new PanelContainer
        {
            PanelOverride = new StyleBoxFlat { BackgroundColor = color },
            MinHeight = 1,
            HorizontalExpand = true,
        };
    }

    /// <summary>The label and detail colours for one enabled/disabled row, shared by the menu button and the list picker.</summary>
    public static (Color Text, Color Detail) RowColors(bool enabled)
    {
        return (enabled ? Color.White : Disabled, enabled ? Dim : Disabled);
    }

    /// <summary>
    /// One row of a dropdown or a list picker: a flat button with no style class, so nothing draws a
    /// box around it, greyed when the item cannot be taken. Inside it a transparent fill holds the
    /// label and, 12px to its right, the detail, in <see cref="RowColors"/>. <paramref name="fill"/>
    /// is that fill's box, for the caller to paint a hover or a selection on. A
    /// <paramref name="rule"/>, when given, sits under the fill inside the button, so hiding the row
    /// hides its rule too. Pressing does nothing until the caller subscribes.
    /// </summary>
    public static ContainerButton ItemRow(DrydockMenuButton.Item item, out StyleBoxFlat fill, Control? rule = null)
    {
        fill = new StyleBoxFlat { BackgroundColor = Color.Transparent };
        var panel = new PanelContainer { PanelOverride = fill, HorizontalExpand = true };
        var (text, detail) = RowColors(item.Enabled);
        var line = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(10, 7), HorizontalExpand = true };
        line.AddChild(new Label { Text = item.Label, HorizontalExpand = true, Modulate = text });
        if (item.Detail != null)
            line.AddChild(new Label { Text = item.Detail, Modulate = detail, Margin = new Thickness(12, 0, 0, 0) });
        panel.AddChild(line);

        var row = new ContainerButton { Disabled = !item.Enabled, HorizontalExpand = true };
        if (rule == null)
        {
            row.AddChild(panel);
            return row;
        }

        var cell = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
        cell.AddChild(panel);
        cell.AddChild(rule);
        row.AddChild(cell);
        return row;
    }

    /// <summary>One labelled line of a card: the label in the key colour, the value clipped.</summary>
    public static Control CardLine(string key, string value, Color? colour)
    {
        var line = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        line.AddChild(new Label { Text = Loc.GetString(key), FontColorOverride = Dim });
        line.AddChild(new Label
        {
            Text = value,
            FontColorOverride = colour,
            ClipText = true,
            HorizontalExpand = true,
            Margin = new Thickness(4, 0, 0, 0),
        });
        return line;
    }

    /// <summary>The callsign first, then the name, by the deed's own split rule, as a ship card titles itself.</summary>
    public static string CardTitle(string fullName)
    {
        var (name, suffix) = DrydockNameRules.SplitShuttleName(fullName);
        return suffix == null
            ? fullName
            : Loc.GetString("drydock-admin-card-title", ("callsign", suffix), ("name", name));
    }
}

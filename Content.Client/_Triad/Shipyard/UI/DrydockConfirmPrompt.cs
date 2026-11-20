// Triad: legacy import.
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// A sentence and two buttons. <see cref="DrydockTextPrompt"/> is the same shape with a field in it,
/// and the field is the point there: sale and rename both need something typed before they commit.
/// An import needs no such gate, because nothing it does is destructive to something the player
/// cannot get back by retrieving the ship.
/// </summary>
public sealed class DrydockConfirmPrompt : FancyWindow
{
    public DrydockConfirmPrompt(string title, string body, string confirmLabel, Action onConfirm)
    {
        Title = title;
        // NaN height measures the contents; a fixed zero would clip them.
        SetSize = new Vector2(430, float.NaN);

        var column = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(14, 12) };

        var text = new RichTextLabel();
        text.SetMessage(FormattedMessage.FromMarkupPermissive(body));
        column.AddChild(text);

        var confirm = new Button { Text = confirmLabel, StyleClasses = { "ButtonSquare" }, MinWidth = 110 };
        var cancel = new Button { Text = Loc.GetString("shipyard-console-prompt-cancel"), StyleClasses = { "ButtonSquare" }, MinWidth = 90 };

        confirm.OnPressed += _ =>
        {
            onConfirm();
            Close();
        };
        cancel.OnPressed += _ => Close();

        var buttons = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
            SeparationOverride = 8,
        };
        buttons.AddChild(confirm);
        buttons.AddChild(cancel);
        column.AddChild(buttons);

        ContentsContainer.AddChild(column);
    }
}

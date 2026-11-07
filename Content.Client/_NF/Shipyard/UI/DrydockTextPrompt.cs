// Triad: drydock tab.
using System.Numerics;
using Content.Client.Stylesheets;
using Content.Client.UserInterface.Controls;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// A sentence, a field, and a button that stays locked until the field passes. The sale
/// confirmation (type the ship's name) and the rename (a name in the allowed shape) are both
/// this. The instructions sit in the field as its placeholder and vanish on the first keystroke;
/// the server applies the same rule again on receipt.
/// </summary>
public sealed class DrydockTextPrompt : FancyWindow
{
    private readonly LineEdit _field;
    private readonly Button _confirm;
    private readonly Label? _counter;
    private readonly Func<string, bool> _accepts;
    private readonly int? _maxLength;

    /// <param name="body">The sentence above the field, as markup, or null.</param>
    /// <param name="warning">A second line in the warning colour, or null. "Cannot be undone." on the sale.</param>
    /// <param name="destructive">Draws the verb in the caution red rather than the action blue.</param>
    public DrydockTextPrompt(
        string title,
        string? body,
        string? warning,
        string placeholder,
        string confirmLabel,
        Func<string, bool> accepts,
        int? maxLength,
        bool destructive,
        Action<string> onConfirm)
    {
        Title = title;
        // A NaN height is "measure the contents"; a zero height is a fixed zero, which the
        // window then clips its contents to.
        SetSize = new Vector2(430, float.NaN);
        _accepts = accepts;
        _maxLength = maxLength;

        var column = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(14, 12) };

        if (body != null)
        {
            var sentence = new RichTextLabel { Margin = new Thickness(0, 0, 0, 10) };
            sentence.SetMessage(FormattedMessage.FromMarkupPermissive(body));
            column.AddChild(sentence);
        }

        if (warning != null)
            column.AddChild(new Label { Text = warning, Modulate = DrydockText.Warning, Margin = new Thickness(0, 0, 0, 10) });

        var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, HorizontalExpand = true };
        _field = new LineEdit { PlaceHolder = placeholder, HorizontalExpand = true };
        row.AddChild(_field);
        if (maxLength != null)
        {
            _counter = new Label { Modulate = DrydockText.Dim, Margin = new Thickness(8, 0, 0, 0), MinWidth = 44, Align = Label.AlignMode.Right };
            row.AddChild(_counter);
        }
        column.AddChild(row);

        var buttons = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _confirm = new Button
        {
            Text = confirmLabel,
            StyleClasses = { "ButtonSquare", destructive ? "ButtonCaution" : StyleNano.ButtonPrimary },
            MinWidth = 110,
            Disabled = true,
        };
        var cancel = new Button { Text = Loc.GetString("shipyard-console-prompt-cancel"), StyleClasses = { "ButtonSquare" }, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        buttons.AddChild(_confirm);
        buttons.AddChild(cancel);
        column.AddChild(buttons);

        ContentsContainer.AddChild(column);

        _field.OnTextChanged += _ => Refresh();
        _field.OnTextEntered += _ =>
        {
            if (!_confirm.Disabled)
                Submit(onConfirm);
        };
        cancel.OnPressed += _ => Close();
        _confirm.OnPressed += _ => Submit(onConfirm);

        Refresh();
    }

    protected override void Opened()
    {
        base.Opened();
        _field.GrabKeyboardFocus();
    }

    private void Refresh()
    {
        var text = _field.Text;
        _confirm.Disabled = !_accepts(text);
        if (_counter != null && _maxLength is { } max)
        {
            _counter.Text = Loc.GetString("shipyard-console-rename-counter", ("count", text.Length), ("max", max));
            _counter.Modulate = text.Length > max ? Color.FromHex("#ff6a6a") : DrydockText.Dim;
        }
    }

    private void Submit(Action<string> onConfirm)
    {
        onConfirm(_field.Text);
        Close();
    }
}

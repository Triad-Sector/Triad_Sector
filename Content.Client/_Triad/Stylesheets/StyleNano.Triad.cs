// Triad: style classes of our own. Appended to the Nano sheet from one marked line in StyleNano so
// the upstream file carries two edits and none of the rules.
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using static Robust.Client.UserInterface.StylesheetHelpers;

namespace Content.Client.Stylesheets;

public sealed partial class StyleNano
{
    /// <summary>
    /// The blue the drydock draws its one main action in: Store, Retrieve, Accept, Offer. Built the
    /// way ButtonCaution is, as a modulate over the ordinary button texture, so every pseudo state
    /// the ordinary button has, this has too.
    /// </summary>
    public const string ButtonPrimary = "ButtonPrimary";

    public static readonly Color ButtonColorPrimaryDefault = Color.FromHex("#2f4c6f");
    public static readonly Color ButtonColorPrimaryHovered = Color.FromHex("#3d6290");
    public static readonly Color ButtonColorPrimaryPressed = Color.FromHex("#26405e");
    public static readonly Color ButtonColorPrimaryDisabled = Color.FromHex("#1e3149");

    private static IEnumerable<StyleRule> TriadRules()
    {
        yield return Element<ContainerButton>().Class(ContainerButton.StyleClassButton).Class(ButtonPrimary)
            .Pseudo(ContainerButton.StylePseudoClassNormal)
            .Prop(Control.StylePropertyModulateSelf, ButtonColorPrimaryDefault);

        yield return Element<ContainerButton>().Class(ContainerButton.StyleClassButton).Class(ButtonPrimary)
            .Pseudo(ContainerButton.StylePseudoClassHover)
            .Prop(Control.StylePropertyModulateSelf, ButtonColorPrimaryHovered);

        yield return Element<ContainerButton>().Class(ContainerButton.StyleClassButton).Class(ButtonPrimary)
            .Pseudo(ContainerButton.StylePseudoClassPressed)
            .Prop(Control.StylePropertyModulateSelf, ButtonColorPrimaryPressed);

        yield return Element<ContainerButton>().Class(ContainerButton.StyleClassButton).Class(ButtonPrimary)
            .Pseudo(ContainerButton.StylePseudoClassDisabled)
            .Prop(Control.StylePropertyModulateSelf, ButtonColorPrimaryDisabled);
    }
}

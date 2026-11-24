using Content.Shared._Triad.Weapons.Melee.EnergySword;
using Robust.Shared.GameStates;

namespace Content.Shared.Weapons.Melee.EnergySword;

[RegisterComponent, NetworkedComponent, Access(typeof(EnergySwordSystem), typeof(SharedTriadEnergySwordSystem))] // Triad - Triad system access
[AutoGenerateComponentState]
public sealed partial class EnergySwordComponent : Component
{
    /// <summary>
    /// What color the blade will be when activated.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Color ActivatedColor = Color.DodgerBlue;

    /// <summary>
    ///     A color option list for the random color picker.
    /// </summary>
    [DataField]
    public List<Color> ColorOptions = new()
    {
        Color.Tomato,
        Color.DodgerBlue,
        Color.Aqua,
        Color.MediumSpringGreen,
        Color.MediumOrchid
    };

    /// <summary>
    /// Whether the energy sword has been pulsed by a multitool,
    /// causing the blade to cycle RGB colors.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Hacked;

    /// <summary>
    /// Triad - For saves. This lets the system know that a color was already set, and it shouldn't be set again on map init.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool PickedColor = false;

    /// <summary>
    /// Triad - Emagged examine text
    /// </summary>
    [DataField, AutoNetworkedField]
    public LocId? EmaggedExamineText = "energy-sword-color-hacked-tool-tip";

    /// <summary>
    ///     RGB cycle rate for hacked e-swords.
    /// </summary>
    [DataField]
    public float CycleRate = 1f;

    // Frontier: block changing colour
    /// <summary>
    ///     RGB cycle rate for hacked e-swords.
    /// </summary>
    [DataField]
    public bool BlockHacking = false;
    // End Frontier
}

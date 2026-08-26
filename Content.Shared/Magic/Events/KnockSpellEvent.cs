using Content.Shared.Actions;

namespace Content.Shared.Magic.Events;

public sealed partial class KnockSpellEvent : InstantActionEvent, ISpeakSpell
{
    /// <summary>
    /// The range this spell opens doors in
    /// 10f is the default
    /// Should be able to open all doors/lockers in visible sight
    /// </summary>
    [DataField]
    public float Range = 10f;

    [DataField]
    public string? Speech { get; private set; }

    /// <summary>
    /// Triad: when set, only doors and lockers on this grid are opened. A wizard casting knock
    /// leaves it null and keeps the old behaviour; a grid-local caster (an artifact firing on its
    /// own ship) passes its grid so the spell cannot pop the airlocks of whatever is docked
    /// alongside.
    /// </summary>
    public EntityUid? OnlyGrid;
}

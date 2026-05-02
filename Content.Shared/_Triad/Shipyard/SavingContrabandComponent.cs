namespace Content.Shared._Triad.Shipyard;

/// <summary>
/// Entities with this component will not be saved when a ship is saved.
/// Optional field for examine text.
/// </summary>
[RegisterComponent]
public sealed partial class SavingContrabandComponent : Component
{
    [DataField]
    public LocId? ExamineText;
}

namespace Content.Shared._Triad.ContrabandPermit;

[RegisterComponent]
public sealed partial class SectorContrabandPermitsComponent : Component
{
    /// <summary>
    /// Stores all permit entries.
    /// Key is the permit owner, the list is the permitted items.
    /// </summary>
    [DataField]
    public Dictionary<NetEntity, List<NetEntity>> Records = new();
}

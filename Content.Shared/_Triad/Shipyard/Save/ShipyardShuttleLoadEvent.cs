namespace Content.Shared._Triad.Shipyard.Save;

public sealed class ShipyardShuttleLoadEvent(EntityUid shuttle, EntityUid purchaser)
{
    public EntityUid Shuttle { get; } = shuttle;
    public EntityUid Purchaser { get; } = purchaser;
}

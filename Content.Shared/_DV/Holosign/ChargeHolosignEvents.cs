namespace Content.Shared._DV.Holosign;

public record struct ChargeHolosignPlacedEvent(EntityUid Projector, EntityUid? User, EntityUid Sign);

public record struct ChargeHolosignRemovedEvent(EntityUid Projector, EntityUid? User, EntityUid Sign);


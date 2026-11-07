// Mono - file changed
using Content.Shared.Spreader;
using Robust.Shared.Prototypes;

namespace Content.Server.Spreader;

[RegisterComponent]
public sealed partial class SpreaderGridComponent : Component
{
    [DataField]
    public float UpdateAccumulator = 0f;

    [DataField]
    public float UpdateSpacing = 1f;

    // Triad: runtime work queues, rebuilt from the grid's spreaders on init and emptied every update.
    // As a data field the serializer refused the Entity<T> queue and aborted any save of a grid that
    // had ever held kudzu, foam or a puddle, ship saves and drydock stores included.
    // [DataField]
    [ViewVariables]
    public Dictionary<ProtoId<EdgeSpreaderPrototype>, Queue<Entity<EdgeSpreaderComponent>>> SpreadQueues = new();
}

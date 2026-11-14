using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Drydock;

/// <summary>
/// A class of entity a drydock retrieve deletes from the ship, the way research is reset on retrieve:
/// state that stays with the round rather than riding a hull, and not contraband, so no permit or
/// seizure is involved. An entity matches when its prototype carries one of <see cref="Components"/>,
/// descends from one of <see cref="Parents"/>, or is one of <see cref="Prototypes"/>. What a matched
/// entity holds that does not match itself is dropped onto the deck first.
/// </summary>
[Prototype]
public sealed partial class DrydockStripPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Component names, as written in YAML (<c>Mech</c>, not <c>MechComponent</c>).</summary>
    [DataField]
    public List<string> Components = new();

    /// <summary>
    /// Entity prototype ids whose descendants match, abstract parents included. Plain strings because
    /// the ids worth naming here are usually abstract, which a prototype-id field refuses; the drydock
    /// logs an error for any that names nothing.
    /// </summary>
    [DataField]
    public List<string> Parents = new();

    /// <summary>Individual entity prototypes, for items whose parents are shared with unrelated things.</summary>
    [DataField]
    public List<EntProtoId> Prototypes = new();
}

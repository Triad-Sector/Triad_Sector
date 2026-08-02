using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Dossier;

/// <summary>
///     Marks an entity as a TDF BuPers dossier paper-doll exemplar.
///     On MapInit (which the guidebook fires when embedding the entity via GuideEntityEmbed),
///     the system randomizes the entity's HumanoidAppearance (skin tone, hair, markings) and
///     applies random species-appropriate gender-aware undergarment markings. Pure marker
///     component — no per-species knobs.
/// </summary>
/// <remarks>
///     Sits in Shared (not Server) because the guidebook spawns its embedded entities
///     client-side; a server-only randomizer would never fire.
/// </remarks>
[RegisterComponent, NetworkedComponent]
public sealed partial class DossierExemplarComponent : Component
{
}

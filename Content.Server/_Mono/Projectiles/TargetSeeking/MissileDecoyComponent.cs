// Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
namespace Content.Server._Mono.Projectiles.TargetSeeking;

/// <summary>
/// Marks an entity as a missile decoy (e.g., a flare).
/// Missiles with a locked target will break their lock and retarget this entity
/// when it enters their detection range and scan arc.
/// </summary>
[RegisterComponent]
public sealed partial class MissileDecoyComponent : Component
{
}
// Triad: targeting lock code end

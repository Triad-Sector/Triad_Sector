using Content.Server.Atmos.Piping.Components;
using Content.Server.Atmos.Piping.EntitySystems;
using Content.Shared.RCD;
using Content.Shared.RPD;
using Content.Shared.RPD.Components;

namespace Content.Server.RPD;

/// <summary>
/// Server-side RPD half: stains a freshly built pipe/atmos device with the operator's selected palette color via
/// the canonical <see cref="AtmosPipeColorComponent"/>. Server-side because that component (and its setter) live on
/// the server; setting it is what makes the color real, it serializes with the ship save and drives the atmos
/// monitoring console, where an appearance-only write would be lost on reload and read white on consoles.
/// </summary>
public sealed class RPDPipeColorSystem : EntitySystem
{
    [Dependency] private readonly AtmosPipeColorSystem _pipeColor = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RPDComponent, RCDObjectSpawnedEvent>(OnObjectSpawned);
    }

    private void OnObjectSpawned(Entity<RPDComponent> ent, ref RCDObjectSpawnedEvent args)
    {
        // The default palette slot means "keep the prototype color"; nothing to stain.
        if (ent.Comp.PipeColor == RPDPalette.DefaultKey)
            return;

        if (!RPDPalette.Colors.TryGetValue(ent.Comp.PipeColor, out var palette) || palette is not { } color)
            return;

        // Entities without the component (air alarms, air sensors) have no pipe color to set, so they skip.
        if (!TryComp<AtmosPipeColorComponent>(args.Spawned, out var pipeColor))
            return;

        _pipeColor.SetColor(args.Spawned, pipeColor, color);
    }
}

using System.Numerics;
using Content.Shared.Light.Components;
using Robust.Shared.Timing;

namespace Content.Shared.Light.EntitySystems;

/// <summary>
/// System for assigning random values to <see cref="SharedPointLightComponent"/> variables when given <see cref="RandomPointLightComponent"/>
/// </summary>
public sealed partial class RandomPointLightSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPointLightSystem _light = default!;

    [SubscribeLocalEvent]
    private void RandomLight(Entity<RandomPointLightComponent> ent, ref ComponentStartup args)
    {
        if (_timing.ApplyingState)
            return;

        var rpl = ent.Comp;

        // Triad: no PredictedRandom helper on this tree; seeding from the net id keeps client and server in step.
        var rand = new System.Random(GetNetEntity(ent).Id);
        float Next(float min, float max) => min + rand.NextSingle() * (max - min);
        // Keeping the V variable between 0.5 and 1.0 so that it's always bright
        var hsv = new Vector4(
            Next(0, 1),
            Next(0, 1),
            Next(0.5f, 1),
            1
        );

        var color = Color.FromHsv(hsv);

        _light.SetRadius(ent, Next(rpl.MinRadius, rpl.MaxRadius));
        _light.SetEnergy(ent, Next(rpl.MinEnergy, rpl.MaxEnergy));
        _light.SetColor(ent, color);
    }
}

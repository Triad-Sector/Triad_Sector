using Content.Shared.Physics;
using Content.Shared.Tag;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Shuttles;

public abstract partial class SharedTriadThrusterSystem : EntitySystem
{
    [Dependency] protected TagSystem _tag = default!;
    [Dependency] protected EntityWhitelistSystem _whitelist = default!;

    protected const CollisionGroup StructureMask = CollisionGroup.FullTileMask;

    protected const int MaxGrilles = 3;
    protected static readonly ProtoId<TagPrototype> GrilleTag = "Grille";

    protected static readonly EntityWhitelist BlockThrusterWhitelist =
        new EntityWhitelist
        {
            Components = new [] { "Door" },
            Tags = new List<ProtoId<TagPrototype>> { "Window", "Wall", "Diagonal" }
        };

    public static readonly List<ThrusterBlockingProfileData> ThrusterBlockingProfileTable =
        new List<ThrusterBlockingProfileData>()
    {
        // Always blocked
        new ThrusterBlockingProfileData(
            new List<ThrusterBlockingRayData>(), // Explicitly initialize empty lists
            8
        ),

        // Never blocked
        new ThrusterBlockingProfileData(
            new List<ThrusterBlockingRayData>(),
            0
        ),

        // Small slash normal thruster
        new ThrusterBlockingProfileData(
            new List<ThrusterBlockingRayData>()
            {
                new ThrusterBlockingRayData(angle: 180d, offsetX: 0.0f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: 135d, offsetX: 0.0f, offsetY: 0.15f),
                new ThrusterBlockingRayData(angle: 103d, offsetX: -0.45f, offsetY: 0.05f),
                new ThrusterBlockingRayData(angle: 90d, offsetX: 0.0f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: 77d, offsetX: -0.45f, offsetY: 0.05f),
                new ThrusterBlockingRayData(angle: 63d, offsetX: 0.45f, offsetY: 0.05f),
                new ThrusterBlockingRayData(angle: 33d, offsetX: 0.0f, offsetY: 0.25f),
                new ThrusterBlockingRayData(angle: 24.5d, offsetX: 0.25f, offsetY: 0.45f),
                new ThrusterBlockingRayData(angle: 17d, offsetX: 0.25f, offsetY: 0.45f),
                new ThrusterBlockingRayData(angle: 0d, offsetX: 0.0f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: -17d, offsetX: -0.25f, offsetY: 0.45f),
                new ThrusterBlockingRayData(angle: -24.5d, offsetX: -0.25f, offsetY: 0.45f),
                new ThrusterBlockingRayData(angle: -33d, offsetX: 0.0f, offsetY: 0.25f),
                new ThrusterBlockingRayData(angle: -63d, offsetX: -0.45f, offsetY: 0.05f),
                new ThrusterBlockingRayData(angle: -77d, offsetX: 0.45f, offsetY: 0.05f),
                new ThrusterBlockingRayData(angle: -90d, offsetX: 0.0f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: -103d, offsetX: 0.45f, offsetY: 0.05f),
                new ThrusterBlockingRayData(angle: -135d, offsetX: 0.0f, offsetY: 0.15f),
            },
            2 // 2 min quality
        ),

        // Large thruster
        new ThrusterBlockingProfileData(
            new List<ThrusterBlockingRayData>()
            {
                new ThrusterBlockingRayData(angle: 180d, offsetX: 0.0f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: 180d, offsetX: 1.0f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: 116.6d, offsetX: 0.5f, offsetY: -0.45f),
                new ThrusterBlockingRayData(angle: -116.6d, offsetX: 0.5f, offsetY: -0.45f),
                new ThrusterBlockingRayData(angle: 90d, offsetX: 1.0f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: 90d, offsetX: 1.0f, offsetY: -1.0f),
                new ThrusterBlockingRayData(angle: -90d, offsetX: 0.0f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: -90d, offsetX: 0.0f, offsetY: -1.0f),
                new ThrusterBlockingRayData(angle: 62d, offsetX: 0.5f, offsetY: -0.55f),
                new ThrusterBlockingRayData(angle: -62d, offsetX: 0.5f, offsetY: -0.55f),
                new ThrusterBlockingRayData(angle: 53d, offsetX: 0.5f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: -53d, offsetX: 0.5f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: 30.5d, offsetX: 0.5f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: -30.5d, offsetX: 0.5f, offsetY: 0.0f),
                new ThrusterBlockingRayData(angle: 24.5d, offsetX: 0.25f, offsetY: 0.45f),
                new ThrusterBlockingRayData(angle: -24.5d, offsetX: 0.75f, offsetY: 0.45f),
                new ThrusterBlockingRayData(angle: 17d, offsetX: 0.45f, offsetY: 0.44f),
                new ThrusterBlockingRayData(angle: -17d, offsetX: 0.55f, offsetY: 0.44f),
                new ThrusterBlockingRayData(angle: 5d, offsetX: 0.45f, offsetY: 0.44f),
                new ThrusterBlockingRayData(angle: -5d, offsetX: 0.55f, offsetY: 0.44f),
                new ThrusterBlockingRayData(angle: 0d, offsetX: 0.0f, offsetY: -1.0f),
                new ThrusterBlockingRayData(angle: 0d, offsetX: 1.0f, offsetY: -1.0f)
            },
            3 // 3 min quality
        ),
    };
}

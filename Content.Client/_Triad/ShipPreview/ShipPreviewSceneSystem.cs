using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Shared.SubFloor;
using Robust.Client.GameObjects;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;

namespace Content.Client._Triad.ShipPreview;

public sealed partial class ShipPreviewSceneSystem : EntitySystem
{
    [Dependency] private IComponentFactory _factory = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    private readonly HashSet<ShipPreviewScene> _openScenes = new();
    private readonly Dictionary<EntProtoId, EntityUid> _spriteTemplates = new();
    private bool _loadingShip;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MetaDataComponent, ComponentInit>(OnEntityInit);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        foreach (var scene in _openScenes)
        {
            DeleteSceneMap(scene);
        }

        _openScenes.Clear();
    }

    public ShipPreviewScene OpenScene()
    {
        var scene = new ShipPreviewScene();
        _openScenes.Add(scene);
        return scene;
    }

    public void CloseScene(ShipPreviewScene scene)
    {
        DeleteSceneMap(scene);
        _openScenes.Remove(scene);
    }

    public bool TryShowShip(ShipPreviewScene scene, ResPath shipFile)
    {
        if (IsAlreadyShowing(scene, shipFile))
            return true;

        RemoveShip(scene);
        EnsureSceneHasMap(scene);

        if (!TryLoadShipGrid(scene, shipFile, out var ship))
            return false;

        _transform.SetLocalPositionRotation(ship.Value.Owner, Vector2.Zero, Angle.Zero);
        HideUnderfloorEntities(ship.Value.Owner);
        StoreShipDetails(scene, ship.Value, shipFile);
        return true;
    }

    public void RemoveShip(ShipPreviewScene scene)
    {
        if (scene.Ship is { } ship && Exists(ship))
            Del(ship);

        ForgetShipDetails(scene);
    }

    private void OnEntityInit(EntityUid uid, MetaDataComponent metaData, ComponentInit args)
    {
        if (!_loadingShip
            || metaData.EntityPrototype is not { } prototype
            || !TryComp<SpriteComponent>(uid, out var sprite))
            return;

        var registration = _factory.GetRegistration<SpriteComponent>();

        if (!prototype.Components.TryGetValue(registration.Name, out var spriteData))
            return;

        if (!_spriteTemplates.TryGetValue(prototype.ID, out var template))
        {
            template = BuildSpriteTemplate(registration, spriteData.Component);
            _spriteTemplates.Add(prototype.ID, template);
        }

        _sprite.CopySprite(template, (uid, sprite));
    }

    private EntityUid BuildSpriteTemplate(ComponentRegistration registration, IComponent spriteData)
    {
        var template = EntityManager.Spawn();

        EntityPrototype.EnsureCompExistsAndDeserialize(
            template,
            registration,
            _factory,
            EntityManager,
            _serialization,
            registration.Name,
            spriteData,
            null);

        return template;
    }

    private void DeleteSpriteTemplates()
    {
        foreach (var template in _spriteTemplates.Values)
        {
            Del(template);
        }

        _spriteTemplates.Clear();
    }

    private void HideUnderfloorEntities(EntityUid ship)
    {
        var underfloor = EntityQueryEnumerator<SubFloorHideComponent, AppearanceComponent, TransformComponent>();

        while (underfloor.MoveNext(out var uid, out _, out var appearance, out var xform))
        {
            if (xform.GridUid == ship)
                _appearance.SetData(uid, SubFloorVisuals.Covered, true, appearance);
        }
    }

    private void IgnoreServerOnlyComponents(ResPath shipFile)
    {
        if (!_mapLoader.TryReadFile(shipFile, out var shipData))
            return;

        var serverOnly = ComponentNamesIn(shipData)
            .Where(name => _factory.GetComponentAvailability(name) == ComponentAvailability.Ignore)
            .Where(name => !_factory.IsIgnored(name))
            .ToHashSet();

        if (serverOnly.Count > 0)
            _factory.RegisterIgnore(serverOnly.ToArray());
    }

    private static IEnumerable<string> ComponentNamesIn(MappingDataNode shipData)
    {
        if (!shipData.TryGet("entities", out SequenceDataNode? prototypeGroups))
            yield break;

        foreach (var group in prototypeGroups.OfType<MappingDataNode>())
        {
            if (!group.TryGet("entities", out SequenceDataNode? entities))
                continue;

            foreach (var entity in entities.OfType<MappingDataNode>())
            {
                if (!entity.TryGet("components", out SequenceDataNode? components))
                    continue;

                foreach (var component in components.OfType<MappingDataNode>())
                {
                    if (component.TryGet("type", out ValueDataNode? type))
                        yield return type.Value;
                }
            }
        }
    }

    private bool IsAlreadyShowing(ShipPreviewScene scene, ResPath shipFile)
    {
        return scene.ShipFile == shipFile && scene.Ship is { } ship && Exists(ship);
    }

    private void EnsureSceneHasMap(ShipPreviewScene scene)
    {
        if (SceneMapExists(scene))
            return;

        ForgetShipDetails(scene);
        scene.MapEntity = _map.CreateMap(out var mapId, runMapInit: false);
        scene.MapId = mapId;
    }

    private bool SceneMapExists(ShipPreviewScene scene)
    {
        return Exists(scene.MapEntity)
               && _map.TryGetMap(scene.MapId, out var mapEntity)
               && mapEntity == scene.MapEntity;
    }

    private bool TryLoadShipGrid(
        ShipPreviewScene scene,
        ResPath shipFile,
        [NotNullWhen(true)] out Entity<MapGridComponent>? ship)
    {
        var options = new DeserializationOptions
        {
            InitializeMaps = false,
            PauseMaps = true,
        };

        _loadingShip = true;

        try
        {
            IgnoreServerOnlyComponents(shipFile);
            return _mapLoader.TryLoadGrid(scene.MapId, shipFile, out ship, options);
        }
        catch (Exception exception)
        {
            Log.Error($"Could not load ship preview grid {shipFile}: {exception}");
            ship = null;
            return false;
        }
        finally
        {
            _loadingShip = false;
            DeleteSpriteTemplates();
        }
    }

    private void StoreShipDetails(ShipPreviewScene scene, Entity<MapGridComponent> ship, ResPath shipFile)
    {
        var bounds = ship.Comp.LocalAABB;

        scene.Ship = ship.Owner;
        scene.ShipFile = shipFile;
        scene.ShipBounds = bounds;
        scene.ShipSizeInTiles = MeasureInTiles(bounds, ship.Comp.TileSize);
        scene.ShipTileCount = CountTiles(ship);
    }

    private static Vector2i MeasureInTiles(Box2 bounds, ushort tileSize)
    {
        return new Vector2i(
            (int) MathF.Round(bounds.Width / tileSize),
            (int) MathF.Round(bounds.Height / tileSize));
    }

    private int CountTiles(Entity<MapGridComponent> ship)
    {
        var tileCount = 0;
        var tiles = _map.GetAllTilesEnumerator(ship.Owner, ship.Comp);

        while (tiles.MoveNext(out _))
        {
            tileCount++;
        }

        return tileCount;
    }

    private void DeleteSceneMap(ShipPreviewScene scene)
    {
        if (SceneMapExists(scene))
            Del(scene.MapEntity);

        ForgetShipDetails(scene);
        scene.MapEntity = EntityUid.Invalid;
        scene.MapId = default;
    }

    private static void ForgetShipDetails(ShipPreviewScene scene)
    {
        scene.Ship = null;
        scene.ShipFile = null;
        scene.ShipBounds = default;
        scene.ShipSizeInTiles = Vector2i.Zero;
        scene.ShipTileCount = 0;
    }
}

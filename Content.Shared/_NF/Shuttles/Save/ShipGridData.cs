using System.Numerics;

namespace Content.Shared._NF.Shuttles.Save
{
    [Serializable]
    [DataDefinition]
    public sealed partial class ShipGridData // Added partial
    {
        [DataField("metadata")]
        public ShipMetadata Metadata { get; set; } = new();

        // NOTE: Legacy saves used 'meta' instead of 'metadata'. We no longer map it here because
        // the source generator attempted to access it for serialization (causing CS0154). The
        // loader now rewrites a top-level 'meta:' key to 'metadata:' prior to deserialization.

        [DataField("grids")]
        public List<GridData> Grids { get; set; } = new();
    }

    [Serializable]
    [DataDefinition]
    public sealed partial class ShipMetadata // Added partial
    {
        [DataField("format_version")]
        public int FormatVersion { get; set; } = 1;

        [DataField("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [DataField("original_grid_id")]
        public string OriginalGridId { get; set; } = string.Empty;

        [DataField("player_id")]
        public string PlayerId { get; set; } = string.Empty;

        [DataField("ship_name")]
        public string ShipName { get; set; } = string.Empty;

        // Original grid rotation in radians (local rotation of the saved grid root) so we can restore
        // the ship's orientation prior to attempting auto-dock. Legacy / old saves will default to 0.
        [DataField("original_grid_rotation")]
        public float OriginalGridRotation { get; set; } = 0f;

        // Legacy checksum field - ignored but kept for backward compatibility with old ship files
        [DataField("checksum")]
        public string Checksum { get; set; } = string.Empty;

        // Add other relevant metadata as needed, e.g., game version, server ID
    }

    [Serializable]
    [DataDefinition]
    public sealed partial class GridData // Added partial
    {
        [DataField("grid_id")]
        public string GridId { get; set; } = string.Empty;

        [DataField("tiles")]
        public List<TileData> Tiles { get; set; } = new();

        [DataField("entities")]
        public List<EntityData> Entities { get; set; } = new();

        [DataField("atmosphere")]
        public string? AtmosphereData { get; set; } = null;

        [DataField("decals")]
        public string? DecalData { get; set; } = null;
    }

    [Serializable]
    [DataDefinition]
    public sealed partial class TileData // Added partial
    {
        [DataField("x")]
        public int X { get; set; }

        [DataField("y")]
        public int Y { get; set; }

        [DataField("tile_type")]
        public string TileType { get; set; } = string.Empty; // This might need to be a more specific type or ID
    }

    [Serializable]
    [DataDefinition]
    public sealed partial class EntityData // Added partial
    {
        [DataField("entity_id")]
        public string EntityId { get; set; } = string.Empty;

        [DataField("prototype")]
        public string Prototype { get; set; } = string.Empty;

        [DataField("position")]
        public Vector2 Position { get; set; } = Vector2.Zero;

        [DataField("rotation")]
        public float Rotation { get; set; } = 0.0f;

        [DataField("components")]
        public List<ComponentData> Components { get; set; } = new();

        // Container relationship data
        [DataField("parent_container_entity")]
        public string? ParentContainerEntity { get; set; } = null;

        [DataField("container_slot")]
        public string? ContainerSlot { get; set; } = null;

        [DataField("is_container")]
        public bool IsContainer { get; set; } = false;

        [DataField("is_contained")]
        public bool IsContained { get; set; } = false;
    }

    [Serializable]
    [DataDefinition]
    public sealed partial class ComponentData // Added partial
    {
        [DataField("type")]
        public string Type { get; set; } = string.Empty;

        // Serialized component data as YAML string for robust handling
        [DataField("yaml_data")]
        public string YamlData { get; set; } = string.Empty;

        // Backup properties dictionary for simple key-value data
        [DataField("properties")]
        public Dictionary<string, object> Properties { get; set; } = new();

        // Component registration information
        [DataField("net_id")]
        public ushort NetId { get; set; } = 0;
    }
}

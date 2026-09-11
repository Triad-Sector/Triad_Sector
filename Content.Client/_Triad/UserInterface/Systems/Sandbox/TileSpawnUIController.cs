using System.Linq;
using Robust.Client.Graphics;
using Robust.Client.Placement;
using Robust.Client.Placement.Modes;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using static Robust.Client.UserInterface.Controls.BaseButton;

namespace Content.Client._Triad.UserInterface.Systems.Sandbox;

/// <summary>
/// Drives the tile spawn window in place of the engine's <see cref="TileSpawningUIController"/>.
/// </summary>
/// <remarks>
/// <para>
/// The engine controller hands <see cref="ItemList"/> the raw sprite a tile declares, and a tile sprite is
/// a horizontal strip of its variants. Two things fall out of that. The item icon draws the whole strip
/// rather than one tile, and <c>ItemList.Item.IconSize</c> reports the strip's width, which
/// <c>ItemList.Draw</c> adds to the left edge of the label's box. Once a strip is wider than the list's
/// content box, that box is built inverted and trips the assert inside <c>UIBox2</c>: measured at 320 px
/// against 187 px on <c>FloorAsphalt</c>, which declares ten variants. Builds with asserts compiled in
/// take the client down when the menu is opened; builds without them silently draw it wrong.
/// </para>
/// <para>
/// Setting <c>IconRegion</c> to one variant cell fixes both, because <c>IconSize</c> returns the region's
/// size when one is set and <c>Draw</c> switches to the sub-rect path.
/// </para>
/// <para>
/// This reuses the engine's window, so layout changes upstream still reach us. The engine controller stays
/// registered but is left inert: every one of its handlers returns early while its window is null.
/// </para>
/// </remarks>
public sealed partial class TileSpawnUIController : UIController
{
    [Dependency] private IPlacementManager _placement = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;

    private TileSpawnWindow? _window;

    private readonly List<ITileDefinition> _shownTiles = new();
    private bool _clearingTileSelections;
    private bool _eraseTile;
    private bool _mirrorableTile;
    private bool _mirroredTile;

    public override void Initialize()
    {
        _placement.PlacementChanged += ClearTileSelection;
        _placement.DirectionChanged += OnDirectionChanged;
        _placement.MirroredChanged += OnMirroredChanged;
    }

    public void ToggleWindow()
    {
        EnsureWindow();

        if (_window!.IsOpen)
        {
            _window.Close();
            return;
        }

        _window.Open();
        UpdateEntityDirectionLabel();
        UpdateMirroredButton();
        _window.SearchBar.GrabKeyboardFocus();
    }

    public void CloseWindow()
    {
        if (_window is not { Disposed: false })
            return;

        _window.Close();
    }

    private void EnsureWindow()
    {
        if (_window is { Disposed: false })
            return;

        _window = UIManager.CreateWindow<TileSpawnWindow>();
        LayoutContainer.SetAnchorPreset(_window, LayoutContainer.LayoutPreset.CenterLeft);
        _window.ClearButton.OnPressed += OnTileClearPressed;
        _window.SearchBar.OnTextChanged += OnTileSearchChanged;
        _window.TileList.OnItemSelected += OnTileItemSelected;
        _window.TileList.OnItemDeselected += OnTileItemDeselected;
        _window.EraseButton.Pressed = _eraseTile;
        _window.EraseButton.OnToggled += OnTileEraseToggled;
        _window.MirroredButton.Disabled = !_mirrorableTile;
        _window.RotationLabel.FontColorOverride = _mirrorableTile ? Color.White : Color.Gray;
        _window.MirroredButton.Pressed = _mirroredTile;
        _window.MirroredButton.OnToggled += OnTileMirroredToggled;
        BuildTileList();
    }

    private void StartTilePlacement(int tileType)
    {
        _placement.BeginPlacing(new PlacementInformation
        {
            PlacementOption = nameof(AlignTileAny),
            TileType = tileType,
            Range = 400,
            IsTile = true,
        });
    }

    private void OnTileEraseToggled(ButtonToggledEventArgs args)
    {
        if (_window is not { Disposed: false })
            return;

        _placement.Clear();

        if (args.Pressed)
        {
            _eraseTile = true;
            StartTilePlacement(0);
        }
        else
        {
            _eraseTile = false;
        }

        args.Button.Pressed = args.Pressed;
    }

    private void OnTileMirroredToggled(ButtonToggledEventArgs args)
    {
        if (_window is not { Disposed: false })
            return;

        _placement.Mirrored = args.Pressed;
        _mirroredTile = _placement.Mirrored;
        args.Button.Pressed = args.Pressed;
    }

    private void ClearTileSelection(object? sender, EventArgs e)
    {
        if (_window is not { Disposed: false })
            return;

        _clearingTileSelections = true;
        _window.TileList.ClearSelected();
        _clearingTileSelections = false;
        _window.EraseButton.Pressed = false;
        _window.MirroredButton.Pressed = _placement.Mirrored;
    }

    private void OnTileClearPressed(ButtonEventArgs args)
    {
        if (_window is not { Disposed: false })
            return;

        _window.TileList.ClearSelected();
        _placement.Clear();
        _window.SearchBar.Clear();
        BuildTileList(string.Empty);
        _window.ClearButton.Disabled = true;
    }

    private void OnTileSearchChanged(LineEdit.LineEditEventArgs args)
    {
        if (_window is not { Disposed: false })
            return;

        _window.TileList.ClearSelected();
        _placement.Clear();
        BuildTileList(args.Text);
        _window.ClearButton.Disabled = string.IsNullOrEmpty(args.Text);
    }

    private void OnTileItemSelected(ItemList.ItemListSelectedEventArgs args)
    {
        StartTilePlacement(_shownTiles[args.ItemIndex].TileId);
        UpdateMirroredButton();
    }

    private void OnTileItemDeselected(ItemList.ItemListDeselectedEventArgs args)
    {
        if (_clearingTileSelections)
            return;

        _placement.Clear();
    }

    private void OnDirectionChanged(object? sender, EventArgs e)
    {
        UpdateEntityDirectionLabel();
    }

    private void UpdateEntityDirectionLabel()
    {
        if (_window is not { Disposed: false })
            return;

        // The engine leaves this as a bare direction word, which reads like an unexplained "South" sitting
        // under the tile list. Caption it so it says what it is.
        _window.RotationLabel.Text = Loc.GetString("tile-spawn-window-rotation-label",
            ("direction", _placement.Direction.ToString()));
    }

    private void OnMirroredChanged(object? sender, EventArgs e)
    {
        UpdateMirroredButton();
    }

    private void UpdateMirroredButton()
    {
        if (_window is not { Disposed: false })
            return;

        if (_placement.CurrentPermission is { IsTile: true } permission)
        {
            _mirrorableTile = _tiles[permission.TileType].AllowRotationMirror;
            _window.MirroredButton.Disabled = !_mirrorableTile;
            _window.RotationLabel.FontColorOverride = _mirrorableTile ? Color.White : Color.Gray;
        }

        _mirroredTile = _placement.Mirrored;
        _window.MirroredButton.Pressed = _mirroredTile;
    }

    private void BuildTileList(string? searchStr = null)
    {
        if (_window is not { Disposed: false })
            return;

        _window.TileList.Clear();

        IEnumerable<ITileDefinition> tileDefs = _tiles.Where(def => !def.EditorHidden);

        if (!string.IsNullOrEmpty(searchStr))
        {
            tileDefs = tileDefs.Where(s =>
                Loc.GetString(s.Name).Contains(searchStr, StringComparison.CurrentCultureIgnoreCase) ||
                s.ID.Contains(searchStr, StringComparison.OrdinalIgnoreCase));
        }

        _shownTiles.Clear();
        _shownTiles.AddRange(tileDefs.OrderBy(d => Loc.GetString(d.Name)));

        foreach (var entry in _shownTiles)
        {
            _window.TileList.AddItem(Loc.GetString(entry.Name), ResolveIcon(entry));
        }
    }

    /// <summary>
    /// Loads a tile's sprite and narrows it to a single variant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tile sprite is a horizontal strip of square variant cells, so one cell is the sheet's height wide
    /// and the first is the tile on its own. The cell size is taken from the texture rather than from the
    /// declared variant count, because at least one tile in the fork chain declares fewer variants than its
    /// sheet actually holds and cropping by that number lands several tiles wide.
    /// </para>
    /// <para>
    /// The crop is an <see cref="AtlasTexture"/> rather than <c>ItemList.Item.IconRegion</c>. Setting the
    /// region does fix <c>IconSize</c>, which is what keeps the label's box valid, but <c>ItemList.Draw</c>
    /// still sizes the destination rectangle from <c>Icon.Size</c> instead of the region, so the cropped
    /// cell gets stretched across the sheet's full width. Handing it an already-narrow texture sidesteps
    /// that: <c>AtlasTexture</c> reports the sub-region as its own size, so both the source and the
    /// destination come out right.
    /// </para>
    /// <para>
    /// The engine calls <c>GetResource</c> for this, which throws and takes the whole menu with it if any
    /// one tile names a sprite that is not on disk.
    /// </para>
    /// </remarks>
    private Texture? ResolveIcon(ITileDefinition entry)
    {
        if (entry.Sprite is not { } sprite)
            return null;

        if (!_resources.TryGetResource<TextureResource>(sprite, out var resource))
        {
            Logger.GetSawmill("tilespawn").Warning($"Tile {entry.ID} names a sprite that could not be loaded: {sprite}");
            return null;
        }

        var texture = (Texture)resource;
        var cell = Math.Min(texture.Width, texture.Height);

        if (cell <= 0 || cell >= texture.Width)
            return texture;

        return new AtlasTexture(texture, UIBox2.FromDimensions(0, 0, cell, cell));
    }
}

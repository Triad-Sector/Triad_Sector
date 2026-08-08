// SPDX-FileCopyrightText: 2025 Steve <marlumpy@gmail.com>
// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.Popups;
using Content.Client.UserInterface.Controls;
using Content.Shared.RCD;
using Content.Shared.RCD.Components;
using Content.Shared.RPD;
using Content.Shared.RPD.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.Collections;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.RPD;

/// <summary>
/// Opens an <see cref="RPDMenu"/> populated with the shared <see cref="RPDPalette"/> and the RPD's build options.
/// Build selection is forwarded to the server via <see cref="RCDSystemMessage"/>, color selection via
/// <see cref="RPDColorChangeMessage"/>. Mirrors the button-model conversion in
/// <see cref="Content.Client.RCD.RCDMenuBoundUserInterface"/> with the RPD's atmos categories.
/// </summary>
public sealed class RPDMenuBoundUserInterface : BoundUserInterface
{
    private const string TopLevelActionCategory = "Main";

    // Triad: category icons mirror the entities we construct so a fork-side sprite swap propagates to the picker.
    private static readonly Dictionary<string, (string Tooltip, SpriteSpecifier Sprite)> PrototypesGroupingInfo
        = new Dictionary<string, (string Tooltip, SpriteSpecifier Sprite)>
        {
            ["Piping"] = ("rcd-component-piping", new SpriteSpecifier.Rsi(new ResPath("/Textures/Structures/Piping/Atmospherics/pipe.rsi"), "pipeFourway")),
            ["AtmosphericUtility"] = ("rcd-component-atmosphericutility", new SpriteSpecifier.Rsi(new ResPath("/Textures/Structures/Piping/Atmospherics/gascanisterport.rsi"), "gasCanisterPort")),
            ["PumpsValves"] = ("rcd-component-pumps", new SpriteSpecifier.Rsi(new ResPath("/Textures/Structures/Piping/Atmospherics/pump.rsi"), "pumpVolume")),
            ["Vents"] = ("rcd-component-vents", new SpriteSpecifier.Rsi(new ResPath("/Textures/_NF/Structures/Piping/Atmospherics/vent.rsi"), "vent_passive")),
            ["SensorsMonitors"] = ("rcd-component-sensorsmonitors", new SpriteSpecifier.Rsi(new ResPath("/Textures/Structures/Wallmounts/air_monitors.rsi"), "alarm0")),
        };

    [Dependency] private readonly IClyde _displayManager = default!;
    [Dependency] private readonly IInputManager _inputManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly ISharedPlayerManager _playerManager = default!;

    private RPDMenu? _menu;

    public RPDMenuBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        IoCManager.InjectDependencies(this);
    }

    protected override void Open()
    {
        base.Open();

        if (!EntMan.TryGetComponent<RCDComponent>(Owner, out var rcd)
            || !EntMan.TryGetComponent<RPDComponent>(Owner, out var rpd))
            return;

        _menu = this.CreateWindow<RPDMenu>();
        _menu.SetRadialButtons(ConvertToButtons(rcd.AvailablePrototypes));
        _menu.ColorSelected += OnColorSelected;

        var selectedColor = RPDPalette.IsValid(rpd.PipeColor)
            ? rpd.PipeColor
            : RPDPalette.DefaultKey;
        _menu.Populate(RPDPalette.Colors, selectedColor);

        var vpSize = _displayManager.ScreenSize;
        _menu.OpenCenteredAt(_inputManager.MouseScreenPosition.Position / vpSize);
    }

    private IEnumerable<RadialMenuOptionBase> ConvertToButtons(HashSet<ProtoId<RCDPrototype>> prototypes)
    {
        Dictionary<string, List<RadialMenuActionOptionBase>> buttonsByCategory = new();
        ValueList<RadialMenuActionOptionBase> topLevelActions = new();
        foreach (var protoId in prototypes)
        {
            var prototype = _prototypeManager.Index(protoId);
            if (prototype.Category == TopLevelActionCategory)
            {
                var topLevelActionOption = new RadialMenuActionOption<RCDPrototype>(HandleMenuOptionClick, prototype)
                {
                    IconSpecifier = RadialMenuIconSpecifier.With(prototype.Sprite),
                    ToolTip = GetTooltip(prototype)
                };
                topLevelActions.Add(topLevelActionOption);
                continue;
            }

            if (!PrototypesGroupingInfo.TryGetValue(prototype.Category, out var groupInfo))
                continue;

            if (!buttonsByCategory.TryGetValue(prototype.Category, out var list))
            {
                list = new List<RadialMenuActionOptionBase>();
                buttonsByCategory.Add(prototype.Category, list);
            }

            var actionOption = new RadialMenuActionOption<RCDPrototype>(HandleMenuOptionClick, prototype)
            {
                IconSpecifier = RadialMenuIconSpecifier.With(prototype.Sprite),
                ToolTip = GetTooltip(prototype)
            };
            list.Add(actionOption);
        }

        var models = new RadialMenuOptionBase[buttonsByCategory.Count + topLevelActions.Count];
        var i = 0;
        foreach (var (key, list) in buttonsByCategory)
        {
            var groupInfo = PrototypesGroupingInfo[key];
            models[i] = new RadialMenuNestedLayerOption(list)
            {
                IconSpecifier = RadialMenuIconSpecifier.With(groupInfo.Sprite),
                ToolTip = Loc.GetString(groupInfo.Tooltip)
            };
            i++;
        }

        foreach (var action in topLevelActions)
        {
            models[i] = action;
            i++;
        }

        return models;
    }

    private void HandleMenuOptionClick(RCDPrototype proto)
    {
        // A predicted message cannot be used here as the RPD UI is closed immediately
        // after this message is sent, which will stop the server from receiving it
        SendMessage(new RCDSystemMessage(proto.ID));

        if (_playerManager.LocalSession?.AttachedEntity == null)
            return;

        var msg = Loc.GetString("rcd-component-change-mode", ("tool", Owner), ("mode", Loc.GetString(proto.SetName)));

        if (proto.Mode is RcdMode.ConstructTile or RcdMode.ConstructObject)
        {
            var name = Loc.GetString(proto.SetName);

            if (proto.Prototype != null &&
                _prototypeManager.TryIndex(proto.Prototype, out var entProto)) // don't use Resolve because this can be a tile
            {
                name = entProto.Name;
            }

            msg = Loc.GetString("rcd-component-change-build-mode", ("tool", Owner), ("name", name));
        }

        // Popup message
        var popup = EntMan.System<PopupSystem>();
        popup.PopupClient(msg, Owner, _playerManager.LocalSession.AttachedEntity);
    }

    private string GetTooltip(RCDPrototype proto)
    {
        string tooltip;

        if (proto.Mode is RcdMode.ConstructTile or RcdMode.ConstructObject
            && proto.Prototype != null
            && _prototypeManager.TryIndex(proto.Prototype, out var entProto)) // don't use Resolve because this can be a tile
        {
            tooltip = Loc.GetString(entProto.Name);
        }
        else
        {
            tooltip = Loc.GetString(proto.SetName);
        }

        tooltip = OopsConcat(char.ToUpper(tooltip[0]).ToString(), tooltip.Remove(0, 1));

        return tooltip;
    }

    private static string OopsConcat(string a, string b)
    {
        // This exists to prevent Roslyn being clever and compiling something that fails sandbox checks.
        return a + b;
    }

    private void OnColorSelected(string colorKey)
    {
        if (!RPDPalette.IsValid(colorKey))
            return;

        SendMessage(new RPDColorChangeMessage(EntMan.GetNetEntity(Owner), colorKey));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _menu != null)
            _menu.ColorSelected -= OnColorSelected;

        base.Dispose(disposing);
    }
}

// SPDX-FileCopyrightText: 2025 Steve <marlumpy@gmail.com>
// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.RCD;
using Content.Shared.RPD;
using Content.Shared.RPD.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client.RPD;

/// <summary>
/// Opens an <see cref="RPDMenu"/> populated with the shared <see cref="RPDPalette"/>. Color selection is
/// forwarded to the server via <see cref="RPDColorChangeMessage"/>.
/// </summary>
public sealed class RPDMenuBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly IClyde _displayManager = default!;
    [Dependency] private readonly IInputManager _inputManager = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;

    private RPDMenu? _menu;

    public RPDMenuBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        IoCManager.InjectDependencies(this);
    }

    protected override void Open()
    {
        base.Open();

        if (!_entityManager.HasComponent<RPDComponent>(Owner))
            return;

        _menu = this.CreateWindow<RPDMenu>();
        _menu.SetEntity(Owner);
        _menu.ColorSelected += OnColorSelected;
        _menu.SendRCDSystemMessageAction += OnRCDSystemMessage;

        var selectedColor = _entityManager.TryGetComponent<RPDComponent>(Owner, out var comp)
                            && RPDPalette.IsValid(comp.PipeColor)
            ? comp.PipeColor
            : RPDPalette.DefaultKey;
        _menu.Populate(RPDPalette.Colors, selectedColor);

        var vpSize = _displayManager.ScreenSize;
        _menu.OpenCenteredAt(_inputManager.MouseScreenPosition.Position / vpSize);
    }

    private void OnColorSelected(string colorKey)
    {
        if (!RPDPalette.IsValid(colorKey))
            return;

        SendMessage(new RPDColorChangeMessage(_entityManager.GetNetEntity(Owner), colorKey));
    }

    private void OnRCDSystemMessage(ProtoId<RCDPrototype> protoId)
    {
        SendMessage(new RCDSystemMessage(protoId));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _menu != null)
        {
            _menu.ColorSelected -= OnColorSelected;
            _menu.SendRCDSystemMessageAction -= OnRCDSystemMessage;
        }
        base.Dispose(disposing);
    }
}

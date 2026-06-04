// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using Content.Client.Shuttles.UI;
using Content.Shared._NF.Shuttles.Events;
using Content.Shared.Shuttles.Components;

namespace Content.Client.Shuttles.BUI
{
    public sealed partial class ShuttleConsoleBoundUserInterface
    {
        private void NfOpen()
        {
            _window ??= new ShuttleConsoleWindow();
            _window.OnInertiaDampeningModeChanged += OnInertiaDampeningModeChanged;
            _window.OnMaxShuttleSpeedChanged += OnMaxShuttleSpeedChanged;
            _window.OnNetworkPortButtonPressed += OnNetworkPortButtonPressed;
            _window.OnServiceFlagsChanged += OnServiceFlagsChanged; // Frontier
        }
        private void OnInertiaDampeningModeChanged(NetEntity? entityUid, InertiaDampeningMode mode)
        {
            SendMessage(new SetInertiaDampeningRequest
            {
                ShuttleEntityUid = entityUid,
                Mode = mode,
            });
        }

        private void OnMaxShuttleSpeedChanged(float? maxSpeed)
        {
            SendMessage(new SetMaxShuttleSpeedRequest
            {
                MaxSpeed = maxSpeed,
            });
        }

        private void OnNetworkPortButtonPressed(string sourcePort, string targetPort)
        {
            SendMessage(new ShuttlePortButtonPressedMessage
            {
                SourcePort = sourcePort,
                TargetPort = targetPort
            });
        }

        // Frontier: service flags
        private void OnServiceFlagsChanged(NetEntity? entityUid, ServiceFlags flags)
        {
            SendMessage(new SetServiceFlagsRequest
            {
                ShuttleEntityUid = entityUid,
                ServiceFlags = flags,
            });
        }
    }
}

// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using Content.Shared._NF.Shuttles.Events;
using Content.Shared.Shuttles.Components;

namespace Content.Client.Shuttles.UI
{
    public sealed partial class ShuttleConsoleWindow
    {
        public event Action<NetEntity?, InertiaDampeningMode>? OnInertiaDampeningModeChanged;
        public event Action<float?>? OnMaxShuttleSpeedChanged;
        public event Action<string, string>? OnNetworkPortButtonPressed;
        public event Action<NetEntity?, ServiceFlags>? OnServiceFlagsChanged; // Frontier

        private void NfInitialize()
        {
            NavContainer.OnInertiaDampeningModeChanged += (entity, mode) =>
            {
                OnInertiaDampeningModeChanged?.Invoke(entity, mode);
            };
            NavContainer.OnServiceFlagsChanged += (entity, flags) => // Frontier
            {
                OnServiceFlagsChanged?.Invoke(entity, flags);
            };

            NavContainer.OnMaxShuttleSpeedChanged += (maxSpeed) =>
            {
                OnMaxShuttleSpeedChanged?.Invoke(maxSpeed);
            };

            NavContainer.OnNetworkPortButtonPressed += (sourcePort, targetPort) =>
            {
                OnNetworkPortButtonPressed?.Invoke(sourcePort, targetPort);
            };
        }
    }
}

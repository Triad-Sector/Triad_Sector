// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using Content.Shared._NF.Shuttles.Events;
using Content.Shared.Shuttles.Components;
using Robust.Client.UserInterface.Controls;
using System.Text.RegularExpressions;
using System;

namespace Content.Client.Shuttles.UI
{
    public sealed partial class NavScreen
    {
        private readonly ButtonGroup _buttonGroup = new();
        public event Action<NetEntity?, InertiaDampeningMode>? OnInertiaDampeningModeChanged;
        public event Action<float?>? OnMaxShuttleSpeedChanged;
        public event Action<string, string>? OnNetworkPortButtonPressed;
        public event Action<NetEntity?, ServiceFlags>? OnServiceFlagsChanged; // Frontier

        private void NfInitialize()
        {
            // Frontier - IFF search
            IffSearchCriteria.OnTextChanged += args => OnIffSearchChanged(args.Text);

            // Frontier - Maximum IFF Distance
            MaximumIFFDistanceValue.GetChild(0).GetChild(1).Margin = new Thickness(10, 0, 0, 0);
            MaximumIFFDistanceValue.OnValueChanged += args => OnRangeFilterChanged(args);

            // Frontier - Maximum Shuttle Speed
            MaximumShuttleSpeedValue.OnTextChanged += args => OnMaxSpeedChanged(args);

            DampenerOff.OnPressed += _ => SetDampenerMode(InertiaDampeningMode.Off);
            DampenerOn.OnPressed += _ => SetDampenerMode(InertiaDampeningMode.Dampen);
            AnchorOn.OnPressed += _ => SetDampenerMode(InertiaDampeningMode.Anchor);

            DampenerOff.Group = _buttonGroup;
            DampenerOn.Group = _buttonGroup;
            AnchorOn.Group = _buttonGroup;

            // Network Port Buttons
            DeviceButton1.OnPressed += _ => OnPortButtonPressed("device-button-1", "button-1");
            DeviceButton2.OnPressed += _ => OnPortButtonPressed("device-button-2", "button-2");
            DeviceButton3.OnPressed += _ => OnPortButtonPressed("device-button-3", "button-3");
            DeviceButton4.OnPressed += _ => OnPortButtonPressed("device-button-4", "button-4");
            DeviceButton5.OnPressed += _ => OnPortButtonPressed("device-button-5", "button-5");
            DeviceButton6.OnPressed += _ => OnPortButtonPressed("device-button-6", "button-6");
            DeviceButton7.OnPressed += _ => OnPortButtonPressed("device-button-7", "button-7");
            DeviceButton8.OnPressed += _ => OnPortButtonPressed("device-button-8", "button-8");

            // Send off a request to get the current dampening mode.
            _entManager.TryGetNetEntity(_shuttleEntity, out var shuttle);
            OnInertiaDampeningModeChanged?.Invoke(shuttle, InertiaDampeningMode.Query);

            ServiceFlagServices.OnPressed += _ => ToggleServiceFlags(ServiceFlags.Services);
            ServiceFlagTrade.OnPressed += _ => ToggleServiceFlags(ServiceFlags.Trade);
            ServiceFlagSocial.OnPressed += _ => ToggleServiceFlags(ServiceFlags.Social);
        }

        private void OnPortButtonPressed(string sourcePort, string targetPort)
        {
            OnNetworkPortButtonPressed?.Invoke(sourcePort, targetPort);
        }

        private void SetDampenerMode(InertiaDampeningMode mode)
        {
            NavRadar.DampeningMode = mode;
            _entManager.TryGetNetEntity(_shuttleEntity, out var shuttle);
            OnInertiaDampeningModeChanged?.Invoke(shuttle, mode);
        }

        private void NfUpdateState()
        {
            if (NavRadar.DampeningMode == InertiaDampeningMode.Station)
            {
                DampenerModeButtons.Visible = false;
                ServiceFlagsBox.Visible = false;
            }
            else
            {
                DampenerModeButtons.Visible = true;
                ServiceFlagsBox.Visible = true;
                DampenerOff.Pressed = NavRadar.DampeningMode == InertiaDampeningMode.Off;
                DampenerOn.Pressed = NavRadar.DampeningMode == InertiaDampeningMode.Dampen;
                AnchorOn.Pressed = NavRadar.DampeningMode == InertiaDampeningMode.Anchor;

                // Disable the Park button (AnchorOn) while in FTL, but keep other dampener buttons enabled
                if (NavRadar.InFtl)
                {
                    AnchorOn.Disabled = true;
                    // If the AnchorOn button is pressed while it gets disabled, we need to switch to another mode
                    if (AnchorOn.Pressed)
                    {
                        DampenerOn.Pressed = true;
                        SetDampenerMode(InertiaDampeningMode.Dampen);
                    }
                }
                else
                {
                    AnchorOn.Disabled = false;
                }

                ToggleServiceFlags(NavRadar.ServiceFlags, updateButtonsOnly: true); // Frontier
            }

        }

        // Frontier - Maximum IFF Distance
        private void OnRangeFilterChanged(int value)
        {
            NavRadar.MaximumIFFDistance = (float) value;
        }

        // Frontier - Maximum Shuttle Speed
        private void OnMaxSpeedChanged(LineEdit.LineEditEventArgs value)
        {
            MaximumShuttleSpeedValue.Text = Regex.Replace(MaximumShuttleSpeedValue.Text, "[^0-9]", "");
            float.TryParse(MaximumShuttleSpeedValue.Text, out var speed);
            OnMaxShuttleSpeedChanged?.Invoke(MaximumShuttleSpeedValue.Text == "" ? null : speed);
        }

        // Frontier - Service Flags
        private void ToggleServiceFlags(ServiceFlags flags, bool updateButtonsOnly = false)
        {
            if (!updateButtonsOnly)
            {
                // Special handling for ServiceFlags.None
                if (flags == ServiceFlags.None)
                {
                    // If None is being toggled, set it to None (clear all other flags)
                    // No need to check if None is already set since that check will always be false
                    NavRadar.ServiceFlags = ServiceFlags.None;
                }
                else
                {
                    // Toggle the requested flag
                    NavRadar.ServiceFlags ^= flags;

                    // If any flag other than None is set, make sure None is unset
                    if (NavRadar.ServiceFlags != 0)
                    {
                        NavRadar.ServiceFlags &= ~ServiceFlags.None; // This is redundant since None is 0
                    }
                    // If toggling resulted in no flags, set None
                    else
                    {
                        NavRadar.ServiceFlags = ServiceFlags.None;
                    }
                }
                _entManager.TryGetNetEntity(_shuttleEntity, out var shuttle);
                OnServiceFlagsChanged?.Invoke(shuttle, NavRadar.ServiceFlags);
            }

            ServiceFlagServices.Pressed = NavRadar.ServiceFlags.HasFlag(ServiceFlags.Services);
            ServiceFlagTrade.Pressed = NavRadar.ServiceFlags.HasFlag(ServiceFlags.Trade);
            ServiceFlagSocial.Pressed = NavRadar.ServiceFlags.HasFlag(ServiceFlags.Social);
        }

        private void NfAddShuttleDesignation(EntityUid? shuttle)
        {
            // Frontier - PR #1284 Add Shuttle Designation
            if (_entManager.TryGetComponent<MetaDataComponent>(shuttle, out var metadata))
            {
                var shipName = metadata.EntityName;

                // Try to find a designation in the format XXX-### (like CIV-748)
                // by checking each word in the ship name
                var shipNameParts = shipName.Split(' ');

                foreach (var part in shipNameParts)
                {
                    // Check if this part matches the designation format (e.g., CIV-748)
                    // The format is 2+ characters, followed by a dash, followed by more characters
                    if (part.Length > 3 && part.Contains('-'))
                    {
                        var dashIndex = part.IndexOf('-');
                        if (dashIndex >= 2 && dashIndex < part.Length - 1)
                        {
                            // This part looks like a designation
                            NavDisplayLabel.Text = shipName.Replace(part, "").Trim();
                            ShuttleDesignation.Text = part;
                            return;
                        }
                    }
                }

                // If we get here, no designation was found, so just show the full name
                NavDisplayLabel.Text = shipName;
                // Leave ShuttleDesignation.Text as "Unknown" (the default)
            }
            // End Frontier - PR #1284
        }

    }
}

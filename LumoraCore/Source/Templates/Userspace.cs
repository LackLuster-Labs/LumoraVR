// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.UI;
using Lumora.Core.GodotUI;
using Lumora.Core.Logging;

namespace Lumora.Core.Templates
{
    /// <summary>
    /// Manages userspace world creation and setup.
    /// </summary>
    public static class Userspace
    {
        /// <summary>
        /// Setup the userspace world for the engine.
        /// Userspace is a private overlay world for UI and settings.
        /// </summary>
        public static World SetupUserspace(Engine engine)
        {
            Logger.Log("Userspace: Setting up userspace world");

            // Create local userspace world
            var world = World.LocalWorld(engine, "Userspace", (w) =>
            {
                Logger.Log("Userspace: Initializing userspace world");

                // Create root structure
                var userspaceRoot = w.RootSlot.AddSlot("UserspaceRoot");

                // Create Dashboard panel (hidden by default, toggled with menu button/Escape)
                var dashboardSlot = userspaceRoot.AddSlot("Dashboard");
                var dashboardPanel = dashboardSlot.AttachComponent<DashboardPanel>();
                dashboardPanel.IsVisible.Value = false;

                Logger.Log("Userspace: Dashboard panel created");

                // Create Context Menu (radial arc menu, toggled with A/X or middle mouse)
                var contextMenuSlot = userspaceRoot.AddSlot("ContextMenu");
                contextMenuSlot.AttachComponent<ContextMenuSystem>();

                // ── Default root items ─────────────────────────────────────────
                // These are always present. Additional items are contributed at
                // open-time by RootContextMenuItem / ContextMenuItemSource components
                // attached anywhere in the world hierarchy — fully dynamic.

                var dashItem = contextMenuSlot.AttachComponent<RootContextMenuItem>();
                dashItem.Label.Value = "Dashboard";
                dashItem.IconPath.Value = "res://Icons/dashboard.png";
                dashItem.IsToggle.Value = true;
                dashItem.Priority.Value = 100;
                dashItem.Pressed += _ =>
                {
                    dashboardPanel.IsVisible.Value = !dashboardPanel.IsVisible.Value;
                    dashItem.IsToggled.Value = dashboardPanel.IsVisible.Value;
                };

                var closeItem = contextMenuSlot.AttachComponent<RootContextMenuItem>();
                closeItem.Label.Value = "Close Menu";
                closeItem.IconPath.Value = "res://Icons/close.png";
                closeItem.Priority.Value = -100; // always last

                Logger.Log("Userspace: Context menu created");
                Logger.Log("Userspace: Userspace world initialized");
            });

            // Register with WorldManager as userspace world
            // (UserspaceWorld setter automatically calls PrivateOverlayWorld which adds to managed worlds)
            engine.WorldManager.UserspaceWorld = world;

            Logger.Log("Userspace: Userspace setup complete");
            return world;
        }
    }
}

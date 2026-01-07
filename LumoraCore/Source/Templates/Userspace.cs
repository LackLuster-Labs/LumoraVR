using System;
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

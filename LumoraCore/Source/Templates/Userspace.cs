using System;
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

                Logger.Log("Userspace: Userspace world initialized");
            });

            // Set as private overlay (not visible to others, always on top)
            engine.WorldManager.PrivateOverlayWorld(world);

            // Register with WorldManager as userspace world
            engine.WorldManager.UserspaceWorld = world;

            Logger.Log("Userspace: Userspace setup complete");
            return world;
        }
    }
}

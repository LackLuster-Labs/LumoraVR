using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LumoraVR.Source.Logging;
using Godot;
namespace LumoraVR.Source.Helpers
{
    internal static class ThredUtil
    {
        public static void RunOnNodeSync(this Node GDobject, Action action)
        {
            // Safety check for null or disposed objects
            if (GDobject == null || !IsInstanceValid(GDobject))
            {
                Logger.Error("RunOnNodeSync: Node is null or disposed");
                return;
            }

            try
            {
                SemaphoreSlim semaphore = new SemaphoreSlim(0, 1);
                Action thisAction = null;

                thisAction = () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"RunOnNodeSync: Exception in action: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                };

                // Check if node is still in tree
                if (!GDobject.IsInsideTree())
                {
                    Logger.Error("RunOnNodeSync: Node is not in scene tree");
                    return;
                }

                var tree = GDobject.GetTree();
                if (tree == null)
                {
                    Logger.Error("RunOnNodeSync: Could not get SceneTree");
                    return;
                }

                tree.PhysicsFrame += thisAction;
                semaphore.Wait();

                // Check again if tree is valid before removing the callback
                if (IsInstanceValid(GDobject) && GDobject.IsInsideTree())
                {
                    tree.PhysicsFrame -= thisAction;
                }
            }
            catch (ObjectDisposedException)
            {
                Logger.Error("RunOnNodeSync: Node was disposed during operation");
            }
            catch (Exception ex)
            {
                Logger.Error($"RunOnNodeSync: Unexpected error: {ex.Message}");
            }
        }

        public static async void RunOnNodeAsync(this Node GDobject, Action action)
        {
            // Safety check for null or disposed objects
            if (GDobject == null || !IsInstanceValid(GDobject))
            {
                Logger.Error("RunOnNodeAsync: Node is null or disposed");
                return;
            }

            try
            {
                SemaphoreSlim semaphore = new SemaphoreSlim(0, 1);
                Action thisAction = null;

                thisAction = () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"RunOnNodeAsync: Exception in action: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                };

                // Check if node is still in tree
                if (!GDobject.IsInsideTree())
                {
                    Logger.Error("RunOnNodeAsync: Node is not in scene tree");
                    return;
                }

                var tree = GDobject.GetTree();
                if (tree == null)
                {
                    Logger.Error("RunOnNodeAsync: Could not get SceneTree");
                    return;
                }

                tree.PhysicsFrame += thisAction;

                try
                {
                    await semaphore.WaitAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error($"RunOnNodeAsync: Error waiting for semaphore: {ex.Message}");
                }

                // Check again if tree is valid before removing the callback
                if (IsInstanceValid(GDobject) && GDobject.IsInsideTree())
                {
                    try
                    {
                        tree.PhysicsFrame -= thisAction;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Tree was disposed, nothing we can do
                        Logger.Error("RunOnNodeAsync: Tree was disposed when removing callback");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                Logger.Error("RunOnNodeAsync: Node was disposed during operation");
            }
            catch (Exception ex)
            {
                Logger.Error($"RunOnNodeAsync: Unexpected error: {ex.Message}");
            }
        }

        // Helper method to safely check if a Godot object is valid
        private static bool IsInstanceValid(Node node)
        {
            try
            {
                return Godot.GodotObject.IsInstanceValid(node);
            }
            catch
            {
                return false;
            }
        }
    }
}

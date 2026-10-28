// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Assets;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

// Hooks self-declare their targets via [ImplementableHook(typeof(X))] on the
// hook class itself, so registration is just a reflection scan of the hooks
// assembly. Add overrides below only if a hook needs registration the
// attribute scheme can't express. - xlinka
public static class GodotHookRegistry
{
    public static void RegisterAll()
    {
        var hookAssembly = typeof(Lumora.Godot.Hooks.SlotHook).Assembly;

        int componentHooks = World.HookTypes.RegisterFromAssembly(hookAssembly);
        int assetHooks = AssetHookRegistry.RegisterFromAssembly(hookAssembly);

        // The core cannot ask the renderer what it is running on, so it is told here, once. Everything
        // platform-gated reads Engine.CurrentPlatform rather than sniffing the OS name again, and the
        // local user replicates it so other peers know what a person is on. -xlinka
        Lumora.Core.Engine.CurrentPlatform = global::Godot.OS.GetName() switch
        {
            "Android" => Lumora.Core.Platform.Android,
            "Windows" or "UWP" => Lumora.Core.Platform.Windows,
            "Linux" or "FreeBSD" or "NetBSD" or "OpenBSD" or "BSD" => Lumora.Core.Platform.Linux,
            _ => Lumora.Core.Platform.Other,
        };
        Lumora.Core.Engine.IsHandheld = AndroidDeviceMode.IsPhone;
        Lumora.Core.Engine.IsSteamFrameStandalone = HeadsetDeviceMode.IsSteamFrame;
        if (HeadsetDeviceMode.IsSteamFrame)
            LumoraLogger.Log($"Device: Steam Frame standalone (Linux), renderer='{(global::Godot.RenderingServer.GetCurrentRenderingMethod())}'");

        // The core cannot know what block format this device samples, and the GPU cache key needs it:
        // the same picture baked as BPTC on a desktop and as ASTC on a headset is different bytes, and
        // without the tag in the key they collide. Queried here, on the main thread, because this is
        // the first point where the render layer is known to the engine and the renderer is up. -xlinka
        var formats = Lumora.Godot.Hooks.TextureAssetHook.QueryDeviceFormats();
        Lumora.Core.Assets.TextureGpuCache.CompressionTag = Lumora.Godot.Hooks.TextureAssetHook.CompressionTag();
        Lumora.Core.Assets.TextureGpuCache.BlockWriter = Lumora.Godot.Hooks.TextureAssetHook.WriteBlockCache;
        Lumora.Godot.Hooks.MeshLodCache.RegisterSealing();
        Lumora.Godot.Hooks.GpuTextureVariants.Install();
        Lumora.Godot.Hooks.ComputePhysics.Install();
        Lumora.Godot.Hooks.GodotConvexDecomposer.Install();

        LumoraLogger.Log($"GodotHookRegistry: registered {componentHooks} component hooks, {assetHooks} asset hooks via reflection "
            + $"(texture formats: {formats}, cache tag: {Lumora.Core.Assets.TextureGpuCache.CompressionTag})");
    }
}

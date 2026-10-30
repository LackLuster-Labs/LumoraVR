// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;

namespace Lumora.Fluids;

// What the renderer turned out to be, reported once on the main thread by the first fluid hook. Nothing here
// touches Godot, so the data side and tests read it freely; before the report it says no renderer and no
// device, which is what a headless test is anyway.
//
// Android builds (Pico, Quest, phones) and the standalone Frame never simulate. A headset in a light world has
// about 0.6 ms of GPU to spare and a fluid frame costs more than that, so they draw the settled ledger and the
// spots on people, and their own users are coated through the keeper's proposals. They still load the module in
// full: its types are part of the protocol hash every peer has to agree on. -xlinka
public static class FluidPlatform
{
    public const string ForwardPlus = "forward_plus";
    public const string Mobile = "mobile";
    public const string Compatibility = "gl_compatibility";

    public static bool Reported { get; private set; }

    // Bumped by every Report. Anything that chose by the renderer before the report (a material picking its Mobile
    // file) compares its own copy, or listens to Changed, and chooses again.
    public static int Version { get; private set; }

    public static event Action? Changed;

    public static string RenderingMethod { get; private set; } = string.Empty;

    public static bool HasRenderingDevice { get; private set; }

    public static bool IsMobileRenderer => RenderingMethod == Mobile;

    public static bool SimulationAllowed => !Engine.IsMobilePlatform && !Engine.IsSteamFrameStandalone;

    // Why simulation is not allowed here, for the status line; empty where it is.
    public static string StandaloneReason => Engine.IsSteamFrameStandalone ? "Frame" : Engine.IsMobilePlatform ? "Android" : string.Empty;

    public static string RendererName => RenderingMethod switch
    {
        ForwardPlus => "Forward+",
        Mobile => "Mobile",
        Compatibility => "Compatibility",
        "" => "no renderer",
        _ => RenderingMethod,
    };

    public static void Report(string renderingMethod, bool hasRenderingDevice)
    {
        RenderingMethod = renderingMethod ?? string.Empty;
        HasRenderingDevice = hasRenderingDevice;
        Reported = true;
        Version++;
        Changed?.Invoke();
    }
}

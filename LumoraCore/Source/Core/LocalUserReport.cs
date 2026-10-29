// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Components;
using Lumora.Core.Input;

namespace Lumora.Core;

// What only this machine can say about its own user, put on their record with the rest of the local
// state: the build and runtime it runs, whether the dash is open and which camera a screen shows. Each
// write waits for a change, so a steady state costs nothing on the wire. -xlinka
internal static class LocalUserReport
{
    private static readonly string Runtime = RuntimeInformation.FrameworkDescription;

    public static void Write(User user, InputInterface? input)
    {
        if (user.EngineVersion.Value != EngineVersion.VersionString)
            user.EngineVersion.Value = EngineVersion.VersionString;
        if (user.RuntimeVersion.Value != Runtime)
            user.RuntimeVersion.Value = Runtime;

        bool dash = input?.IsDashboardOpen ?? false;
        if (user.DashOpen.Value != dash)
            user.DashOpen.Value = dash;

        // A headset is always looked through from the head.
        var mode = input?.IsVRActive == true
            ? DesktopViewMode.FirstPerson
            : DesktopHeadDrive.ResolveMode(user.Root?.Slot?.GetComponent<UserInputState>());
        if (user.ViewMode.Value != mode)
            user.ViewMode.Value = mode;

        var hand = EngineSettings.PrimaryHand;
        if (user.PrimaryHand.Value != hand)
            user.PrimaryHand.Value = hand;

        bool live = Photos.LiveStatus.IsLive;
        if (user.IsLive.Value != live)
            user.IsLive.Value = live;

        var capture = Photos.CaptureWatch.State;
        if (user.Capture.Value != capture)
            user.Capture.Value = capture;
    }
}

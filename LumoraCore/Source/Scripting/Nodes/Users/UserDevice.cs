// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What a user runs, as their own machine reports it: the head device (a screen or a headset), the platform,
// the camera a screen shows (first person, third person or a free camera; FirstPerson in a headset), and
// the app and runtime versions. The versions read empty until their machine has sent them, and everything
// reads its first value and empty text for a user the script cannot see.
[Node("Users/Info/Device", Capability.UsersRead, Description = "Reads the user's reported device, platform, view mode and app versions.",
    Keywords = "vr or desktop, desktop mode, headset, platform, view mode, third person, version")]
public sealed class UserDevice : ReadNode
{
    [return: Port("headDevice")]
    public static HeadOutputDevice Run(ScriptContext ctx, User? user, out Platform platform, out DesktopViewMode viewMode,
        out string appVersion, out string runtimeVersion)
    {
        platform = default;
        viewMode = default;
        appVersion = runtimeVersion = string.Empty;
        if (!ScriptReads.Visible(ctx, user))
            return default;
        platform = user!.UserPlatform.Value;
        viewMode = user.ViewMode.Value;
        appVersion = user.EngineVersion.Value ?? string.Empty;
        runtimeVersion = user.RuntimeVersion.Value ?? string.Empty;
        return user.HeadDevice.Value;
    }
}

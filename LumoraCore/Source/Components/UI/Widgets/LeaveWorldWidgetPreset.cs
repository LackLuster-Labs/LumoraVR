// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Components.Import;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// Close the world you are in. The confirm, the fallback world and the focus hand-off all belong to the
// Worlds screen, so this is a shortcut to that path rather than a second copy of it.
[ComponentCategory("Hidden")]
public sealed class LeaveWorldWidgetPreset : ActionWidgetPreset
{
    protected override string Title => "Leave World";
    protected override string? Icon => "leave";
    protected override color IconTint => DashTheme.Negative;

    protected override string? Unavailable
    {
        get
        {
            var world = FocusedWorld;
            if (world == null)
                return "No world";
            return IsLocalHome(world) ? "You are home" : null;
        }
    }

    protected override void Invoke()
    {
        var world = FocusedWorld;
        var dash = Dash;
        if (world == null || dash == null || IsLocalHome(world))
            return;
        foreach (var screen in dash.Screens)
        {
            if (screen is WorldsScreen worlds)
            {
                worlds.CloseWorld(world);
                return;
            }
        }
    }
}

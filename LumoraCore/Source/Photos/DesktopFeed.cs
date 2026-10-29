// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;

namespace Lumora.Core.Photos;

// What the desktop window shows in place of the headset's eye while one is set: a camera's view, for whoever
// watches the monitor or streams it. One at a time; the last to ask has it, and only it can take it back.
// -xlinka
public static class DesktopFeed
{
    private static object? _owner;

    public static RenderTexture? Shown { get; private set; }

    // Where that picture is seen from: the filming camera, looking down its -Z.
    public static Slot? Viewpoint { get; private set; }

    public static void Show(object owner, RenderTexture? texture, Slot? viewpoint = null)
    {
        _owner = owner;
        Shown = texture;
        Viewpoint = texture != null ? viewpoint : null;
    }

    public static void Hide(object owner)
    {
        if (!ReferenceEquals(_owner, owner))
            return;
        _owner = null;
        Shown = null;
        Viewpoint = null;
    }
}

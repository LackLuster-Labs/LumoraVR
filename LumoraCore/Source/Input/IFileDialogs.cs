// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Input;

// The operating system's own open and save dialogs, injected by the platform layer. Only where a window is
// on a screen somebody is looking at: in a headset the dialog would open on a monitor nobody can see, so
// callers fall back to a path they type. done gets the picked path, or null when the dialog was cancelled,
// on whatever thread the platform answers on. False when no dialog opened at all. -xlinka
public interface IFileDialogs
{
    bool PickSave(string title, string folder, string fileName, string filter, Action<string?> done);

    bool PickOpen(string title, string folder, string filter, Action<string?> done);
}

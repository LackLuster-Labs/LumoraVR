// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Photos;

// What the streaming program on this machine said it is doing, weakest first, through its own link, its own
// log, or a helper it only runs while it writes. The nametag shows the strongest.
public enum CaptureState : byte
{
    None,
    Recording,
    Streaming,
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Something a script did past the point where it starts to cost the frame, the session or memory. None of
// them stops it: each kind is logged the first time and then at most once every WarnSeconds, and counted
// for the diagnostic panel. -xlinka
public enum ScriptWarning : byte
{
    SlowCall,
    HeavyFrame,
    Handles,
    TextHeld,
    LongText,
    Collections,
    CollectionItems,
    Delays,
    Awaits,
    Queue,
    Rays,
    SlotsMade,
    Messages,
    Packs,
    SyncedValues,
    Copy,
    Memory,
    Fault,
    Log,
    Undo,
    Drawings,
    ViewReads,
    Attaches,
    Bakes,
    Variables,
    People,
}

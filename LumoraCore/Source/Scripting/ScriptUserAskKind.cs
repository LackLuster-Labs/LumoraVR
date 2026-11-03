// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Scripting;

// What a script on the host asks a person's own machine to do to them.
public enum ScriptUserAskKind : byte
{
    Push,
    SetVelocity,
    Scale,
    PlaceHips,
    TurnHips,
    SitInSeat,
    LeaveSeat,
    SwitchLocomotion,
    InstallLocomotions,
    LetGo
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Scripting;

public readonly record struct JoinRequested(JoinRequest? Request, string UserId, string Name, string MachineId, bool Guest, bool Friend,
    bool Silenced);

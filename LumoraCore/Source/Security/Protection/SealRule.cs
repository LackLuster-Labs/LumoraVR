// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Security;

// The bits a seal's rules carry, the same values the accounts service signs.
[Flags]
public enum SealRule
{
    None = 0,
    Equip = 1,
    SaveCopy = 2,
    Export = 4,
    Inspect = 8,
    All = Equip | SaveCopy | Export | Inspect,
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Security;

public enum SealVerdict
{
    Allowed,
    // The asker owns every seal in the way but one is still being checked.
    Pending,
    Refused,
}

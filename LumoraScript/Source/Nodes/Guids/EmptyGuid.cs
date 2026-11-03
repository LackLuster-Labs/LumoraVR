// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The all-zero guid, what an unset guid reads as.
[Node("Data/IDs/Empty Guid", Description = "The all-zero guid, what an unset guid reads as.",
    Keywords = "uuid, zero, blank id, null guid")]
public sealed class EmptyGuid : ValueNode
{
    [return: Port("guid")] public static Guid Run() => Guid.Empty;
}

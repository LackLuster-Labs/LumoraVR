// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Whether the guid is all zeros: unset, or set to Empty Guid.
[Node("Data/IDs/Is Empty", Description = "Whether the guid is all zeros: unset, or set to Empty Guid.",
    Keywords = "uuid, unset, blank id")]
public sealed class IsEmptyGuid : ValueNode
{
    [return: Port("empty")] public static bool Run(Guid guid) => guid == Guid.Empty;
}

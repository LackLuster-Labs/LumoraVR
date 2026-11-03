// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A box holding nothing, min at infinity and max at minus infinity, so the first point or box
// encapsulated into it is exactly what it becomes.
[Node("Math/Bounds/Empty Bounds", Description = "A box holding nothing, min at infinity and max at minus infinity, so the first point or box encapsulated into it is exactly what it becomes.",
    Keywords = "bounding box, aabb, box")]
public sealed class EmptyBounds : ValueNode
{
    [return: Port("bounds")] public static BoundingBox Run() => BoundingBox.Empty;
}

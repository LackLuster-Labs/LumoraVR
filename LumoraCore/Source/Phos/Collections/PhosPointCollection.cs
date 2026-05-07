// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Core.Phos.Collections;

/// <summary>
/// Collection of points.
/// Used by PhosShape to track which points belong to a shape.
/// </summary>
public class PhosPointCollection : List<PhosPoint>
{
    public PhosPointCollection() : base() { }

    public PhosPointCollection(int capacity) : base(capacity) { }

    public PhosPointCollection(IEnumerable<PhosPoint> collection) : base(collection) { }
}

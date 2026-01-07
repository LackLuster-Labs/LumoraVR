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
}

using System;
using System.Collections.Generic;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

public abstract class SyncBagBase<TKey, TValue> : ReplicatedDictionary<TKey, TValue> where TValue : class, IWorldElement
{
    public int RemoveAll(Predicate<TValue> match)
    {
        if (match == null)
        {
            return 0;
        }

        var keys = new List<TKey>();
        foreach (var kvp in _elements)
        {
            if (match(kvp.Value))
            {
                keys.Add(kvp.Key);
            }
        }

        foreach (var key in keys)
        {
            Remove(key);
        }

        return keys.Count;
    }
}

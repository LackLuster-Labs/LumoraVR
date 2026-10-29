// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core.Persistence;

// Maps a world's local RefIDs to stable GUIDs and back so cross-references survive a save/load
// round-trip. On load, a reference resolves immediately if its target is already loaded, otherwise
// it waits for it.
public sealed class ReferenceTranslator
{
    private readonly Dictionary<Guid, RefID> _globalToLocal = new();
    private readonly Dictionary<RefID, Guid> _localToGlobal = new();
    private Dictionary<Guid, List<ISyncRef>> _pendingRequests = new();
    private readonly bool _numbered;

    public ReferenceTranslator()
    {
    }

    private ReferenceTranslator(bool numbered) => _numbered = numbered;

    // Ids made from the RefIDs themselves rather than fresh GUIDs, so two saves of an unchanged world come
    // out byte for byte the same. For telling whether anything changed; a file wants fresh ids.
    public static ReferenceTranslator Numbered() => new(numbered: true);

    public bool HasLocal(RefID local) => _localToGlobal.ContainsKey(local);
    public bool HasGlobal(Guid global) => _globalToLocal.ContainsKey(global);

    // save: stable GUID for a local RefID, allocating one the first time it's seen
    public Guid Fetch(RefID local)
    {
        if (local == RefID.Null)
            throw new InvalidOperationException("Cannot fetch a null reference.");
        if (_localToGlobal.TryGetValue(local, out var existing))
            return existing;
        var global = _numbered ? FromRefID(local) : Guid.NewGuid();
        Associate(local, global);
        return global;
    }

    private static Guid FromRefID(RefID local)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, local.RawValue);
        return new Guid(bytes);
    }

    // load: bind a rebuilt element's local RefID to its saved GUID and resolve any waiters
    public void Associate(RefID local, Guid global)
    {
        if (local == RefID.Null)
            throw new InvalidOperationException("Cannot associate a null reference.");
        _globalToLocal[global] = local;
        _localToGlobal[local] = global;

        if (_pendingRequests.TryGetValue(global, out var waiters))
        {
            foreach (var waiter in waiters)
                waiter.Value = local;
            _pendingRequests.Remove(global);
        }
    }

    // Drops a local RefID/GUID binding. Needed for a translator that outlives the elements it
    // recorded (undo history): once an element is destroyed its GUID must stop resolving, or a later
    // load hands a dead ID to a waiter instead of letting the rebuilt element claim the GUID. Leaves
    // the binding alone if the GUID has already been re-pointed at a different element.
    public void Forget(RefID local)
    {
        if (!_localToGlobal.TryGetValue(local, out var global))
            return;
        _localToGlobal.Remove(local);
        if (_globalToLocal.TryGetValue(global, out var current) && current == local)
            _globalToLocal.Remove(global);
    }

    // load: point a reference at the GUID's element now, or queue it until that element loads
    public void Request(Guid global, ISyncRef requestee)
    {
        if (_globalToLocal.TryGetValue(global, out var local))
        {
            requestee.Value = local;
            return;
        }
        if (!_pendingRequests.TryGetValue(global, out var list))
        {
            list = new List<ISyncRef>();
            _pendingRequests[global] = list;
        }
        list.Add(requestee);
    }

    // unresolved = targets that never loaded
    public Dictionary<Guid, List<ISyncRef>> TakeUnresolved()
    {
        var unresolved = _pendingRequests;
        _pendingRequests = new Dictionary<Guid, List<ISyncRef>>();
        return unresolved;
    }
}

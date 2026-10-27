// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

public static partial class PackageMemberMapper
{
    // A package chain keeps each bone's rest pose in its own record and moves the bone slots themselves, so the
    // slots were saved wherever the swing had them at that moment: a flag's strips bent by their wind, a tail
    // mid-sway. Ours takes its rest from the pose the bones stand in when it builds, so each bone is put back at
    // its stored rest before the chain sees it. Bones are named in the single-reference pass, ahead of the list
    // that fills the chain, so the chain builds from the rest pose. -xlinka
    private static void RestBonesAsStored(DataTreeDictionary data, Action<ISyncRef, string> queueReference, Func<Uri, Uri?> remapUrl)
    {
        if ((data.TryGetNode("Bones") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList bones)
            return;
        foreach (var entry in bones.Children)
        {
            var record = entry as DataTreeDictionary;
            if (record?.TryGetNode("Data") is DataTreeDictionary nested)
                record = nested;
            if (record == null)
                continue;
            if ((record.TryGetNode("BoneSlot") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeValue { Value: string id }
                || id.Length == 0 || IsNullId(id))
                continue;
            float3? position = null;
            floatQ? rotation = null;
            if ((record.TryGetNode("OrigPosition") as DataTreeDictionary)?.TryGetNode("Data") is { } p
                && TryConvert(p, typeof(float3), out var pv, remapUrl, "OrigPosition") && pv is float3 pf
                && float.IsFinite(pf.x) && float.IsFinite(pf.y) && float.IsFinite(pf.z))
                position = pf;
            if ((record.TryGetNode("OrigRotation") as DataTreeDictionary)?.TryGetNode("Data") is { } r
                && TryConvert(r, typeof(floatQ), out var rv, remapUrl, "OrigRotation") && rv is floatQ rq
                && rq.x * rq.x + rq.y * rq.y + rq.z * rq.z + rq.w * rq.w > 1e-6f)
                rotation = rq;
            if (position == null && rotation == null)
                continue;
            queueReference(new BoneArrival(slot =>
            {
                if (slot.IsDestroyed)
                    return;
                if (position is { } at && !slot.LocalPosition.IsDriven)
                    slot.LocalPosition.Value = at;
                if (rotation is { } turn && !slot.LocalRotation.IsDriven)
                    slot.LocalRotation.Value = turn.Normalized;
            }), id);
        }
    }

    // Takes a slot the import names and hands it on; holds nothing itself.
    private sealed class BoneArrival : ISyncRef
    {
        private readonly Action<Slot> _seen;

        public BoneArrival(Action<Slot> seen) => _seen = seen;

        public RefID Value { get => RefID.Null; set { } }
        public IWorldElement Target { get => null!; set => TrySet(value); }
        public IWorldElement RawTarget => null!;
        public Type TargetType => typeof(Slot);
        public ReferenceState State => default;
        public void Clear() { }

        public bool TrySet(IWorldElement target)
        {
            if (target is not Slot slot)
                return false;
            _seen(slot);
            return true;
        }
    }
}

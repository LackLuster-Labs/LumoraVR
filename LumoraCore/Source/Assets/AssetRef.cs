// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Core.Assets;

public class AssetRef<A> : SyncRef<IAssetProvider<A>>, IAssetRef where A : Asset
{
    private bool _skipReleaseOnValueChange;

    public A Asset => (Target?.Asset) ?? null!;

    public bool IsAssetAvailable => Target?.IsAssetAvailable ?? false;

    IAssetProvider IAssetRef.Target
    {
        get => Target;
        set => Target = (value as IAssetProvider<A>)!;
    }

    public AssetRef() : base()
    {
    }

    public AssetRef(Component owner) : base(owner)
    {
    }

    protected override bool InternalSetValue(in RefID value, bool sync = true, bool change = true)
    {
        if (!_skipReleaseOnValueChange && _value.Equals(value))
        {
            return false;
        }
        return base.InternalSetValue(in value, sync, change);
    }

    protected override bool InternalSetRefID(in RefID id, IAssetProvider<A> prevTarget)
    {
        _skipReleaseOnValueChange = true;
        bool result = base.InternalSetRefID(in id, prevTarget);
        if (result)
        {
            ReleaseTarget(prevTarget);
        }
        else
        {
            _skipReleaseOnValueChange = false;
        }
        return result;
    }

    protected override void ValueChanged()
    {
        if (!_skipReleaseOnValueChange)
        {
            ReleaseTarget(RawTarget);
        }
        _skipReleaseOnValueChange = false;
        base.ValueChanged();
    }

    // A list element pointed somewhere else is a change to the list as far as a renderer is concerned, the
    // same as one finishing its load. Pointed at a material that was already loaded and already in use
    // (every nametag's shared plate material), nothing else ever said so: the provider only refreshes for
    // its first reference, so the surface kept drawing the old material until something unrelated dirtied
    // the renderer. -xlinka
    protected override void RunReferenceChanged()
    {
        SyncElementChanged();
        (Parent as SyncAssetList<A>)?.ElementAssetUpdated();
        base.RunReferenceChanged();
    }

    protected override void RunObjectAvailable()
    {
        Target?.ReferenceSet(this);
        SyncElementChanged(); // Target just resolved - re-trigger ApplyChanges so material gets applied
        (Parent as SyncAssetList<A>)?.ElementAssetUpdated();
        base.RunObjectAvailable();
    }

    public void AssetUpdated()
    {
        SyncElementChanged();
        // If this ref lives in a list, the list has to say so too - a renderer watches the LIST, not
        // each element, so a late-loading material would otherwise never reach it.
        (Parent as SyncAssetList<A>)?.ElementAssetUpdated();
    }

    public override void Dispose()
    {
        ReleaseTarget(RawTarget);
        base.Dispose();
    }

    private void ReleaseTarget(IAssetProvider<A> target)
    {
        if (target == null)
            return;

        target.ReferenceFreed(this);
    }
}

using Lumora.Core;

namespace Lumora.Core.Assets;

/// <summary>
/// Reference to an asset provider backed by SyncRef behavior.
/// Handles reference counting and change notifications for asset updates.
/// </summary>
public class AssetRef<A> : SyncRef<IAssetProvider<A>>, IAssetRef where A : Asset
{
    private bool _skipReleaseOnValueChange;

    /// <summary>
    /// The loaded asset instance (null if not loaded or no provider).
    /// </summary>
    public A Asset => Target?.Asset;

    /// <summary>
    /// Check if the asset is currently available (loaded).
    /// </summary>
    public bool IsAssetAvailable => Target?.IsAssetAvailable ?? false;

    IAssetProvider IAssetRef.Target
    {
        get => Target;
        set => Target = value as IAssetProvider<A>;
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

    protected override void RunReferenceChanged()
    {
        SyncElementChanged();
        base.RunReferenceChanged();
    }

    protected override void RunObjectAvailable()
    {
        Target?.ReferenceSet(this);
        base.RunObjectAvailable();
    }

    public void AssetUpdated()
    {
        SyncElementChanged();
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

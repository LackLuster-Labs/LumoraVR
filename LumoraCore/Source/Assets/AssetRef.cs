using System;
using Lumora.Core;

namespace Lumora.Core.Assets;

/// <summary>
/// Reference to an asset provider with automatic lifecycle management.
/// Handles reference counting, change tracking, and asset updates.
/// Connects components to asset providers in a network-synced way.
/// </summary>
public class AssetRef<A> : Sync<string>, IAssetRef where A : Asset
{
    private IAssetProvider<A> _target;
    private bool _wasChanged;

    // ===== PROPERTIES =====

    /// <summary>
    /// The asset provider this reference points to.
    /// Setting this updates the reference and notifies the old/new providers.
    /// </summary>
    public IAssetProvider<A> Target
    {
        get => _target;
        set
        {
            if (_target != value)
            {
                // Unregister from old provider
                _target?.ReferenceFreed(this);

                _target = value;
                _wasChanged = true;

                // Register with new provider
                _target?.ReferenceSet(this);

                // Trigger asset update notification
                AssetUpdated();
            }
        }
    }

    IAssetProvider IAssetRef.Target
    {
        get => Target;
        set => Target = value as IAssetProvider<A>;
    }

    /// <summary>
    /// The loaded asset instance (null if not loaded or no provider).
    /// </summary>
    public A Asset => _target?.Asset;

    /// <summary>
    /// Check if the asset is currently available (loaded).
    /// </summary>
    public bool IsAssetAvailable => _target?.IsAssetAvailable ?? false;

    /// <summary>
    /// Check if the reference was changed and clear the flag.
    /// Used for efficient change detection in update loops.
    /// </summary>
    public bool GetWasChangedAndClear()
    {
        bool result = _wasChanged;
        _wasChanged = false;
        return result;
    }

    // ===== CONSTRUCTORS =====

    public AssetRef() : base(default)
    {
    }

    public AssetRef(Component owner, string defaultValue = default) : base(owner, defaultValue)
    {
    }

    // ===== ASSET UPDATE NOTIFICATION =====

    /// <summary>
    /// Called when the referenced asset is updated (loaded, changed, removed).
    /// Triggers change notification for the owning component.
    /// </summary>
    public void AssetUpdated()
    {
        _wasChanged = true;

        // Mark as dirty for network sync via SyncElement
        InvalidateSyncElement();

        // Force change notification
        ForceSet(Value);
    }

    // ===== CLEANUP =====
    // Note: Cleanup happens automatically when component is destroyed
    // via the component lifecycle (Component.OnDestroy)
}

using System;
using System.Threading;
using System.Threading.Tasks;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

/// <summary>
/// Delegate for asset integration callbacks.
/// </summary>
/// <param name="isNewInstance">True if this is a newly created asset instance</param>
public delegate void AssetIntegratedCallback(bool isNewInstance);

/// <summary>
/// Base class for procedural asset providers that generate assets asynchronously.
/// Implements thread-safe update synchronization using SpinLock and write locks.
/// </summary>
/// <typeparam name="A">The asset type to provide</typeparam>
public abstract class AsyncProceduralProvider<A> : DynamicAssetProvider<A> where A : Asset, new()
{
    private SpinLock _updateLock = new SpinLock(enableThreadOwnerTracking: false);
    private volatile bool _isUpdating;
    private volatile bool _pendingUpdate;
    private volatile bool _safeDisposeComplete;
    private int _completedUpdates;
    private bool _hasError;

    private Action<IAsset> _onWriteLockAcquired;
    private Action _backgroundUpdateAction;
    private Func<Task> _asyncUpdateAction;
    private AssetIntegratedCallback _integratedCallback;

    /// <summary>
    /// Number of completed asset updates.
    /// </summary>
    public int CompletedUpdateCount => _completedUpdates;

    /// <summary>
    /// Whether an error occurred during the last update.
    /// </summary>
    public bool HasError => _hasError;

    /// <summary>
    /// Override to return true if async updates should be used instead of background thread.
    /// </summary>
    protected virtual bool PreferAsyncUpdate => false;

    protected override void UpdateAsset(A asset)
    {
        bool lockTaken = false;
        try
        {
            _updateLock.Enter(ref lockTaken);

            if (IsDestroyed)
            {
                RunSafeDispose();
                return;
            }

            if (_isUpdating)
            {
                _pendingUpdate = true;
                return;
            }

            _isUpdating = true;
        }
        finally
        {
            if (lockTaken) _updateLock.Exit();
        }

        PrepareUpdateState();

        // Cache delegates to avoid allocations
        _onWriteLockAcquired ??= OnWriteLockAcquired;
        _backgroundUpdateAction ??= ExecuteBackgroundUpdate;
        _asyncUpdateAction ??= ExecuteAsyncUpdate;
        _integratedCallback ??= OnAssetIntegrated;

        asset.RequestWriteLock(this, _onWriteLockAcquired);
    }

    protected override void FreeAsset()
    {
        bool lockTaken = false;
        try
        {
            _updateLock.Enter(ref lockTaken);
            if (_isUpdating)
            {
                _pendingUpdate = true;
                return;
            }
        }
        finally
        {
            if (lockTaken) _updateLock.Exit();
        }
        base.FreeAsset();
    }

    private void OnWriteLockAcquired(IAsset asset)
    {
        if (IsDestroyed)
        {
            asset.ReleaseWriteLock(this);
            RunSafeDispose();
            return;
        }

        if (PreferAsyncUpdate)
        {
            Task.Run(_asyncUpdateAction);
        }
        else
        {
            Task.Run(_backgroundUpdateAction);
        }
    }

    private async Task ExecuteAsyncUpdate()
    {
        try
        {
            if (!_hasError && !IsDestroyed)
            {
                await GenerateAssetAsync(Asset);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during dispose
        }
        catch (Exception ex)
        {
            _hasError = true;
            AquaLogger.Log($"Error in async procedural asset update: {ex.Message}");
            OnGenerationFailed(ex.Message);
        }
        FinishUpdate();
    }

    private void ExecuteBackgroundUpdate()
    {
        if (IsDestroyed)
        {
            Asset?.ReleaseWriteLock(this);
            RunSafeDispose();
            return;
        }

        try
        {
            if (!_hasError)
            {
                GenerateAsset(Asset);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during dispose
        }
        catch (Exception ex)
        {
            _hasError = true;
            AquaLogger.Log($"Error in procedural asset update: {ex.Message}");
            OnGenerationFailed(ex.Message);
        }
        FinishUpdate();
    }

    private void FinishUpdate()
    {
        Asset?.ReleaseWriteLock(this);

        if (IsDestroyed)
        {
            RunSafeDispose();
            return;
        }

        UploadToRenderer(_integratedCallback);
    }

    private void OnAssetIntegrated(bool isNewInstance)
    {
        OnAssetIntegrationComplete(isNewInstance);

        if (isNewInstance)
        {
            AssetCreated();
        }
        else
        {
            AssetUpdated();
        }

        _completedUpdates++;

        bool lockTaken = false;
        try
        {
            _updateLock.Enter(ref lockTaken);

            if (_pendingUpdate)
            {
                MarkChangeDirty();
                _pendingUpdate = false;
            }
            _isUpdating = false;
        }
        finally
        {
            if (lockTaken) _updateLock.Exit();
        }
    }

    private void RunSafeDispose()
    {
        if (!_safeDisposeComplete)
        {
            _safeDisposeComplete = true;
            OnSafeDispose();
        }
    }

    /// <summary>
    /// Clear the error state to allow updates to resume.
    /// </summary>
    public void ClearError()
    {
        _hasError = false;
    }

    // ===== Abstract Methods =====

    /// <summary>
    /// Capture state from sync fields before the async update begins.
    /// Called on main thread before write lock is acquired.
    /// </summary>
    protected abstract void PrepareUpdateState();

    /// <summary>
    /// Generate the asset data synchronously.
    /// Called on a background thread while holding write lock.
    /// </summary>
    protected abstract void GenerateAsset(A asset);

    /// <summary>
    /// Generate the asset data asynchronously.
    /// Called when PreferAsyncUpdate is true.
    /// </summary>
    protected abstract ValueTask GenerateAssetAsync(A asset);

    /// <summary>
    /// Upload the generated asset data to the renderer.
    /// Must call the callback when complete.
    /// </summary>
    protected abstract void UploadToRenderer(AssetIntegratedCallback onComplete);

    /// <summary>
    /// Called when asset generation fails with an error.
    /// </summary>
    protected abstract void OnGenerationFailed(string error);

    // ===== Virtual Methods =====

    /// <summary>
    /// Called after asset integration is complete.
    /// </summary>
    protected virtual void OnAssetIntegrationComplete(bool isNewInstance)
    {
    }

    /// <summary>
    /// Called for safe cleanup after disposal while an update was in progress.
    /// </summary>
    protected virtual void OnSafeDispose()
    {
    }

    public override void OnDestroy()
    {
        bool lockTaken = false;
        try
        {
            _updateLock.Enter(ref lockTaken);
            if (!_isUpdating)
            {
                RunSafeDispose();
            }
        }
        finally
        {
            if (lockTaken) _updateLock.Exit();
        }
        base.OnDestroy();
    }
}

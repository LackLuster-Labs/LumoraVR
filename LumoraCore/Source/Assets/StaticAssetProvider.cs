// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

// Component base for providers backed by a shared, URL-loaded asset. The provider is a thin
// IAssetRequester: it resolves its URL and asks the AssetManager for the asset, which loads itself and
// is shared across every requester for the same URL.
public abstract class StaticAssetProvider<A> : AssetProvider<A>, IAssetRequester, IUrlAssetProvider, IAssetReload
    where A : LoadableAsset, new()
{
    public readonly Sync<Uri> URL;

    private A? _asset;
    private Uri? _letGoUrl;
    private IAssetVariantDescriptor? _letGoVariant;
    private Uri? _requestedSourceUrl;                  // URL.Value the current request was issued for
    private Uri? _resolvedUrl;                          // ProcessURL result, the RequestAsset key
    private IAssetVariantDescriptor? _requestedVariant; // descriptor the current request was issued for
    private readonly object _lock = new();

    public override A Asset =>
        (_asset != null && IsLoaded(_asset)) ? _asset : null!;

    public override bool IsAssetAvailable =>
        _asset != null && URL.Value == _requestedSourceUrl && IsLoaded(_asset);

    private static bool IsLoaded(A asset) =>
        asset.LoadState is AssetLoadState.PartiallyLoaded or AssetLoadState.FullyLoaded;

    // A load that FAILED is not pending: nothing more is coming, so whoever is showing a loading skin
    // over this has to stop. No URL is not pending either - there is nothing to wait for. -xlinka
    public Uri? SourceUrl => URL.Value;

    public bool IsLoadPending
    {
        get
        {
            if (URL.Value == null)
                return false;
            var asset = _asset;
            if (asset != null && asset.LoadState == AssetLoadState.Failed)
                return false;
            return !IsAssetAvailable;
        }
    }

    // Asset hides a failed instance, so this is the one way to its reason. An address changed since the
    // request no longer counts as failed.
    public string? LoadFailure
    {
        get
        {
            var asset = _asset;
            if (asset == null || asset.LoadState != AssetLoadState.Failed || URL.Value != _requestedSourceUrl)
                return null;
            return asset.LoadFailureReason ?? $"could not load {URL.Value}";
        }
    }

    // The raw state for a diagnostic line, and nothing else reads it. Asset hides the instance until it
    // is loaded, so from outside "not loaded" is indistinguishable from "never asked for" - and those
    // are different bugs: the first is a decode still running or failed, the second is a provider with
    // no reference holding it open (a request is only issued on the first reference). The reference
    // count is printed for that reason. -xlinka
    public string LoadStateDescription
    {
        get
        {
            var asset = _asset;
            string state = URL.Value == null ? "no url"
                : asset == null ? (_resolvedUrl == null ? "not requested" : "requested, no asset yet")
                : asset.LoadState.ToString();
            return $"{state}, refs={AssetReferenceCount}";
        }
    }

    protected StaticAssetProvider()
    {
        URL = new Sync<Uri>(this, null!);
        URL.OnChanged += _ => OnUrlChanged();
    }

    private void OnUrlChanged()
    {
        if (AssetReferenceCount > 0)
            UpdateAsset();
    }

    // The variant descriptor (wrap/mipmap options, etc.) for this provider's request, or null for the
    // default variant. Recomputed on every refresh; if it changes, the asset is re-requested for the new
    // variant.
    protected virtual IAssetVariantDescriptor? GetVariantDescriptor() => null;

    protected override void UpdateAsset()
    {
        Uri resolved = ProcessURL(URL.Value);
        IAssetVariantDescriptor? variant = GetVariantDescriptor();

        lock (_lock)
        {
            if (resolved == _resolvedUrl && Equals(variant, _requestedVariant))
                return; // already requested for this resolved URL + variant

            ReleaseCurrent();
            _requestedSourceUrl = URL.Value;
            _resolvedUrl = resolved;
            _requestedVariant = variant;
        }

        if (resolved == null)
        {
            AssetRemoved();
            return;
        }

        var manager = Engine.Current?.AssetManager;
        if (manager == null)
        {
            LumoraLogger.Warn($"StaticAssetProvider[{GetType().Name}]: no AssetManager; cannot request {resolved}");
            return;
        }

        manager.RequestAsset<A>(resolved, this, variant);
    }

    protected override void FreeAsset()
    {
        lock (_lock)
        {
            ReleaseCurrent();
        }
        AssetRemoved();
    }

    // Must be called under _lock.
    private void ReleaseCurrent()
    {
        if (_resolvedUrl != null)
            Engine.Current?.AssetManager?.ReleaseAsset<A>(_resolvedUrl, this, _requestedVariant);

        _asset = null;
        _requestedSourceUrl = null;
        _resolvedUrl = null;
        _requestedVariant = null;
    }

    Uri? IAssetReload.Held
    {
        get
        {
            lock (_lock)
                return _resolvedUrl;
        }
    }

    Uri? IAssetReload.LetGo()
    {
        lock (_lock)
        {
            _letGoUrl = _resolvedUrl;
            _letGoVariant = _requestedVariant;
            if (_letGoUrl == null)
                return null;
            ReleaseCurrent();
        }
        AssetRemoved();
        return _letGoUrl;
    }

    bool IAssetReload.Drop()
    {
        var url = _letGoUrl;
        return url == null || (Engine.Current?.AssetManager?.Forget<A>(url, _letGoVariant) ?? true);
    }

    void IAssetReload.AskAgain()
    {
        _letGoUrl = null;
        _letGoVariant = null;
        if (!IsDestroyed && AssetReferenceCount > 0)
            UpdateAsset();
    }

    // IAssetRequester. Called by the asset's variant manager.

    void IAssetRequester.AssignAsset(Asset asset) => _asset = (A)asset;

    void IAssetRequester.AssetLoadStateUpdated(Asset asset)
    {
        if (asset != _asset)
            return;

        // AssetCreated/AssetUpdated/AssetRemoved marshal to the main thread internally, so this
        // is safe to run on the asset's background load thread.
        switch (asset.LoadState)
        {
            case AssetLoadState.PartiallyLoaded:
            case AssetLoadState.FullyLoaded:
                AssetCreated();
                AssetUpdated();
                break;
            case AssetLoadState.Unloaded:
                if (_asset == asset)
                    _asset = null;
                AssetRemoved();
                break;
            case AssetLoadState.Failed:
                // Nothing more is coming: whoever holds this has to hear it, to say why. A cloud part that was
                // only late is waited for (LateCloudParts) and loads again when it lands.
                AssetUpdated();
                LateCloudParts.Failed((LoadableAsset)asset);
                break;
        }
    }
}

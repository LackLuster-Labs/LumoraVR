// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

// After InitializeStatic it gathers and decodes itself from its URL in LoadSelf, tracks the requesters
// keeping it alive, and is shared by every requester of the same (URL, type). After InitializeDynamic it
// is procedural: no requesters, loaded immediately.
public abstract class LoadableAsset : Asset
{
    private readonly HashSet<IAssetRequester> _requests = new();
    private readonly object _stateLock = new();
    private bool _runningUpdate;
    private bool _unload;

    internal AssetVariantManager? VariantManager { get; set; }

    // Null for the default variant. The variant manager sets it when it creates the asset, before
    // LoadSelf reads it.
    public IAssetVariantDescriptor? TargetVariant { get; internal set; }

    public override int ActiveRequestCount
    {
        get { lock (_stateLock) { return _requests.Count; } }
    }

    // Called by the variant manager (under its lock). Kicks off loading on the first request.
    internal void TryAddRequest(IAssetRequester requester)
    {
        CheckStatic();
        lock (_stateLock)
        {
            _requests.Add(requester);
        }

        requester.AssignAsset(this);

        if (LoadState == AssetLoadState.Created)
        {
            RunUpdate();
        }
        else
        {
            requester.AssetLoadStateUpdated(this);
        }
    }

    internal void RemoveRequest(IAssetRequester requester)
    {
        CheckStatic();
        lock (_stateLock)
        {
            _requests.Remove(requester);
        }
    }

    internal void CollectRequesters(List<IAssetRequester> into)
    {
        lock (_stateLock)
        {
            into.AddRange(_requests);
        }
    }

    internal void QueueUnload()
    {
        _unload = true;
        RunUpdate();
    }

    protected override void OnLoadStateChanged()
    {
        base.OnLoadStateChanged();

        if (LoadState == AssetLoadState.Unloaded)
        {
            return;
        }

        NotifyRequestersUpdated();
    }

    // Push the current state at every requester without a state transition. A progressive load
    // replaces its own contents more than once, and only the FIRST of those replacements is a
    // state change (LoadStarted -> PartiallyLoaded); the rest have to be announced by hand or the
    // consuming material keeps whatever it bound the first time. -xlinka
    protected void NotifyRequestersUpdated()
    {
        IAssetRequester[] snapshot;
        lock (_stateLock)
        {
            snapshot = _requests.ToArray();
        }
        foreach (var requester in snapshot)
        {
            requester.AssetLoadStateUpdated(this);
        }
    }

    // Announce that usable-but-not-final contents are in place. Safe to call repeatedly: the first
    // call moves the state, later ones just re-notify, and neither can walk the state backwards.
    protected void ReportPartiallyLoaded()
    {
        if (LoadState < AssetLoadState.PartiallyLoaded)
        {
            SetLoadState(AssetLoadState.PartiallyLoaded);
        }
        else
        {
            NotifyRequestersUpdated();
        }
    }

    private void RunUpdate()
    {
        lock (_stateLock)
        {
            if (_runningUpdate)
            {
                return;
            }
            _runningUpdate = true;
        }
        _ = Task.Run(ProcessUpdate);
    }

    private async Task ProcessUpdate()
    {
        // Runs detached via Task.Run, so an escaping exception would be an unobserved task fault with no
        // log trace. The inner try guards the load itself; this outer one catches the state transitions
        // (SetLoadState, Unload, OnAssetUnloaded) so nothing slips through silently. -xlinka
        try
        {
            try
            {
                if (LoadState == AssetLoadState.Created)
                {
                    SetLoadState(AssetLoadState.LoadStarted);
                }

                if (!_unload)
                {
                    try
                    {
                        await LoadSelf().ConfigureAwait(false);
                        if (LoadState != AssetLoadState.FullyLoaded && LoadState != AssetLoadState.Failed)
                        {
                            SetLoadState(AssetLoadState.FullyLoaded);
                        }
                    }
                    catch (Exception ex)
                    {
                        FailLoad(ex.Message);
                    }
                }
            }
            finally
            {
                lock (_stateLock)
                {
                    _runningUpdate = false;
                }
            }

            if (_unload)
            {
                Unload();
                if (LoadState != AssetLoadState.Unloaded)
                {
                    SetLoadState(AssetLoadState.Unloaded);
                }
                VariantManager?.OnAssetUnloaded(this);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"LoadableAsset: unhandled error processing update for {AssetURL}: {ex.Message}");
        }
    }

    // Static instances only. Gather the bytes with AssetManager.RequestGather and decode them; it starts
    // off the main thread, but see DecodeOffThread for where the decode has to run.
    protected abstract Task LoadSelf();

    // Run the CPU half of a load somewhere a stall is harmless. ProcessUpdate starts on the pool, but
    // a LoadSelf resumes on whatever thread completed its gather, and that is the world update loop -
    // so a decode written as a plain call after an await is one context capture away from freezing a
    // frame for as long as the parse takes. Hopping here states the placement instead of inheriting
    // it, and it holds no matter who completes the gather or what context they carry. -xlinka
    protected static Task<T> DecodeOffThread<T>(Func<T> decode) => Task.Run(decode);
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Utility;

namespace Lumora.Core.Components.Assets;

// How far everyone here has got loading one asset: a player that shows "3 of 4 people can see this" before it
// starts. Every machine writes its own row with what its own copy of the asset is doing, and every machine counts
// the rows the same way. A person with no row yet counts as not loaded. A video counts as loaded once it is ready
// to show, and as failed once its link would not play.
//
// The counts are written locally on every machine, like a driven value: they are the same sum everywhere, and
// broadcasting one machine's would only fight the others'. -xlinka
[ComponentCategory("Assets")]
[ComponentGenericTypes(typeof(TextureAsset), typeof(VideoTexture), typeof(AudioClip), typeof(MeshDataAsset), typeof(MaterialAsset))]
public sealed class AssetLoadTally<A> : Component where A : Asset
{
    public readonly AssetRef<A> Asset;

    [NonPersistent]
    public readonly SyncList<UserValueEntry<AssetLoadState>> Rows;

    [NonPersistent]
    public readonly Sync<int> TotalUsers;
    [NonPersistent]
    public readonly Sync<int> NotLoaded;
    [NonPersistent]
    public readonly Sync<int> Loading;
    [NonPersistent]
    public readonly Sync<int> PartiallyLoaded;
    [NonPersistent]
    public readonly Sync<int> FullyLoaded;
    [NonPersistent]
    public readonly Sync<int> Failed;

    private readonly List<User> _users = new();
    private readonly Dictionary<User, AssetLoadState> _states = new();
    private double _nextLook;

    public AssetLoadTally()
    {
        Asset = new AssetRef<A>(this);
        Rows = new SyncList<UserValueEntry<AssetLoadState>>();
        TotalUsers = new Sync<int>(this, 0);
        NotLoaded = new Sync<int>(this, 0);
        Loading = new Sync<int>(this, 0);
        PartiallyLoaded = new Sync<int>(this, 0);
        FullyLoaded = new Sync<int>(this, 0);
        Failed = new Sync<int>(this, 0);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var world = World;
        if (world == null)
            return;
        double now = world.Time.TotalTime;
        if (now < _nextLook)
            return;
        _nextLook = now + 0.5;

        WriteOwnRow(world.LocalUser);
        Count(world);
    }

    private AssetLoadState LocalState()
    {
        if (Asset.Target is VideoTextureProvider video && !string.IsNullOrEmpty(video.LoadError.Value))
            return AssetLoadState.Failed;
        var asset = Asset.Asset;
        if (asset == null)
            return Asset.Target == null ? AssetLoadState.Unloaded : AssetLoadState.Created;
        if (asset is VideoTexture clip)
            return clip.IsReady ? AssetLoadState.FullyLoaded : AssetLoadState.LoadStarted;
        return asset.LoadState;
    }

    private void WriteOwnRow(User? user)
    {
        if (user == null || !Rows.IsWriteAllowedQuietly())
            return;
        var state = LocalState();
        UserValueEntry<AssetLoadState>? row = null;
        foreach (var entry in Rows.Elements)
        {
            if (entry.User.Target == user)
            {
                row = entry;
                break;
            }
        }
        if (row != null && row.Value.Value == state)
            return;
        row ??= Rows.Add();
        if (row.User.Target != user)
            row.User.Target = user;
        row.Value.Value = state;
    }

    private void Count(World world)
    {
        _states.Clear();
        foreach (var entry in Rows.Elements)
        {
            if (entry.User.Target is { } user)
                _states[user] = entry.Value.Value;
        }
        // This machine's own answer stands even where its row could not be written.
        if (world.LocalUser is { } local)
            _states[local] = LocalState();

        world.GetAllUsers(_users);
        int total = 0, none = 0, loading = 0, partial = 0, full = 0, failed = 0;
        foreach (var user in _users)
        {
            if (user == null || user.IsDisposed)
                continue;
            total++;
            if (!_states.TryGetValue(user, out var state))
            {
                none++;
                continue;
            }
            switch (state)
            {
                case AssetLoadState.Created:
                case AssetLoadState.LoadStarted:
                    loading++;
                    break;
                case AssetLoadState.PartiallyLoaded:
                    partial++;
                    break;
                case AssetLoadState.FullyLoaded:
                    full++;
                    break;
                case AssetLoadState.Failed:
                    failed++;
                    break;
                default:
                    none++;
                    break;
            }
        }
        _users.Clear();

        if (total == TotalUsers.Value && none == NotLoaded.Value && loading == Loading.Value && partial == PartiallyLoaded.Value
            && full == FullyLoaded.Value && failed == Failed.Value)
            return;
        TotalUsers.SetValueSilently(total);
        NotLoaded.SetValueSilently(none);
        Loading.SetValueSilently(loading);
        PartiallyLoaded.SetValueSilently(partial);
        FullyLoaded.SetValueSilently(full);
        Failed.SetValueSilently(failed);
    }

    public override void OnUserLeft(User user)
    {
        base.OnUserLeft(user);
        if (World?.IsAuthority == true)
            Rows.RemoveAll(e => e.User.Target == user || e.User.Target == null);
    }
}

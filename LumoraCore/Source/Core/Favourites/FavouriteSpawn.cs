// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Lumora.Core;

// Where a menu makes a video player, a portal, a camera and the like, the user's favourite of that kind is
// made instead, into the same slot and standing where the slot stands. Everything the menu would set on its
// own one it then sets on the favourite's, so a favourite player still plays what was pasted and a favourite
// portal still leads where the button said.
//
// The world has the last word: a favourite is the user's own item, so it comes in only where they may bring
// their own things, and anywhere else the built-in one is made without a word. When the favourite cannot be
// had (gone from the inventory, not fetched, no longer that kind of thing) the built-in one is made and a
// quiet notice says why. -xlinka
public static class FavouriteSpawn
{
    public static bool Allows(World world)
        => world.AllowsItemSpawning
            && world.DataModelPermissions?.AllowsDomain(world.LocalUser, DataModelPermissionDomain.Spawn) != false;

    // A favourite would be tried: there is one, and this world lets the user bring their own things.
    public static bool Wanted(World? world, FavouriteKind kind)
        => world != null && !world.IsDestroyed && Favourites.Has(kind) && Allows(world);

    // Makes slot the favourite of that kind and hands done its component, on the world thread. done gets null
    // straight away when there is nothing to try, or later when the favourite could not be had; either way
    // the caller makes its own on slot. done also hears null if slot went away meanwhile, so check it.
    // slot should be empty: the favourite loads over it. Call on the world thread.
    public static void Fill<T>(Slot slot, FavouriteKind kind, Action<T?> done) where T : Component
    {
        var world = slot?.World;
        if (slot == null || world == null || !Wanted(world, kind))
        {
            done(null);
            return;
        }
        _ = FetchThenLoad(world, slot, kind, done);
    }

    public static Task<T?> FillAsync<T>(Slot slot, FavouriteKind kind) where T : Component
    {
        var done = new TaskCompletionSource<T?>();
        Fill<T>(slot, kind, found => done.TrySetResult(found));
        return done.Task;
    }

    // A camera for the local user, a little ahead of their eyes and looking where they look: their favourite
    // one when they have one, run by them. Without a favourite it is made at once. Call on the world thread.
    public static void Camera(World world, bool keep, Action<PhotoCamera?> done)
    {
        var root = world.LocalUser?.Root;
        if (!Wanted(world, FavouriteKind.Camera) || world.RootSlot == null || root == null)
        {
            done(PhotoCamera.Spawn(world, keep));
            return;
        }
        Slot slot;
        using (world.EnterSpawnAllocation())
        {
            slot = world.RootSlot.AddSlot("Camera");
            if (!keep)
                slot.Persistent.Value = false;
            slot.GlobalPosition = root.HeadPosition + root.HeadRotation * (float3.Backward * 0.45f) + float3.Down * 0.08f;
            slot.GlobalRotation = root.HeadRotation;
        }
        Fill<PhotoCamera>(slot, FavouriteKind.Camera, camera =>
        {
            if (slot.IsDestroyed)
            {
                done(null);
                return;
            }
            using var own = world.EnterSpawnAllocation();
            if (camera == null)
                camera = slot.AttachComponent<PhotoCamera>();
            else
                camera.Operator.Target = world.LocalUser!;
            done(camera);
        });
    }

    // What the notice says it fell back to.
    public static string BuiltIn(FavouriteKind kind) => kind switch
    {
        FavouriteKind.Avatar => "Wearing the default avatar",
        FavouriteKind.Home => "Staying in your home space",
        _ => $"Built-in {kind.Noun()}",
    };

    private static async Task FetchThenLoad<T>(World world, Slot slot, FavouriteKind kind, Action<T?> done) where T : Component
    {
        FavouriteFetched? fetched;
        try
        {
            fetched = await Task.Run(() => FavouriteFetch.FetchAsync(kind)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fetched = Favourites.Get(kind) is { } item
                ? new FavouriteFetched { Item = item, Problem = $"Your favourite {kind.Noun()} could not be fetched: {ex.Message}" }
                : null;
        }
        world.RunSynchronously(() =>
        {
            if (slot.IsDestroyed)
            {
                done(null);
                return;
            }
            if (fetched?.Tree == null)
            {
                if (fetched?.Problem != null)
                    FavouriteNotice.Show(world, slot, BuiltIn(kind), fetched.Problem);
                done(null);
                return;
            }
            Load(world, slot, kind, fetched, done);
        });
    }

    // The slot keeps where it stands, its name and whether it is saved with the world; the favourite brings
    // everything else, its own size included.
    private static void Load<T>(World world, Slot slot, FavouriteKind kind, FavouriteFetched fetched, Action<T?> done) where T : Component
    {
        var position = slot.LocalPosition.Value;
        var rotation = slot.LocalRotation.Value;
        var name = slot.Name.Value;
        var persistent = slot.Persistent.Value;
        string favourite = fetched.Item.Name;
        try
        {
            slot.LoadObjectStaged(fetched.Tree!, world.EnterSpawnAllocation, started =>
            {
                started.LocalPosition.Value = position;
                started.LocalRotation.Value = rotation;
                started.Name.Value = name;
                started.Persistent.Value = persistent;
            }, loaded =>
            {
                if (slot.IsDestroyed)
                {
                    done(null);
                    return;
                }
                var found = loaded == null ? null : slot.GetComponentInChildren<T>();
                if (found == null)
                    FavouriteNotice.Show(world, slot, BuiltIn(kind), $"Your favourite {kind.Noun()} \"{favourite}\" did not load.");
                done(found);
            });
        }
        catch (Exception ex)
        {
            FavouriteNotice.Show(world, slot, BuiltIn(kind), $"Your favourite {kind.Noun()} \"{favourite}\" did not load: {ex.Message}");
            done(null);
        }
    }
}

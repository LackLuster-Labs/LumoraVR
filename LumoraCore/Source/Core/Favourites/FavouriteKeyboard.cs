// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.UI;
using Lumora.Core.Persistence;
using Lumora.Core.Templates;

namespace Lumora.Core;

// The user's favourite keyboard in place of the built-in one. A keyboard is made in a world (Create New >
// Object > Keyboard, a KeyboardDesign), changed there with the usual tools, saved and made the favourite; this
// brings it into the user's own space and VrKeyboard takes it up exactly as it takes up the built-in, so
// showing, hiding, docking, poking, repeat, the preview and focus all work the same.
//
// Your own space holds the dash, the account and every field you type into, so what a keyboard brings in is
// fenced: what draws it and what makes its keys keys, nothing that acts on its own. No Loom graph runs in it
// (its scripts are left out, and say so), nothing that makes sound, moves physics or talks to the network, and
// nothing that only your own space may hold. Its keys type through VrKeyboard into the focused field and
// nowhere else, the same as the built-in ones, and its preview shows what the built-in's does.
//
// A favourite that cannot be had (gone, unreadable, no keys, too big, broken on the way in) leaves the built-in
// standing, so nobody in a headset is ever left without a keyboard; the reason waits for the next time the
// keyboard comes up. The swap itself waits for the keyboard to be put away. -xlinka
public static class FavouriteKeyboard
{
    // A keyboard is a few hundred parts. Past this it is not one, and loading it would stall every frame of
    // the user's own space for it.
    public const int MostParts = 3000;

    private static readonly string[] Categories =
    {
        "UI/Helio", "Assets/Materials", "Assets/Textures", "Assets/Procedural Meshes", "Assets/Material Property Blocks",
        "Utility/Transforms", "Utility/Values",
    };

    private static readonly HashSet<Type> Also = new()
    {
        typeof(KeyboardDesign), typeof(VrKey), typeof(UITheme), typeof(Grabbable), typeof(FontProvider),
        typeof(Assets.ImageProvider), typeof(MeshRenderer),
    };

    private static readonly HashSet<Type> Never = new()
    {
        typeof(RenderTextureProvider), typeof(PhotoDetails),
    };

    private static int _generation;
    private static Slot? _favourite;
    private static string? _loadedFrom;
    private static (string Title, string Text)? _toSay;

    public static void Attach()
    {
        Favourites.Changed += kind =>
        {
            if (kind == FavouriteKind.Keyboard)
                Refresh();
        };
        Favourites.Ready += Refresh;
    }

    // May run on any thread.
    public static void Refresh()
    {
        int generation = Interlocked.Increment(ref _generation);
        var space = Engine.Current?.WorldManager?.UserspaceWorld;
        if (space == null || space.IsDestroyed)
            return;
        if (!Favourites.Has(FavouriteKind.Keyboard))
        {
            space.RunSynchronously(() => BuiltIn(space, generation, null));
            return;
        }
        _ = Task.Run(async () =>
        {
            FavouriteFetched? fetched;
            try
            {
                fetched = await FavouriteFetch.FetchAsync(FavouriteKind.Keyboard).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                fetched = Favourites.Get(FavouriteKind.Keyboard) is { } item
                    ? new FavouriteFetched { Item = item, Problem = FavouriteLocale.KeyboardDidNotLoad(item.Name, ex.Message).Resolve() }
                    : null;
            }
            space.RunSynchronously(() => Use(space, generation, fetched));
        });
    }

    // The keyboard came up: whatever went wrong with the favourite since is said now, where the user is.
    public static void Shown()
    {
        if (_toSay is not { } say)
            return;
        _toSay = null;
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        world?.RunSynchronously(() => FavouriteNotice.Show(world, null, say.Title, say.Text));
    }

    // VrKeyboard could not take its layout up. The favourite goes and the built-in comes back.
    public static void Broken(VrKeyboard keyboard, string why)
    {
        var space = keyboard.World;
        if (space == null || keyboard.Slot == null || !ReferenceEquals(keyboard.Slot, _favourite))
            return;
        string name = Favourites.Get(FavouriteKind.Keyboard)?.Name ?? keyboard.Slot.Name.Value ?? "";
        _toSay = (FavouriteSpawn.BuiltIn(FavouriteKind.Keyboard), FavouriteLocale.KeyboardDidNotLoad(name, why).Resolve());
        space.RunSynchronously(() => BuiltIn(space, _generation, _toSay));
    }

    private static void Use(World space, int generation, FavouriteFetched? fetched)
    {
        if (generation != _generation || space.IsDestroyed)
            return;
        if (fetched == null)
        {
            BuiltIn(space, generation, null);
            return;
        }
        string name = fetched.Item.Name;
        if (fetched.Tree == null)
        {
            BuiltIn(space, generation, (FavouriteSpawn.BuiltIn(FavouriteKind.Keyboard), fetched.Problem ?? FavouriteLocale.KeyboardNoKeys(name).Resolve()));
            return;
        }
        // The same file already in use: a sign in or a settings pull saying again what is known.
        if (_favourite is { IsDestroyed: false } && string.Equals(_loadedFrom, fetched.Path, StringComparison.Ordinal))
            return;

        var problem = Load(space, fetched.Tree, name, out var loaded, out var leftOut);
        if (loaded == null)
        {
            Logging.Logger.Warn($"Favourites: {problem}");
            BuiltIn(space, generation, (FavouriteSpawn.BuiltIn(FavouriteKind.Keyboard), problem ?? ""));
            return;
        }
        if (leftOut.Count > 0)
        {
            Logging.Logger.Log($"Favourites: the favourite keyboard \"{name}\" came in without {string.Join(", ", leftOut)}");
            _toSay = (FavouriteLocale.KeyboardLeftOutTitle.Resolve(), FavouriteLocale.KeyboardLeftOut(name, string.Join(", ", leftOut)).Resolve());
        }
        WhenPutAway(space, generation, () =>
        {
            if (loaded.IsDestroyed)
                return;
            Retire(space, keep: loaded);
            loaded.AttachComponent<VrKeyboard>();
            _favourite = loaded;
            _loadedFrom = fetched.Path;
            Logging.Logger.Log($"Favourites: typing with the favourite keyboard \"{name}\"");
        }, () =>
        {
            if (!loaded.IsDestroyed)
                loaded.Destroy();
        });
    }

    // The built-in back, when a favourite is up. say waits for the next time the keyboard comes up.
    private static void BuiltIn(World space, int generation, (string Title, string Text)? say)
    {
        if (say != null)
            _toSay = say;
        if (_favourite is not { IsDestroyed: false })
            return;
        WhenPutAway(space, generation, () =>
        {
            if (Userspace.LocalRoot is not { IsDestroyed: false } root)
                return;
            Retire(space, keep: null);
            _favourite = null;
            _loadedFrom = null;
            Userspace.SpawnKeyboard(root);
        }, null);
    }

    // Never under someone's hands: a keyboard that is up stays until it is put away.
    private static void WhenPutAway(World space, int generation, Action swap, Action? dropped)
    {
        if (generation != _generation || space.IsDestroyed)
        {
            dropped?.Invoke();
            return;
        }
        if (VrKeyboard.LocalShown)
        {
            space.RunInUpdates(30, () => WhenPutAway(space, generation, swap, dropped));
            return;
        }
        swap();
    }

    private static void Retire(World space, Slot? keep)
    {
        var root = Userspace.LocalRoot;
        if (root == null)
            return;
        foreach (var keyboard in new List<VrKeyboard>(root.GetComponentsInChildren<VrKeyboard>()))
        {
            if (!keyboard.IsDestroyed && keyboard.Slot != null && !ReferenceEquals(keyboard.Slot, keep))
                keyboard.Slot.Destroy();
        }
    }

    // The fenced load, parked hidden under the user's own space. Null and the reason when it is no keyboard.
    private static string? Load(World space, DataTreeDictionary tree, string name, out Slot? loaded, out List<string> leftOut)
    {
        loaded = null;
        leftOut = new List<string>();
        if (Userspace.LocalRoot is not { IsDestroyed: false } root)
            return null;
        int parts;
        try
        {
            parts = Fence(space, tree, leftOut);
        }
        catch (Exception ex)
        {
            return FavouriteLocale.KeyboardDidNotLoad(name, ex.Message).Resolve();
        }
        if (parts > MostParts)
            return FavouriteLocale.KeyboardTooBig(name, parts, MostParts).Resolve();

        var slot = root.AddSlot("Virtual Keyboard");
        slot.ActiveSelf.Value = false;
        var before = new HashSet<Slot>(space.RootSlot.Children);
        try
        {
            slot.LoadObject(tree, assetsRoot: slot);
        }
        catch (Exception ex)
        {
            slot.Destroy();
            return FavouriteLocale.KeyboardDidNotLoad(name, ex.Message).Resolve();
        }
        foreach (var child in new List<Slot>(space.RootSlot.Children))
        {
            if (!before.Contains(child) && !ReferenceEquals(child, slot))
                child.SetParent(slot, preserveGlobalTransform: false);
        }
        slot.Name.Value = "Virtual Keyboard";
        slot.ActiveSelf.Value = false;
        slot.Persistent.Value = false;

        // Second fence, on what actually loaded: whatever a type migration made of a part is judged as well.
        foreach (var component in new List<Component>(slot.GetComponentsInChildren<Component>()))
        {
            if (!component.IsDestroyed && !Allowed(component.GetType()))
            {
                Note(leftOut, component.GetType());
                component.Destroy();
            }
        }
        if (slot.GetComponent<KeyboardDesign>() == null || !System.Linq.Enumerable.Any(slot.GetComponentsInChildren<VrKey>()))
        {
            slot.Destroy();
            return FavouriteLocale.KeyboardNoKeys(name).Resolve();
        }
        loaded = slot;
        return null;
    }

    // Takes every part the fence refuses out of the saved tree, before anything of it is made, and counts the
    // slots. Never the kept copy of another kind: each keyboard load reads its own.
    private static int Fence(World space, DataTreeDictionary tree, List<string> leftOut)
    {
        var control = new LoadControl(space, new ReferenceTranslator());
        if (tree.TryGetDictionary("TypeVersions") is { } versions)
            control.LoadTypeVersions(versions);
        int slots = 0;
        if (tree.TryGetNode("Object") is DataTreeDictionary root)
            slots += FenceSlot(root, control, leftOut);
        if (tree.TryGetList("Dependencies") is { } dependencies)
        {
            foreach (var entry in dependencies.Children)
            {
                if (entry is DataTreeDictionary slot)
                    slots += FenceSlot(slot, control, leftOut);
            }
        }
        if (tree.TryGetList("Assets") is { } assets)
            FenceComponents(assets, control, leftOut);
        return slots;
    }

    private static int FenceSlot(DataTreeDictionary root, LoadControl control, List<string> leftOut)
    {
        int slots = 0;
        var pending = new Stack<DataTreeDictionary>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var slot = pending.Pop();
            slots++;
            if (slot.TryGetList("Components") is { } components)
                FenceComponents(components, control, leftOut);
            if (slot.TryGetList("Children") is { } children)
            {
                foreach (var child in children.Children)
                {
                    if (child is DataTreeDictionary childSlot)
                        pending.Push(childSlot);
                }
            }
        }
        return slots;
    }

    private static void FenceComponents(DataTreeList components, LoadControl control, List<string> leftOut)
    {
        for (int i = components.Children.Count - 1; i >= 0; i--)
        {
            Type? type = null;
            try
            {
                var (typeName, _) = WorkerSaveLoad.ExtractWorker(components.Children[i], control);
                WorkerManager.TryGetType(typeName, out type);
            }
            catch (Exception)
            {
            }
            if (type != null && Allowed(type))
                continue;
            Note(leftOut, type);
            components.Children.RemoveAt(i);
        }
    }

    public static bool Allowed(Type type)
    {
        if (!typeof(Component).IsAssignableFrom(type) || ComponentExecutionPolicy.IsUserspaceOnly(type) || Never.Contains(type))
            return false;
        if (Also.Contains(type))
            return true;
        string category = type.GetCustomAttribute<ComponentCategoryAttribute>()?.Category ?? "";
        foreach (var allowed in Categories)
        {
            if (category == allowed || category.StartsWith(allowed + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void Note(List<string> leftOut, Type? type)
    {
        string label = type == null ? "unknown parts" : type.Name;
        if (!leftOut.Contains(label))
            leftOut.Add(label);
    }
}

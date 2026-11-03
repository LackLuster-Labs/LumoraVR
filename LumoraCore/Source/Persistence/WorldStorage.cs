// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Persistence;

// Persists a World to and from bytes/files: World.SaveWorld -> data tree -> DataTreeConverter bytes,
// and back. Used for the local home save; richer record/cloud storage and asset bundling can layer
// on top later.
public static class WorldStorage
{
    public static byte[] Serialize(World world) => DataTreeConverter.SaveToBytes(world.SaveWorld());

    public static void Deserialize(World world, byte[] bytes)
    {
        world.LoadWorld((DataTreeDictionary)DataTreeConverter.LoadFromBytes(bytes));
        ObjectAssetHolders.SweepRoot(world);
    }

    // Write the world to path, creating directories as needed. When encrypt is set the blob is AES-GCM
    // encrypted at rest (saved worlds / inventory). The local home is left plain so it loads at startup
    // before anything else.
    public static bool SaveToFile(World world, string path, bool encrypt = false)
    {
        // A world whose content is still being parsed off-thread is EMPTY. Saving it would write that
        // emptiness over the file it is loading from. -xlinka
        if (world == null || world.IsSessionStartPending)
        {
            LumoraLogger.Warn($"WorldStorage: refusing to save '{world?.Name}' to '{path}' - it hasn't finished loading.");
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tree = world.SaveWorld();
            var bytes = DataTreeConverter.SaveToBytes(tree);
            if (encrypt)
                bytes = LocalEncryption.Encrypt(bytes);

            // Write to a temp sibling and move, so a crash mid-write can't corrupt the save.
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            CloudAssetPacker.KeepLocalFiles(tree, Engine.Current?.LocalDB);
            WriteThumbnailSidecar(path, encrypt);
            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldStorage: failed to save '{world?.Name}' to '{path}': {ex.Message}");
            return false;
        }
    }

    // A picture of the world as it looked when you saved it, written beside the save. The world browser
    // shows it on the saved-world card and there is no other source for one: a .lworld carries geometry,
    // not a render.
    //
    // Never fails the save. No view at all (headless, or a build with no capture service wired) just
    // means no sidecar and the card falls back to its placeholder art. -xlinka
    public static string ThumbnailSidecarPath(string savePath) => savePath + ".jpg";

    // An encrypted save gets its picture sealed too; the local home's stays plain like the home itself.
    private static void WriteThumbnailSidecar(string path, bool encrypt)
    {
        try
        {
            var input = Engine.Current?.InputInterface;
            if (input == null || !input.TryCaptureWorldView(ThumbnailWidth, ThumbnailHeight, out var jpeg)
                || jpeg == null || jpeg.Length == 0)
                return;
            if (encrypt)
            {
                LocalEncryption.WireSealedStorage();
                Lumora.Nexus.Storage.SealedFile.WriteAllBytes(ThumbnailSidecarPath(path), jpeg);
            }
            else
            {
                File.WriteAllBytes(ThumbnailSidecarPath(path), jpeg);
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"WorldStorage: couldn't write the thumbnail beside '{path}': {ex.Message}");
        }
    }

    // Same 256x144 the session thumbnail publishes, so both feed the browser's 16:9 card art.
    private const int ThumbnailWidth = 256;
    private const int ThumbnailHeight = 144;

    // Load the world from path; returns false if absent or on failure.
    public static bool LoadFromFile(World world, string path)
        => IntegrateTree(world, ReadTree(path), path);

    // Read half of a load: file read, decrypt, decompress and parse. Touches nothing but the file, so it
    // can run on a task while the main thread keeps drawing frames. Returns null if the file is absent or
    // unreadable (the failure is logged here, exactly as the one-shot load used to log it). -xlinka
    public static DataTreeDictionary? ReadTree(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            // Transparently handle both encrypted and plain (local-home / legacy) saves.
            return (DataTreeDictionary)DataTreeConverter.LoadFromBytes(LocalEncryption.Decrypt(File.ReadAllBytes(path)));
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldStorage: failed to load from '{path}': {ex.Message}");
            return null;
        }
    }

    // IntegrateTree for a world held before it runs: the tree goes back a slot at a time over the next frames,
    // pumped by the deferred session start. whenDone hears whether it all went back, false straight away
    // when there is no tree. -xlinka
    public static void IntegrateTreeStaged(World world, DataTreeDictionary? tree, string path, Action<bool>? whenDone = null)
    {
        if (tree == null)
        {
            whenDone?.Invoke(false);
            return;
        }
        world.BeginStagedLoad(tree, whole =>
        {
            if (!whole)
                LumoraLogger.Error($"WorldStorage: failed to load from '{path}'");
            else
                ObjectAssetHolders.SweepRoot(world);
            whenDone?.Invoke(whole);
        });
    }

    // Integration half: everything that touches the world. Must run on the world's own thread.
    public static bool IntegrateTree(World world, DataTreeDictionary? tree, string path)
    {
        if (tree == null)
            return false;

        try
        {
            world.LoadWorld(tree);
            ObjectAssetHolders.SweepRoot(world);
            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldStorage: failed to load from '{path}': {ex.Message}");
            return false;
        }
    }
}

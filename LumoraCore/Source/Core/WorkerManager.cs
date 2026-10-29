// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Networking;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

public class WorkerManager
{
    public readonly World World;

    private SyncArray<string> indexTable;
    private Dictionary<string, int> nameToIndex;
    private List<Type> types;
    private HashSet<string> typesToRegister;

    // A joiner handed the world in slices holds this table as it stood when the table's own slice went out. A type
    // registered after that reaches it in a delta it only replays once running, so a later slice naming that type
    // by index named an entry the joiner did not have, and its sync loop threw on it every cycle from then on.
    // While a slice is encoded, an index at or past this goes out by name, which needs nothing from the table.
    // Only the sync thread encodes, and it puts this back when the slice is done. -xlinka
    internal int NameIndexesFrom = int.MaxValue;
    internal SyncElement TypeTable => indexTable;
    internal int TypeCount => types.Count;

    public WorkerManager(World world)
    {
        World = world;
        nameToIndex = new Dictionary<string, int>();
        types = new List<Type>();
        typesToRegister = new HashSet<string>();
        
        indexTable = new SyncArray<string>();
        indexTable.Initialize(world, world.RootSlot);
        indexTable.EndInitPhase();
        indexTable.DataWritten += NewIndexes;
        
        if (world.IsAuthority)
        {
            // Index 0 is reserved for null/unknown types
            indexTable.Append("");
        }
    }

    // The type table is the authority's own bookkeeping, not an edit of the world, so a Social or Event world's
    // freeze has nothing to say about it; under the freeze every new type threw here every frame. -xlinka
    public void RegisterTypes()
    {
        if (!World.IsAuthority || typesToRegister.Count == 0)
        {
            return;
        }

        using (World.DataModelPermissions?.EnterSystemBypass())
        {
            foreach (string typeName in typesToRegister)
            {
                RegisterType(typeName);
            }
        }
        typesToRegister.Clear();
    }

    public void InformOfTypeUse(Type type)
    {
        if (World.IsAuthority && !nameToIndex.ContainsKey(type.FullName!))
        {
            typesToRegister.Add(type.FullName!);
        }
    }

    private void NewIndexes(SyncArray<string> sender, int startIndex, int length)
    {
        for (int i = 0; i < length; i++)
        {
            int index = startIndex + i;

            // Skip indices we already know (happens when a full-snapshot delta re-sends all entries)
            if (index < types.Count)
                continue;

            if (index == 0)
            {
                types.Add(null!);
                continue;
            }

            string typeName = sender[index];
            Type type = GetType(typeName);
            nameToIndex[typeName] = index;
            types.Add(type);
        }
    }

    public int GetIndex(Type type)
    {
        int value = 0;
        nameToIndex.TryGetValue(type.FullName!, out value);
        if (value == 0)
        {
            InformOfTypeUse(type);
        }
        return value;
    }

    private int RegisterType(string typeName)
    {
        int count = indexTable.Count;
        indexTable.Append(typeName);
        return count;
    }

    public void EncodeType(BinaryWriter writer, Type type)
    {
        int index = GetIndex(type);
        if (index >= NameIndexesFrom)
            index = 0;
        writer.Write7BitEncoded((ulong)index);
        if (index == 0)
        {
            // Type not in index table yet, write full name
            writer.Write(type.FullName!);
        }
    }

    public Type DecodeType(BinaryReader reader)
    {
        int index = (int)reader.Read7BitEncoded();

        // A record that got here ahead of the table entry it names. Not corrupt, early: thrown as the kind of
        // failure a pending record waits out, so it decodes once the entry lands. -xlinka
        if (index >= types.Count)
        {
            throw new InvalidOperationException($"Unknown type index: {index}, total known indexes: {types.Count}");
        }

        if (index > 0)
        {
            Type resolved = types[index];
            if (resolved == null)
            {
                // This index resolved to null when the type table first synced - almost always because
                // the owning assembly wasn't loaded yet. Don't trust that forever: try again now (cheap,
                // GetType caches hits) and heal the slot if it resolves. Without this, ONE early miss
                // permanently nulls every component of that type for the world's whole lifetime, which
                // silently drops the component and orphans its child slots. A still-null result just
                // means a host type we genuinely don't have. -xlinka
                string name = indexTable[index];
                resolved = GetType(name);
                if (resolved != null)
                    types[index] = resolved;
            }
            return resolved!;
        }

        // Index 0 means type name follows
        string typeName = reader.ReadString();
        Type type = GetType(typeName);
        InformOfTypeUse(type);
        return type;
    }

    public static T Instantiate<T>() where T : IWorker, new()
    {
        return new T();
    }

    public static IWorker Instantiate(Type type)
    {
        try
        {
            return (IWorker)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error instantiating type: {ex}", ex);
        }
    }

    public static IWorker Instantiate(string typename)
    {
        try
        {
            return Instantiate(GetType(typename));
        }
        catch (Exception innerException)
        {
            throw new Exception($"Error instantiating type with typename: {typename}", innerException);
        }
    }

    // Resolved types are cached across all worlds (a type name maps to the same Type process-wide).
    // We only cache hits - a miss may resolve later once its assembly is loaded, so we never cache
    // null. -xlinka
    private static readonly Dictionary<string, Type> _typeCache = new();

    // Renamed component types, old full name -> current full name, so worlds/items saved before a
    // rename still load. Add an entry here whenever a persisted component class is renamed. -xlinka
    // Guarded by _typeCache's lock, since a rename decides which cache key a lookup lands on.
    private static readonly Dictionary<string, string> _renamedTypes = new()
    {
        ["Lumora.Core.Components.Avatar.AvatarObjectSlot"] = "Lumora.Core.Components.Avatar.AvatarSocket",
        ["Lumora.Core.Components.Avatar.AvatarPoseSmoothLerp"] = "Lumora.Core.Components.Avatar.PoseSmoother",
        ["Lumora.Core.Components.Avatar.AvatarPoseNode"] = "Lumora.Core.Components.Avatar.AvatarPoseDriver",
        ["Lumora.Core.Components.Avatar.AvatarManager"] = "Lumora.Core.Components.Avatar.AvatarEquipManager",
        ["Lumora.Core.Components.Avatar.AvatarRoot"] = "Lumora.Core.Components.Avatar.AvatarForm",
        ["Lumora.Core.Components.Avatar.BipedRig"] = "Lumora.Core.Components.Avatar.HumanoidRig",
        ["Lumora.Core.Components.Avatar.HandPoser"] = "Lumora.Core.Components.Avatar.HandPoseDriver",
        ["Lumora.Core.Components.Avatar.AvatarFingerPoseInfo"] = "Lumora.Core.Components.Avatar.UserHandPoseInfo",
        ["Lumora.Core.Components.Avatar.AvatarHandDataAssigner"] = "Lumora.Core.Components.Avatar.HandPoseBinder",
        ["Lumora.Core.Components.Avatar.FingerPoseStreamManager"] = "Lumora.Core.Components.Avatar.HandPoseStreamManager",
        ["Lumora.Core.Components.Avatar.FingerPoseLerp"] = "Lumora.Core.Components.Avatar.HandPoseBlend",
        ["Lumora.Core.Components.Avatar.FingerPosePreset"] = "Lumora.Core.Components.Avatar.HandPosePreset",
        ["Lumora.Core.Components.Avatar.FingerPoseModifier"] = "Lumora.Core.Components.Avatar.HandPoseModifier",
        ["Lumora.Core.Components.Avatar.FingerPoseMultiplexer"] = "Lumora.Core.Components.Avatar.HandPoseMultiplexer",
        ["Lumora.Core.Components.Avatar.FingerSplayModifier"] = "Lumora.Core.Components.Avatar.HandSplayModifier",
        ["Lumora.Core.Components.Avatar.AvatarDestroyOnDequip"] = "Lumora.Core.Components.Avatar.DiscardOnDequip",
        ["Lumora.Core.Components.Avatar.AvatarNameTagAssigner"] = "Lumora.Core.Components.Avatar.NameBadgeDriver",
        ["Lumora.Core.Components.Avatar.CommonAvatarBuilder"] = "Lumora.Core.Components.Avatar.AvatarAssembler",
        ["Lumora.Core.Components.Avatar.AvatarCreator"] = "Lumora.Core.Components.Avatar.AvatarStudio",
        ["Lumora.Core.Components.Avatar.DirectVisemeDriver"] = "Lumora.Core.Components.Avatar.VisemeWeightDriver",
        ["Lumora.Core.Components.Avatar.VisemeAnalyzer"] = "Lumora.Core.Components.Avatar.LipSyncAnalyzer",
        ["Lumora.Core.Components.Avatar.EyeRotationDriver"] = "Lumora.Core.Components.Avatar.EyeGazeDriver",
        ["Lumora.Core.Components.Avatar.EyeTrackingStreamManager"] = "Lumora.Core.Components.Avatar.EyeStreamManager",
        ["Lumora.Core.Components.Avatar.MouthTrackingStreamManager"] = "Lumora.Core.Components.Avatar.MouthStreamManager",
        ["Lumora.Core.Components.Avatar.ExpressionDriver"] = "Lumora.Core.Components.Avatar.MouthExpressionDriver",
        // The clock driver moved out of the UI widgets and onto the general text drivers, where it works
        // on any string field rather than only a Helio text. Its Target changed from a plain reference to
        // a drive, so an old save resolves the component and its format but comes back with the clock
        // unwired; re-point it once and the save keeps it. -xlinka
        ["Lumora.Core.Components.UI.CurrentDateTimeTextDriver"] = "Lumora.Core.Components.Utility.CurrentDateTimeTextDriver",
        // The dual-sided material split into a metallic and a specular member. The one type before the
        // split had Metallic and Smoothness and the metallic shader, so its saves load as the metallic one,
        // every member under the same name. -xlinka
        ["Lumora.Core.Assets.PBS_DualSided"] = "Lumora.Core.Assets.PBS_MetallicDualSided",
        ["Lumora.Core.Components.ItemSeal"] = "Lumora.Core.Components.AvatarProtection",
    };

    // Register a rename alias at runtime. Same contract as the table above; this is the entry point for
    // an alias that becomes known after startup (a plugin declaring what it used to be called), and it
    // clears any cached miss so content already refused starts resolving. -xlinka
    public static void RegisterRenamedType(string oldTypeName, string currentTypeName)
    {
        if (string.IsNullOrEmpty(oldTypeName) || string.IsNullOrEmpty(currentTypeName))
            throw new ArgumentException("A rename alias needs both names.");

        lock (_typeCache)
        {
            _renamedTypes[oldTypeName] = currentTypeName;
        }
    }

    public static Type GetType(string typename)
    {
        if (TryGetType(typename, out var type))
            return type;

        // A type the host used can't be resolved here. The component will be skipped on this client
        // and its whole subtree will go missing - this is the loud signal for that. -xlinka
        LumoraLogger.Error($"WorkerManager.GetType: UNRESOLVED TYPE '{typename}' - a host component of this type will be SKIPPED on this client (assembly not loaded or name mismatch). Joined world will be incomplete.");
        return null!;
    }

    // Same lookup without the shouting. A save file legitimately names types this build does not have,
    // and the persistence layer reports that once, in its own words, after deciding what to do about
    // it - so it needs a way to ask that does not log an error per miss. -xlinka
    public static bool TryGetType(string typename, out Type type)
    {
        type = null!;
        if (string.IsNullOrEmpty(typename))
            return false;

        lock (_typeCache)
        {
            if (_renamedTypes.TryGetValue(typename, out var currentName))
                typename = currentName;

            if (_typeCache.TryGetValue(typename, out var cached))
            {
                type = cached;
                return true;
            }
        }

        type = Type.GetType(typename)!;
        if (type == null)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(typename)!;
                if (type != null)
                    break;
            }
        }

        if (type == null)
            return false;

        lock (_typeCache)
        {
            _typeCache[typename] = type;
        }
        return true;
    }
}


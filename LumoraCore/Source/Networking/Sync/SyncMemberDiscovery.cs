using System;
using System.Collections.Generic;
using System.Reflection;
using Lumora.Core;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Discovers and initializes sync members via reflection.
/// </summary>
public static class SyncMemberDiscovery
{
    /// <summary>
    /// Discover all Sync<T> fields in an object and initialize them.
    /// Returns list of discovered sync members.
    /// </summary>
    public static List<ISyncMember> DiscoverSyncMembers(ISyncObject target)
    {
        var syncMembers = new List<ISyncMember>();
        Type type = target.GetType();

        // Find all fields that are ISyncMember
        FieldInfo[] fields = type.GetFields(
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance
        );

        int memberIndex = 0;
        foreach (var field in fields)
        {
            // Check if field is ISyncMember
            if (typeof(ISyncMember).IsAssignableFrom(field.FieldType))
            {
                // Get existing value or create new instance
                ISyncMember syncMember = (ISyncMember)field.GetValue(target);

                if (syncMember == null)
                {
                    // Create new instance if null
                    try
                    {
                        syncMember = (ISyncMember)Activator.CreateInstance(field.FieldType);
                        field.SetValue(target, syncMember);
                    }
                    catch (Exception ex)
                    {
                        AquaLogger.Error($"Failed to create sync member {field.Name}: {ex.Message}");
                        continue;
                    }
                }

                // Initialize member
                syncMember.MemberIndex = memberIndex;
                syncMember.Name = field.Name;

                syncMembers.Add(syncMember);
                memberIndex++;
            }
        }

        AquaLogger.Debug($"Discovered {syncMembers.Count} sync members in {type.Name}");
        return syncMembers;
    }

    /// <summary>
    /// Discover and initialize sync members with world context.
    /// Each sync member gets its own RefID from the world's ReferenceController.
    /// </summary>
    public static List<ISyncMember> DiscoverAndInitializeSyncMembers(object target, World world, IWorldElement parent)
    {
        var syncMembers = new List<ISyncMember>();
        Type type = target.GetType();
        int memberIndex = 0;

        // Find all fields that are ISyncMember
        FieldInfo[] fields = type.GetFields(
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance
        );

        foreach (var field in fields)
        {
            // Check if field is ISyncMember
            if (typeof(ISyncMember).IsAssignableFrom(field.FieldType))
            {
                // Get existing value or create new instance
                ISyncMember syncMember = (ISyncMember)field.GetValue(target);

                if (syncMember == null)
                {
                    // Create new instance if null
                    try
                    {
                        syncMember = (ISyncMember)Activator.CreateInstance(field.FieldType);
                        field.SetValue(target, syncMember);
                    }
                    catch (Exception ex)
                    {
                        AquaLogger.Error($"Failed to create sync member {field.Name}: {ex.Message}");
                        continue;
                    }
                }

                // Set member metadata
                syncMember.MemberIndex = memberIndex;
                syncMember.Name = field.Name;

                // Initialize with world context (allocates RefID and registers)
                // Only initialize if not already initialized
                if (world != null && syncMember.World == null)
                {
                    syncMember.Initialize(world, parent);
                }

                syncMembers.Add(syncMember);
                memberIndex++;
            }
        }

        // Also find properties that are ISyncMember (auto-properties)
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance
        );

        foreach (var prop in properties)
        {
            // Check if property is ISyncMember and has a getter
            if (typeof(ISyncMember).IsAssignableFrom(prop.PropertyType) && prop.CanRead)
            {
                try
                {
                    ISyncMember syncMember = (ISyncMember)prop.GetValue(target);

                    if (syncMember != null)
                    {
                        // Set member metadata if not already set
                        if (syncMember.Name == null)
                        {
                            syncMember.MemberIndex = memberIndex;
                            syncMember.Name = prop.Name;
                        }

                        // Initialize with world context if not already initialized
                        if (world != null && syncMember.World == null)
                        {
                            syncMember.Initialize(world, parent);
                        }

                        // Only add if not already in the list (avoid duplicates from backing fields)
                        if (!syncMembers.Contains(syncMember))
                        {
                            syncMembers.Add(syncMember);
                            memberIndex++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AquaLogger.Error($"Failed to access sync member property {prop.Name}: {ex.Message}");
                }
            }
        }

        AquaLogger.Debug($"Discovered and initialized {syncMembers.Count} sync members in {type.Name}");
        return syncMembers;
    }

    /// <summary>
    /// Initialize already discovered sync members with world context.
    /// </summary>
    public static void InitializeSyncMembers(List<ISyncMember> members, World world, IWorldElement parent)
    {
        if (world == null) return;

        foreach (var member in members)
        {
            member.Initialize(world, parent);
        }
    }

    /// <summary>
    /// Get all dirty sync members (changed since last sync).
    /// </summary>
    public static List<ISyncMember> GetDirtySyncMembers(List<ISyncMember> members)
    {
        var dirty = new List<ISyncMember>();
        foreach (var member in members)
        {
            if (member.IsDirty)
            {
                dirty.Add(member);
            }
        }
        return dirty;
    }

    /// <summary>
    /// Clear dirty flags on all sync members.
    /// Called after successful sync.
    /// </summary>
    public static void ClearDirtyFlags(List<ISyncMember> members)
    {
        foreach (var member in members)
        {
            member.IsDirty = false;
        }
    }

    /// <summary>
    /// Mark all sync members as dirty.
    /// Used for full state sync.
    /// </summary>
    public static void MarkAllDirty(List<ISyncMember> members)
    {
        foreach (var member in members)
        {
            member.IsDirty = true;
        }
    }
}

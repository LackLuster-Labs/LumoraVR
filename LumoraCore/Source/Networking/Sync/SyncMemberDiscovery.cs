using System;
using System.Collections.Generic;
using System.Reflection;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Discovers and initializes sync members via reflection.
/// 
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

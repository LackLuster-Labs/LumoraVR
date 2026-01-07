using System;
using System.IO;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Base interface for all sync members.
/// 
/// </summary>
public interface ISyncMember
{
    /// <summary>
    /// Index of this sync member in the parent's sync member list.
    /// Assigned during discovery.
    /// </summary>
    int MemberIndex { get; set; }

    /// <summary>
    /// Name of this sync member (field name).
    /// </summary>
    string Name { get; set; }

    /// <summary>
    /// Whether this member has changed since last sync.
    /// </summary>
    bool IsDirty { get; set; }

    /// <summary>
    /// Version of this member's value.
    /// Increments on every change.
    /// </summary>
    ulong Version { get; set; }

    /// <summary>
    /// Write this member's current value to a binary writer.
    /// </summary>
    void Encode(BinaryWriter writer);

    /// <summary>
    /// Read a new value from a binary reader and apply it.
    /// </summary>
    void Decode(BinaryReader reader);

    /// <summary>
    /// Get the current value as object.
    /// </summary>
    object GetValueAsObject();
}

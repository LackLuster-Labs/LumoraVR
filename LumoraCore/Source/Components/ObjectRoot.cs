// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components;

/// <summary>
/// Marker component for object root detection.
/// Attach to a slot to mark it as the root of a logical object.
/// </summary>
[ComponentCategory("Internal")]
public class ObjectRoot : Component
{
    /// <summary>
    /// Optional display name for this object.
    /// </summary>
    public readonly Sync<string> ObjectName = new();

    /// <summary>
    /// Whether this object can be grabbed.
    /// </summary>
    public readonly Sync<bool> Grabbable = new();

    /// <summary>
    /// Whether this object can be duplicated.
    /// </summary>
    public readonly Sync<bool> Duplicatable = new();

    /// <summary>
    /// Whether this object can be destroyed by users.
    /// </summary>
    public readonly Sync<bool> Destroyable = new();

    public override void OnInit()
    {
        base.OnInit();

        // ObjectName = string.Empty (C# default null is close enough for Sync<string>, but original used string.Empty)
        ObjectName.Value = string.Empty;
        Grabbable.Value = true;
        Duplicatable.Value = true;
        Destroyable.Value = true;
    }

    /// <summary>
    /// Get the display name (falls back to slot name).
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(ObjectName.Value) ? Slot.Name.Value : ObjectName.Value;
}

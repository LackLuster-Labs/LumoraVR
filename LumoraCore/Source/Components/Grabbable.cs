// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components;

/// <summary>
/// Marks a slot as grabbable by input grabbers.
/// </summary>
[ComponentCategory("Interaction")]
public sealed class Grabbable : Component
{
    /// <summary>
    /// Whether this object can currently be grabbed.
    /// </summary>
    public readonly Sync<bool> AllowGrab = new();

    /// <summary>
    /// Whether rotation should follow the grabber.
    /// </summary>
    public readonly Sync<bool> FollowRotation = new();

    public override void OnInit()
    {
        base.OnInit();
        AllowGrab.Value = true;
        FollowRotation.Value = false;
    }
}

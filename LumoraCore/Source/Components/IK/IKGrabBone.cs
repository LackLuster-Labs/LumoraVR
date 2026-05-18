// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.IK;

/// <summary>
/// Makes an IK bone grabbable by users for interaction.
/// </summary>
[ComponentCategory("IK")]
public class IKGrabBone : Component
{
    /// <summary>
    /// Whether grabbing this bone also grabs child bones.
    /// </summary>
    public readonly Sync<bool> GrabChildren = new();

    /// <summary>
    /// Maximum force applied when grabbed.
    /// </summary>
    public readonly Sync<float> MaximumForce = new();

    /// <summary>
    /// How rigid the bone is when grabbed.
    /// </summary>
    public readonly Sync<float> Rigidity = new();

    public override void OnInit()
    {
        base.OnInit();
        GrabChildren.Value = true;
        MaximumForce.Value = 100.0f;
        Rigidity.Value = 1.0f;
    }
}

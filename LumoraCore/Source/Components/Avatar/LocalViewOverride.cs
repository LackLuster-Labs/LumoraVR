// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// Overrides a slot's rendered transform when viewed from a specific rendering context.
///
/// Primary use: scale the local user's head to zero in UserView so the player
/// doesn't see their own avatar head, while other users and floor shadows are unaffected.
///
/// Usage:
///   var lvo = headVisual.AttachComponent&lt;LocalViewOverride&gt;();
///   lvo.Context.Value          = RenderingContext.UserView;
///   lvo.HasScaleOverride.Value = true;
///   lvo.ScaleOverride.Value    = float3.Zero;
/// </summary>
[ComponentCategory("Avatar")]
public class LocalViewOverride : ImplementableComponent<IHook>
{
    /// <summary>Which rendering context activates this override.</summary>
    public readonly Sync<RenderingContext> Context = new();

    /// <summary>When true, PositionOverride replaces the slot's normal position.</summary>
    public readonly Sync<bool> HasPositionOverride = new();
    public readonly Sync<float3> PositionOverride = new();

    /// <summary>When true, RotationOverride replaces the slot's normal rotation.</summary>
    public readonly Sync<bool> HasRotationOverride = new();
    public readonly Sync<floatQ> RotationOverride = new();

    /// <summary>
    /// When true, ScaleOverride replaces the slot's normal scale.
    /// Set to float3.Zero to make the slot invisible in the target context
    /// while keeping shadow casting intact.
    /// </summary>
    public readonly Sync<bool> HasScaleOverride = new();
    public readonly Sync<float3> ScaleOverride = new();

    public override void OnInit()
    {
        base.OnInit();
        Context.Value = RenderingContext.UserView;
        // HasPositionOverride = false (C# default, skip)
        // PositionOverride = float3.Zero (C# default, skip)
        // HasRotationOverride = false (C# default, skip)
        RotationOverride.Value = floatQ.Identity;
        // HasScaleOverride = false (C# default, skip)
        ScaleOverride.Value = float3.One;
    }
}

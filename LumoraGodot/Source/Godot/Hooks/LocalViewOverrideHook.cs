// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

/// <summary>
/// Godot hook for LocalViewOverride.
///
/// When Context == UserView and this slot belongs to the local user,
/// sets every MeshInstance3D under the slot to ShadowsOnly — invisible
/// to the camera but still casting shadows on the floor.
///
/// When the context does not apply, restores normal shadow casting.
/// </summary>
public class LocalViewOverrideHook : ComponentHook<LocalViewOverride>
{
    private bool _wasApplied = false;

    public static IHook<LocalViewOverride> Constructor() => new LocalViewOverrideHook();

    public override void Initialize()
    {
        base.Initialize();
        LumoraLogger.Log($"LocalViewOverrideHook: Initialized on '{Owner.Slot.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        bool shouldApply = Owner.Enabled
            && Owner.Context.Value == RenderingContext.UserView
            && IsLocalUser();

        if (shouldApply)
        {
            // Re-apply every frame to catch MeshInstance3Ds created after us
            SetMeshCastShadow(GeometryInstance3D.ShadowCastingSetting.ShadowsOnly);
            _wasApplied = true;
        }
        else if (_wasApplied)
        {
            SetMeshCastShadow(GeometryInstance3D.ShadowCastingSetting.On);
            _wasApplied = false;
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_wasApplied && !destroyingWorld)
            SetMeshCastShadow(GeometryInstance3D.ShadowCastingSetting.On);

        base.Destroy(destroyingWorld);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SetMeshCastShadow(GeometryInstance3D.ShadowCastingSetting setting)
    {
        var slotNode = ((SlotHook)Owner.Slot.Hook).GeneratedNode3D;
        if (slotNode == null || !GodotObject.IsInstanceValid(slotNode)) return;

        var nodes = slotNode.FindChildren("*", "MeshInstance3D", recursive: true, owned: false);
        foreach (var node in nodes)
        {
            if (node is MeshInstance3D mesh)
                mesh.CastShadow = setting;
        }
    }

    private bool IsLocalUser()
    {
        var slot = Owner.Slot;
        while (slot != null)
        {
            var userRoot = slot.GetComponent<UserRoot>();
            if (userRoot != null)
                return userRoot.ActiveUser?.IsLocal ?? false;
            slot = slot.Parent;
        }
        return false;
    }
}

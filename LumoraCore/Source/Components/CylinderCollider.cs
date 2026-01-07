using Lumora.Core;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Cylinder-shaped collider.
/// </summary>
[ComponentCategory("Physics/Colliders")]
public class CylinderCollider : Collider
{
    // ===== SYNC FIELDS =====

    public readonly Sync<float> Radius;
    public readonly Sync<float> Height;

    // ===== INITIALIZATION =====

    public CylinderCollider()
    {
        Radius = new Sync<float>(this, 1f);
        Height = new Sync<float>(this, 1f);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Radius.OnChanged += _ => RunApplyChanges();
        Height.OnChanged += _ => RunApplyChanges();
        AquaLogger.Log($"CylinderCollider: Initialized with Radius={Radius.Value}, Height={Height.Value}");
    }

    // ===== ABSTRACT METHOD IMPLEMENTATIONS =====

    public override object CreateGodotShape()
    {
        // Created by PhysicsColliderHook
        return null;
    }

    public override object GetLocalBounds()
    {
        // Axis-aligned bounds - cylinder inscribed in box
        var r = Radius.Value;
        var h = Height.Value * 0.5f;
        var min = new Math.float3(-r, -h, -r) + Offset.Value;
        var max = new Math.float3(r, h, r) + Offset.Value;
        return new { Min = min, Max = max };
    }
}

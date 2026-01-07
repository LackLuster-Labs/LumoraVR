using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Sphere-shaped collider.
/// </summary>
[ComponentCategory("Physics/Colliders")]
public class SphereCollider : Collider
{
    // ===== SYNC FIELDS =====

    public readonly Sync<float> Radius;

    // ===== INITIALIZATION =====

    public SphereCollider()
    {
        Radius = new Sync<float>(this, 0.5f);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Radius.OnChanged += _ => RunApplyChanges();
        AquaLogger.Log($"SphereCollider: Initialized with Radius={Radius.Value}");
    }

    // ===== ABSTRACT METHOD IMPLEMENTATIONS =====

    public override object CreateGodotShape()
    {
        // Created by PhysicsColliderHook
        return null;
    }

    public override object GetLocalBounds()
    {
        float r = Radius.Value;
        float3 min = Offset.Value + new float3(-r, -r, -r);
        float3 max = Offset.Value + new float3(r, r, r);
        return new { Min = min, Max = max };
    }

}

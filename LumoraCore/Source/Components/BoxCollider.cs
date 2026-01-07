using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Box-shaped collider (rectangular prism).
/// </summary>
[ComponentCategory("Physics/Colliders")]
public class BoxCollider : Collider
{
    // ===== SYNC FIELDS =====

    public readonly Sync<float3> Size;

    // ===== INITIALIZATION =====

    public BoxCollider()
    {
        Size = new Sync<float3>(this, float3.One);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        AquaLogger.Log($"BoxCollider: Initialized with Size={Size.Value}");
    }

    // ===== ABSTRACT METHOD IMPLEMENTATIONS =====

    public override object CreateGodotShape()
    {
        // Created by PhysicsColliderHook
        return null;
    }

    public override object GetLocalBounds()
    {
        // Axis-aligned bounds centered at Offset with extents Size/2
        var half = Size.Value * 0.5f;
        float3 min = Offset.Value - half;
        float3 max = Offset.Value + half;
        return new { Min = min, Max = max };
    }

}

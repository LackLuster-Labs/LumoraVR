using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Capsule-shaped collider (cylinder with hemispherical ends).
/// </summary>
[ComponentCategory("Physics/Colliders")]
public class CapsuleCollider : Collider
{
    // ===== SYNC FIELDS =====

    public readonly Sync<float> Height;
    public readonly Sync<float> Radius;

    /// <summary>
    /// Cylinder length (excluding the spherical caps).
    /// Height = Length + (Radius * 2)
    /// </summary>
    public float Length
    {
        get => System.Math.Max(0f, Height.Value - Radius.Value * 2f);
        set => Height.Value = value + Radius.Value * 2f;
    }

    // ===== INITIALIZATION =====

    public CapsuleCollider()
    {
        Height = new Sync<float>(this, 2.0f);
        Radius = new Sync<float>(this, 0.5f);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Height.OnChanged += _ => RunApplyChanges();
        Radius.OnChanged += _ => RunApplyChanges();
        AquaLogger.Log($"CapsuleCollider: Initialized with Height={Height.Value}, Radius={Radius.Value}");
    }

    // ===== ABSTRACT METHOD IMPLEMENTATIONS =====

    public override object CreateGodotShape()
    {
        // This is handled by CapsuleColliderHook
        // Return null for now (hook creates the actual Godot CapsuleShape3D)
        return null;
    }

    public override object GetLocalBounds()
    {
        // Return AABB that encompasses the capsule
        // Capsule is centered at Offset with height along Y axis
        float halfHeight = Height.Value / 2f;
        float3 min = Offset.Value + new float3(-Radius.Value, -halfHeight, -Radius.Value);
        float3 max = Offset.Value + new float3(Radius.Value, halfHeight, Radius.Value);

        // Return as object (will be Aabb in Godot)
        return new { Min = min, Max = max };
    }

}

using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Lumora.Core.Physics;

/// <summary>
/// Interface for components that own and manage Collider components.
/// Defines the contract for physics collider ownership.
///
/// Examples: CharacterController, Rigidbody
///
/// NOTE: This interface defines the contract for collider owners.
/// Platform-specific implementations will implement this with their own Collider types.
/// </summary>
public interface IColliderOwner
{
    /// <summary>
    /// Whether this owner's colliders should be kinematic (non-physics-driven).
    /// </summary>
    bool Kinematic { get; }

    /// <summary>
    /// Called when a collider's shape changes.
    /// </summary>
    void OnColliderShapeChanged(Collider collider);

    /// <summary>
    /// Called when a collider registers with this owner.
    /// </summary>
    void OnColliderAdded(Collider collider);

    /// <summary>
    /// Called when a collider unregisters from this owner.
    /// </summary>
    void OnColliderRemoved(Collider collider);

    /// <summary>
    /// Post-process the collider's bounds offset.
    /// Used by CharacterController to adjust collider positioning.
    /// </summary>
    void PostprocessBoundsOffset(ref float3 offset);
}

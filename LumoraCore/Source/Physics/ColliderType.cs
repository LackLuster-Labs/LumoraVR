namespace Lumora.Core.Physics;

/// <summary>
/// Collider physics type - determines how it interacts with physics simulation.
/// Standard physics collider type enumeration.
/// </summary>
public enum ColliderType
{
    /// <summary>
    /// No collision - ignores all physics.
    /// </summary>
    NoCollision,

    /// <summary>
    /// Static collider - doesn't move, provides collision for dynamic objects.
    /// </summary>
    Static,

    /// <summary>
    /// Trigger - detects overlaps but doesn't provide physical collision.
    /// </summary>
    Trigger,

    /// <summary>
    /// Active physics - full physics simulation with forces.
    /// </summary>
    Active,

    /// <summary>
    /// Character controller - specialized for character movement.
    /// </summary>
    CharacterController
}

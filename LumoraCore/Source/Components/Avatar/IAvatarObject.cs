using System.Collections.Generic;
using Lumora.Core.Input;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// Interface for avatar objects that can be equipped to body node slots.
/// </summary>
public interface IAvatarObject
{
    /// <summary>
    /// The body node this avatar object corresponds to.
    /// </summary>
    BodyNode Node { get; }

    /// <summary>
    /// Whether this avatar object is currently equipped.
    /// </summary>
    bool IsEquipped { get; }

    /// <summary>
    /// Priority for equipping order (higher = later).
    /// </summary>
    int EquipOrderPriority { get; }

    /// <summary>
    /// The AvatarObjectSlot this object is equipped to.
    /// </summary>
    AvatarObjectSlot EquippingSlot { get; }

    /// <summary>
    /// Body nodes that cannot be equipped simultaneously with this object.
    /// </summary>
    IEnumerable<BodyNode> MutuallyExclusiveNodes { get; }

    /// <summary>
    /// User explicitly allowed to equip this object.
    /// </summary>
    User ExplicitlyAllowedUser { get; }

    /// <summary>
    /// Equip this avatar object to the given slot.
    /// </summary>
    void Equip(AvatarObjectSlot slot);

    /// <summary>
    /// Dequip this avatar object from its current slot.
    /// </summary>
    void Dequip();

    /// <summary>
    /// Explicitly allow a user to equip this object.
    /// </summary>
    void ExplicitlyAllowEquip(User user);
}

/// <summary>
/// Interface for components on avatar objects that receive equip/dequip events.
/// </summary>
public interface IAvatarObjectComponent
{
    /// <summary>
    /// Called before equipping to the slot.
    /// </summary>
    void OnPreEquip(AvatarObjectSlot slot);

    /// <summary>
    /// Called when equipped to the slot.
    /// </summary>
    void OnEquip(AvatarObjectSlot slot);

    /// <summary>
    /// Called when dequipped from the slot.
    /// </summary>
    void OnDequip(AvatarObjectSlot slot);
}

/// <summary>
/// Interface for pose filters that can modify tracking data.
/// </summary>
public interface IAvatarPoseFilter
{
    /// <summary>
    /// Process and potentially modify the pose data.
    /// </summary>
    void ProcessPose(AvatarObjectSlot slot, Slot space, ref Math.float3 position, ref Math.floatQ rotation, ref bool isTracking);
}

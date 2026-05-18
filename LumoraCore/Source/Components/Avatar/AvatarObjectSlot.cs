// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// A slot that can have avatar objects equipped to it for a specific body node.
/// Used by TrackedDevicePositioner to create body node tracking points.
/// </summary>
[ComponentCategory("Users/Common Avatar System")]
public class AvatarObjectSlot : Component
{
    /// <summary>
    /// The currently equipped avatar object.
    /// </summary>
    public LinkRef<IAvatarObject> Equipped { get; private set; }

    /// <summary>
    /// The body node this slot corresponds to.
    /// </summary>
    public readonly Sync<BodyNode> Node = new();

    /// <summary>
    /// Whether this slot is currently tracking.
    /// </summary>
    public readonly Sync<bool> IsTracking = new();

    /// <summary>
    /// Whether this slot's device is active.
    /// </summary>
    public readonly Sync<bool> IsActive = new();

    /// <summary>
    /// Whether to drive the active state of equipped objects.
    /// </summary>
    public readonly Sync<bool> DriveActive = new();

    /// <summary>
    /// Whether to drive the scale of equipped objects.
    /// </summary>
    public readonly Sync<bool> DriveScale = new();

    /// <summary>
    /// List of pose filters to apply to tracking data.
    /// Using a simple list since IAvatarPoseFilter doesn't implement IWorldElement.
    /// </summary>
    private readonly List<IAvatarPoseFilter> _filters = new();

    /// <summary>
    /// Get the pose filters list (read-only enumerable).
    /// </summary>
    public IEnumerable<IAvatarPoseFilter> Filters => _filters;

    /// <summary>
    /// Add a pose filter.
    /// </summary>
    public void AddFilter(IAvatarPoseFilter filter)
    {
        if (filter != null && !_filters.Contains(filter))
            _filters.Add(filter);
    }

    /// <summary>
    /// Remove a pose filter.
    /// </summary>
    public void RemoveFilter(IAvatarPoseFilter filter)
    {
        _filters.Remove(filter);
    }

    /// <summary>
    /// Whether an object is currently equipped.
    /// </summary>
    public bool HasEquipped => Equipped?.Target != null;

    // Internal state
    private UserRoot _userRoot;

    public override void OnAwake()
    {
        base.OnAwake();

        Equipped = new LinkRef<IAvatarObject>(this);
    }

    public override void OnInit()
    {
        base.OnInit();
        // BodyNode.NONE is not enum value 0 — set it explicitly
        Node.Value = BodyNode.NONE;
        // IsTracking defaults to true (not C# default false)
        IsTracking.Value = true;
        // IsActive defaults to true
        IsActive.Value = true;
        // DriveActive = false (C# default, skip)
        // DriveScale = false (C# default, skip)
    }

    public override void OnStart()
    {
        base.OnStart();
        FindUserRoot();
    }

    private void FindUserRoot()
    {
        _userRoot = Slot.GetComponent<UserRoot>();
        var current = Slot.Parent;
        while (_userRoot == null && current != null)
        {
            _userRoot = current.GetComponent<UserRoot>();
            current = current.Parent;
        }
    }

    /// <summary>
    /// Check if this slot is under the local user.
    /// </summary>
    public bool IsUnderLocalUser
    {
        get
        {
            if (_userRoot == null)
                FindUserRoot();
            return _userRoot?.ActiveUser == World?.LocalUser;
        }
    }

    /// <summary>
    /// Pre-equip an avatar object - dequips any existing object first.
    /// </summary>
    public bool PreEquip(IAvatarObject avatarObject, HashSet<IAvatarObject> dequippedObjects)
    {
        if (avatarObject.Node == Node.Value)
        {
            Dequip(dequippedObjects);

            // Call OnPreEquip on all IAvatarObjectComponent in the avatar's slot
            ForeachObjectComponent(avatarObject as Component, (c) =>
            {
                try
                {
                    c.OnPreEquip(this);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Error($"Exception in OnPreEquip: {ex.Message}");
                }
            });

            return true;
        }
        return false;
    }

    /// <summary>
    /// Equip an avatar object to this slot.
    /// </summary>
    public void Equip(IAvatarObject avatarObject)
    {
        Equipped.Target = avatarObject;
        avatarObject.Equip(this);

        // Call OnEquip on all IAvatarObjectComponent
        ForeachObjectComponent(avatarObject as Component, (c) =>
        {
            try
            {
                c.OnEquip(this);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"Exception in OnEquip: {ex.Message}");
            }
        });

        LumoraLogger.Log($"AvatarObjectSlot: Equipped {avatarObject.Node} to slot on '{Slot.SlotName.Value}'");
    }

    /// <summary>
    /// Dequip the currently equipped object.
    /// </summary>
    public void Dequip(HashSet<IAvatarObject> dequippedObjects)
    {
        if (Equipped?.Target == null)
            return;

        dequippedObjects?.Add(Equipped.Target);

        // Call OnDequip on all IAvatarObjectComponent
        ForeachObjectComponent(Equipped.Target as Component, (c) =>
        {
            try
            {
                c.OnDequip(this);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"Exception in OnDequip: {ex.Message}");
            }
        });

        Equipped.Target.Dequip();
        Equipped.Target = null;
    }

    /// <summary>
    /// Call an action on all IAvatarObjectComponent in a slot hierarchy.
    /// </summary>
    public void ForeachObjectComponent(Action<IAvatarObjectComponent> action)
    {
        if (Equipped?.Target is Component comp)
        {
            ForeachObjectComponent(comp, action);
        }
    }

    /// <summary>
    /// Call an action on all IAvatarObjectComponent in a component's slot hierarchy.
    /// </summary>
    public static void ForeachObjectComponent(Component component, Action<IAvatarObjectComponent> action)
    {
        if (component?.Slot == null)
            return;

        var components = new List<IAvatarObjectComponent>();
        CollectObjectComponents(component.Slot, components);

        foreach (var c in components)
        {
            action(c);
        }
    }

    private static void CollectObjectComponents(Slot slot, List<IAvatarObjectComponent> objectComponents)
    {
        // Get all IAvatarObjectComponent from this slot
        foreach (var comp in slot.Components)
        {
            if (comp is IAvatarObjectComponent avatarComp)
            {
                objectComponents.Add(avatarComp);
            }
        }

        // Recurse into children, but stop at AvatarObjectSlot boundaries
        foreach (var child in slot.Children)
        {
            if (child.GetComponent<AvatarObjectSlot>() == null)
            {
                CollectObjectComponents(child, objectComponents);
            }
        }
    }

    /// <summary>
    /// Get the filtered pose data for this slot.
    /// Applies all pose filters to the raw tracking data.
    /// </summary>
    public Slot GetFilteredPoseData(out float3 position, out floatQ rotation, out bool isTracking)
    {
        if (_userRoot == null)
            FindUserRoot();

        var space = _userRoot?.Slot;
        if (space == null)
        {
            position = float3.Zero;
            rotation = floatQ.Identity;
            isTracking = false;
            return Slot;
        }

        // Convert this slot's world transform into UserRoot-local space.
        // Using global→local conversion handles any depth of nesting under UserRoot
        // (e.g. UserRoot > Body Nodes > Head > BodyNode) correctly.
        position = space.GlobalPointToLocal(Slot.GlobalPosition);
        rotation = space.GlobalRotation.Inverse * Slot.GlobalRotation;
        isTracking = IsTracking.Value;

        // Apply all filters
        foreach (var filter in Filters)
        {
            filter?.ProcessPose(this, space, ref position, ref rotation, ref isTracking);
        }

        return space;
    }
}

/// <summary>
/// A reference to an IAvatarObject that can be linked.
/// </summary>
public class LinkRef<T> where T : class
{
    private T _target;
    private readonly Component _owner;

    public T Target
    {
        get => _target;
        set => _target = value;
    }

    public LinkRef(Component owner)
    {
        _owner = owner;
    }
}

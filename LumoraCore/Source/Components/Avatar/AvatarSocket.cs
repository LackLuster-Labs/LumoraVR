// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// A slot that can have avatar objects equipped to it for a specific body node.
// Used by TrackedDevicePositioner to create body node tracking points.
[ComponentCategory("Users/Avatar")]
public class AvatarSocket : UserRootComponent
{
    // The currently equipped avatar object. Synced so every peer can see
    // equip state (pose nodes derive their drive links from it, and
    // FillEmptySockets must not re-fill slots the authority already filled).
    public readonly SyncRef<IAvatarEquippable> Equipped = null!;

    public readonly Sync<BodyNode> Node = new();

    public readonly Sync<bool> IsTracking = new();

    public readonly Sync<bool> IsActive = new();

    public readonly Sync<bool> DriveActive = new();

    public readonly Sync<bool> DriveScale = new();

    // The pose filters in the order they run: higher priority first, equal ones in the order they came. Kept
    // on each machine rather than synced: whatever lays a filter on (a seat, the smoothing below) does it on
    // every machine from state they all share, so every machine runs the same list without one travelling.
    // -xlinka
    private readonly List<(IPoseFilter Filter, int Priority)> _filters = new();

    public int FilterCount => _filters.Count;

    public IEnumerable<IPoseFilter> Filters
    {
        get
        {
            for (int i = 0; i < _filters.Count; i++)
                yield return _filters[i].Filter;
        }
    }

    public void AddFilter(IPoseFilter filter, int priority = 0)
    {
        if (filter == null || IndexOfFilter(filter) >= 0)
            return;
        int at = _filters.Count;
        while (at > 0 && _filters[at - 1].Priority < priority)
            at--;
        _filters.Insert(at, (filter, priority));
    }

    public void RemoveFilter(IPoseFilter filter)
    {
        int index = IndexOfFilter(filter);
        if (index >= 0)
            _filters.RemoveAt(index);
    }

    private int IndexOfFilter(IPoseFilter filter)
    {
        for (int i = 0; i < _filters.Count; i++)
        {
            if (ReferenceEquals(_filters[i].Filter, filter))
                return i;
        }
        return -1;
    }

    public bool HasEquipped => Equipped?.Target != null;

    // Internal state
    private UserRoot _userRoot = null!;

    private PoseSmoother _autoSmoothing = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        Node.OnChanged += _ =>
        {
            QueueAutoSmoothing();
            OfferToWearer();
            QueueLateFill();
        };
        Equipped.OnTargetChange += _ => QueueReclaim();
    }

    // The authority dresses a socket that reaches it empty, and the wearer can be putting their own part in at
    // the same moment. The writes cross, the default lands last, and a respawned joiner wore the grey head over
    // their avatar's. The wearer takes the socket back once the default has landed. -xlinka
    private bool _reclaimQueued;

    private void QueueReclaim()
    {
        if (_reclaimQueued || World == null || Equipped.Target is not Component held || !AvatarEquipManager.IsDefaultPiece(held))
            return;
        _reclaimQueued = true;
        World.RunSynchronously(() =>
        {
            _reclaimQueued = false;
            if (!IsDestroyed)
                OfferToWearer();
        });
    }

    // The head socket is the one a user's own positioner adds on its first input frame, and a joiner's
    // reaches the host some time after that. The body was dressed at its start, before either, so a head
    // socket arriving later went bare for good: nobody's head showed, bar the odd user whose socket beat the
    // dressing. The authority dresses a positioner's socket when it turns up empty. Other sockets are left
    // to whatever made them. -xlinka
    private bool _lateFillQueued;

    private void QueueLateFill()
    {
        if (_lateFillQueued || World?.IsAuthority != true)
            return;
        _lateFillQueued = true;
        World.RunSynchronously(() =>
        {
            _lateFillQueued = false;
            if (IsDestroyed || HasEquipped || Node.Value == BodyNode.NONE)
                return;
            if (Slot?.Parent?.GetComponent<TrackedDevicePositioner>()?.ObjectSlot.Target != this)
                return;
            if (_userRoot == null)
                FindUserRoot();
            _userRoot?.GetRegisteredComponent<AvatarEquipManager>()?.FillEmptySockets(new List<AvatarSocket> { this });
        });
    }

    // A socket that turns up once the avatar is on (a body's own head and controllers, a seat's stand-in, a
    // tracker plugged in late), or one left holding a default piece, gets the worn avatar's part for it. Only
    // the wearer's machine does it: equip state is the wearer's to write.
    internal void OfferToWearer()
    {
        if (IsDestroyed || Node.Value == BodyNode.NONE || !IsUnderLocalUser)
            return;
        _userRoot?.GetRegisteredComponent<AvatarEquipManager>()?.OfferSocket(this);
    }

    // Hips/feet jitter visibly without smoothing in any IK/tracker setup
    // (controller skew, network noise). Head and hands stay direct so input
    // doesn't gain latency. - xlinka
    private static bool ShouldAutoSmooth(BodyNode node)
        => node == BodyNode.Hips || node == BodyNode.LeftFoot || node == BodyNode.RightFoot;

    // Smoothing is each machine's own arithmetic on a pose every machine already has, so each machine keeps its
    // own on a local slot, set up on the next update rather than in the middle of whatever changed the node.
    // Nothing about it travels, and nothing the host made ends up inside a socket its wearer may later take
    // away: a host-made smoothing slot under a seat's stand-in made the stand-in impossible to remove. -xlinka
    private bool _smoothingQueued;

    private void QueueAutoSmoothing()
    {
        if (_smoothingQueued || World == null)
            return;
        _smoothingQueued = true;
        World.RunSynchronously(() =>
        {
            _smoothingQueued = false;
            if (!IsDestroyed)
                RefreshAutoSmoothing();
        });
    }

    private void RefreshAutoSmoothing()
    {
        if (Slot == null || Slot.IsDestroyed)
            return;

        if (ShouldAutoSmooth(Node.Value))
        {
            if (_autoSmoothing == null || _autoSmoothing.IsDestroyed)
            {
                var smoothSlot = Slot.AddLocalSlot("AutoSmoothing");
                smoothSlot.Persistent.Value = false;
                _autoSmoothing = smoothSlot.AttachComponent<PoseSmoother>();
                _autoSmoothing.PositionSmoothSpeed.Value = -1f;
                _autoSmoothing.RotationSmoothSpeed.Value = 20f;
                AddFilter(_autoSmoothing, int.MinValue);
            }
        }
        else if (_autoSmoothing != null)
        {
            RemoveFilter(_autoSmoothing);
            _autoSmoothing.Slot?.Destroy();
            _autoSmoothing = null!;
        }
    }

    public override void OnInit()
    {
        base.OnInit();
        // BodyNode.NONE is not enum value 0 - set it explicitly
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
        if (ShouldAutoSmooth(Node.Value))
            QueueAutoSmoothing();
        QueueLateFill();
    }

    private void FindUserRoot()
    {
        _userRoot = Slot?.ActiveUserRoot!;
    }

    public new bool IsUnderLocalUser
    {
        get
        {
            if (_userRoot == null)
                FindUserRoot();
            return _userRoot?.ActiveUser == World?.LocalUser;
        }
    }

    // Pre-equip an avatar object - dequips any existing object first.
    public bool PreEquip(IAvatarEquippable avatarObject, HashSet<IAvatarEquippable> dequippedObjects)
    {
        if (avatarObject.Node == Node.Value)
        {
            Dequip(dequippedObjects);

            // Call OnPreEquip on all IAvatarEquipReceiver in the avatar's slot
            ForeachObjectComponent((avatarObject as Component)!, (c) =>
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

    public void Equip(IAvatarEquippable avatarObject)
    {
        Equipped.Target = avatarObject;
        avatarObject.Equip(this);

        ForeachObjectComponent((avatarObject as Component)!, (c) =>
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

        LumoraLogger.Debug($"AvatarSocket: Equipped {avatarObject.Node} to slot on '{Slot.SlotName.Value}'");
    }

    public void Dequip(HashSet<IAvatarEquippable> dequippedObjects)
    {
        var equipped = Equipped?.Target;
        if (equipped == null)
            return;

        dequippedObjects?.Add(equipped);

        ForeachObjectComponent((equipped as Component)!, (c) =>
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

        if (equipped is not Component component || !component.IsDestroyed)
            equipped.Dequip();
        Equipped!.Target = null!;
    }

    public void ForeachObjectComponent(Action<IAvatarEquipReceiver> action)
    {
        if (Equipped?.Target is Component comp)
        {
            ForeachObjectComponent(comp, action);
        }
    }

    public static void ForeachObjectComponent(Component component, Action<IAvatarEquipReceiver> action)
    {
        if (component?.Slot == null)
            return;

        var components = new List<IAvatarEquipReceiver>();
        CollectObjectComponents(component.Slot, components);

        foreach (var c in components)
        {
            action(c);
        }
    }

    private static void CollectObjectComponents(Slot slot, List<IAvatarEquipReceiver> objectComponents)
    {
        foreach (var comp in slot.Components)
        {
            if (comp is IAvatarEquipReceiver avatarComp)
            {
                objectComponents.Add(avatarComp);
            }
        }

        // Recurse into children, but stop at AvatarSocket boundaries
        foreach (var child in slot.Children)
        {
            if (child.GetComponent<AvatarSocket>() == null)
            {
                CollectObjectComponents(child, objectComponents);
            }
        }
    }

    // Applies all pose filters to the raw tracking data.
    public Slot GetFilteredPose(out float3 position, out floatQ rotation, out bool isTracking)
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

        if (_autoSmoothing == null && ShouldAutoSmooth(Node.Value))
            QueueAutoSmoothing();

        // Convert this slot's world transform into UserRoot-local space.
        // Using global->local conversion handles any depth of nesting under UserRoot
        // (e.g. UserRoot > Body Nodes > Head > BodyNode) correctly.
        position   = space.GlobalPointToLocal(Slot.GlobalPosition);
        rotation   = space.GlobalRotation.Inverse * Slot.GlobalRotation;
        isTracking = IsTracking.Value;

        for (int i = 0; i < _filters.Count; i++)
        {
            var filter = _filters[i].Filter;
            if (filter is Component { IsDestroyed: true })
                continue;
            filter?.ProcessPose(this, space, ref position, ref rotation, ref isTracking);
        }

        return space;
    }
}

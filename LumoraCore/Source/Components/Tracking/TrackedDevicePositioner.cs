// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Components.Avatar;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Users")]
[DefaultUpdateOrder(-1000000)] // Runs very early - before IK and other components
public class TrackedDevicePositioner : UserRootComponent, IInputUpdateReceiver
{
    public readonly Sync<int> DeviceIndex = null!;

    public readonly Sync<BodyNode> CorrespondingBodyNode = null!;

    // overrides DeviceIndex when set
    public readonly Sync<BodyNode?> AutoBodyNode = null!;

    public readonly Sync<bool> AlwaysRenderModel = null!;

    public readonly SyncRef<Slot> ReferenceModel = null!;

    public readonly SyncRef<Slot> BodyNodeRoot = null!;

    public readonly SyncRef<AvatarSocket> ObjectSlot = null!;

    public readonly Sync<bool> IsTracking = null!;

    public readonly Sync<bool> IsActive = null!;

    public readonly Sync<bool> CreateAvatarObjectSlot = null!;

    // Internal state
    private UserRoot _userRoot = null!;
    private bool _isRegistered;
    private TransformStreamDriver _streamDriver = null!;
    private bool _streamDriverChecked;

    // pure read, never writes synced fields, so a property access (including remote/inspector reads) has
    // no replication side effects; DeviceIndex/CorrespondingBodyNode are written only from the update path
    public ITrackedDevice TrackedDevice
    {
        get
        {
            if (!IsUnderLocalUser)
                return null!;

            var input = Engine.Current?.InputInterface;
            if (input == null)
                return null!;

            // If AutoBodyNode is set, use that to find the device
            if (AutoBodyNode.Value.HasValue)
            {
                var device = input.GetBodyNode(AutoBodyNode.Value.Value);
                if (device != null && device.CorrespondingBodyNode == AutoBodyNode.Value.Value)
                    return device;

                return null!;
            }

            // Otherwise use DeviceIndex
            if (DeviceIndex.Value >= 0 && DeviceIndex.Value < input.InputDeviceCount)
            {
                return (input.GetDevice(DeviceIndex.Value) as ITrackedDevice) ?? null!;
            }

            return null!;
        }
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

    public UserRoot LocalUserRoot
    {
        get
        {
            if (_userRoot == null)
                FindUserRoot();
            return _userRoot!;
        }
    }

    public override void OnAwake()
    {
        base.OnAwake();
        FindUserRoot();
    }

    public override void OnInit()
    {
        base.OnInit();
        DeviceIndex.Value = -1;
        CorrespondingBodyNode.Value = BodyNode.NONE;
        CreateAvatarObjectSlot.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();

        FindUserRoot();
        TryRegisterWithInput();
    }

    // handles late SyncRef resolution
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // If not registered yet, keep trying (SyncRef may resolve later on client)
        if (!_isRegistered)
        {
            FindUserRoot();
            TryRegisterWithInput();
        }

        // The body's own socket goes up as soon as the body is this machine's, focused or not. Input frames skip a
        // world in the background, and a joined world settles there first: an avatar put on meanwhile found no
        // head socket, so its IK never ran and the host dressed the socket in the default head once it came.
        // -xlinka
        if (BodyNodeRoot.Target == null && CreateAvatarObjectSlot.Value && AutoBodyNode.Value.HasValue && IsUnderLocalUser
            && World.IsLocalAllocationReady)
        {
            using (World.EnterLocalUserAllocation())
                UpdateObjectSlot();
        }
    }

    private void TryRegisterWithInput()
    {
        if (_isRegistered)
            return;

        if (!IsUnderLocalUser)
            return;

        var input = Engine.Current?.InputInterface;
        if (input != null)
        {
            input.RegisterInputEventReceiver(this);
            _isRegistered = true;
            LumoraLogger.Debug($"TrackedDevicePositioner: Registered for input on '{Slot.SlotName.Value}' (node: {AutoBodyNode.Value})");
        }
    }

    public override void OnDestroy()
    {
        // Unregister from InputInterface
        if (_isRegistered)
        {
            var input = Engine.Current?.InputInterface;
            input?.UnregisterInputEventReceiver(this);
            _isRegistered = false;
        }

        base.OnDestroy();
    }

    private void FindUserRoot()
    {
        _userRoot = Slot?.ActiveUserRoot!;
    }

    private void RemoveBodyNode()
    {
        var socket = ObjectSlot.Target;
        var freed = socket?.Equipped.Target?.Node ?? BodyNode.NONE;
        if (socket != null && socket.HasEquipped)
        {
            socket.Dequip(new System.Collections.Generic.HashSet<Avatar.IAvatarEquippable>());
        }
        BodyNodeRoot.Target?.Destroy();
        if (BodyNodeRoot.Target != null)
            BodyNodeRoot.Target = null!;
        if (ObjectSlot.Target != null)
            ObjectSlot.Target = null!;
        if (freed != BodyNode.NONE)
            OfferFreedDriver(freed, socket!);
    }

    private void UpdateObjectSlot()
    {
        if (BodyNodeRoot.Target == null)
        {
            BodyNodeRoot.Target = Slot.AddSlot("BodyNode");

            // Attach AvatarSocket
            var objectSlot = BodyNodeRoot.Target.AttachComponent<AvatarSocket>();
            ObjectSlot.Target = objectSlot;

            // Drive IsTracking and IsActive from this component
        }

        if (AutoBodyNode.Value.HasValue && ObjectSlot.Target != null)
        {
            ObjectSlot.Target.Node.Value = AutoBodyNode.Value.Value;
        }
    }

    private void UpdateBodyNode()
    {
        var device = TrackedDevice;
        if (CreateAvatarObjectSlot.Value)
        {
            UpdateObjectSlot();
        }

        if (device != null && BodyNodeRoot.Target != null)
        {
            // The parent Slot is the tracked/default body transform. BodyNodeRoot is only the
            // equipment/body-node offset child. When tracking is absent the offset must be
            // identity; otherwise a stale desktop write can stack another head height under
            // Head and pull the avatar head/nameplate away from the camera. A body tracker keeps
            // its calibrated offset instead, so an occluded puck does not flap synced writes.
            if (device.IsTracking)
            {
                SetIfChanged(BodyNodeRoot.Target.LocalPosition, device.BodyNodePositionOffset);
                SetIfChanged(BodyNodeRoot.Target.LocalRotation, device.BodyNodeRotationOffset);
            }
            else if (device is not ITracker)
            {
                SetIfChanged(BodyNodeRoot.Target.LocalPosition, float3.Zero);
                SetIfChanged(BodyNodeRoot.Target.LocalRotation, floatQ.Identity);
            }

            if (ObjectSlot.Target != null)
            {
                var socket = ObjectSlot.Target;
                var node = device.CorrespondingBodyNode;
                if (socket.Node.Value != node)
                {
                    // A full socket turns the re-offer away and the old part's pose driver stays on this
                    // device, so it is emptied first. The driver it gave up goes to an empty socket already
                    // waiting for that part: when two pucks swap joints, the first one relabelled is that
                    // socket. -xlinka
                    var freed = socket.Equipped.Target?.Node ?? BodyNode.NONE;
                    if (socket.HasEquipped)
                        socket.Dequip(new System.Collections.Generic.HashSet<Avatar.IAvatarEquippable>());
                    socket.Node.Value = node;
                    if (freed != BodyNode.NONE && freed != node)
                        OfferFreedDriver(freed, socket);
                }
                SetIfChanged(socket.IsTracking, IsTracking.Value);
                SetIfChanged(socket.IsActive, IsActive.Value);
            }
        }
    }

    // A plain write marks the field for sync even when the value is the same, and these run every input
    // frame for every tracked device.
    private static void SetIfChanged<T>(Sync<T> field, T value)
    {
        if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(field.Value, value))
            field.Value = value;
    }

    private void OfferFreedDriver(BodyNode node, AvatarSocket except)
    {
        var root = LocalUserRoot;
        if (root == null)
            return;
        var waiting = new System.Collections.Generic.List<AvatarSocket>();
        root.GetRegisteredComponents(waiting, s => s != except && !s.IsDestroyed && !s.HasEquipped && s.Node.Value == node);
        if (waiting.Count > 0)
            waiting[0].OfferToWearer();
    }

    // Convert tracked poses into this node's parent frame before publishing its transform stream.
    public void BeforeInputUpdate()
    {
        // Device tracking space belongs to the focused user. Applying it in another world
        // teleports that world's head/controllers toward the focused world's avatar.
        // Overlay rigs still need tracking for the dashboard and detached panels.
        if (World == null || World.Focus == World.WorldFocus.Background || !IsUnderLocalUser)
            return;

        var device = TrackedDevice;

        bool tracking = false;
        bool isActive = false;
        float3 pos = float3.Zero;
        floatQ rot = floatQ.Identity;
        BodyNode node = AutoBodyNode.Value ?? BodyNode.NONE;

        if (device != null)
        {
            tracking = device.IsTracking;
            isActive = device.IsDeviceActive;

            if (tracking)
            {
                pos = device.RawPosition;
                rot = device.RawRotation;
                // Body-node slots and extra trackers can have different parents. Resolve both
                // through the same avatar tracking space before writing their local transforms.
                if (device.TrackingSpace != null && Slot.Parent is { } parent)
                {
                    pos = parent.GlobalPointToLocal(device.Position);
                    rot = parent.GlobalRotationToLocal(device.Rotation);
                }
            }
            else
            {
                // Keep authored/default pose when not tracking (important for desktop height)
                pos = Slot.LocalPosition.Value;
                rot = Slot.LocalRotation.Value;

                // If head/body defaults were zeroed, restore sane desktop height for head
                if (node == BodyNode.Head && pos.LengthSquared < 0.0001f)
                {
                    var input = Engine.Current?.InputInterface;
                    float height = input?.UserHeight ?? InputInterface.DEFAULT_USER_HEIGHT;
                    pos = new float3(0f, height, 0f);
                }
            }

            node = device.CorrespondingBodyNode;
            SetIfChanged(CorrespondingBodyNode, node);

            // Mirror the resolved device index into the synced field here, in the update path,
            // so the TrackedDevice getter stays read-only. Only meaningful when AutoBodyNode
            // drove the lookup; otherwise DeviceIndex is the authored input.
            if (AutoBodyNode.Value.HasValue)
                SetIfChanged(DeviceIndex, device is InputDevice inputDev ? inputDev.DeviceIndex : -1);
        }
        else
        {
            // Keep current position when no device (don't reset to zero)
            pos = Slot.LocalPosition.Value;
            rot = Slot.LocalRotation.Value;
        }

        // Clamp and filter invalid values
        pos = ClampPosition(pos);
        rot = FilterRotation(rot);

        // Skip only a truly still frame. The rotation gate was 0.0001 on 1-|dot|, which is about 1.6 degrees:
        // a hand turning slowly left its controller (and the laser on it) frozen until the turn piled up past
        // that, then jumped the whole 1.6 degrees at once, 3 cm at a metre. It is measured on the components
        // now, because 1-|dot| in float cannot see a turn much under 0.05 degrees at all and stepped a slow
        // hand whatever the threshold. The poses travel on their transform streams (or stay in the local
        // userspace world), so writing every frame costs nothing on the wire. -xlinka
        const float POS_THRESHOLD_SQ = 0.0001f * 0.0001f; // 0.1mm squared
        const float ROT_THRESHOLD_SQ = 2e-9f; // about 0.005 degrees

        var currentPos = Slot.LocalPosition.Value;
        var currentRot = Slot.LocalRotation.Value;

        float posDeltaSq = (pos - currentPos).LengthSquared;
        float side = floatQ.Dot(rot, currentRot) < 0f ? -1f : 1f;
        float qx = rot.x - side * currentRot.x;
        float qy = rot.y - side * currentRot.y;
        float qz = rot.z - side * currentRot.z;
        float qw = rot.w - side * currentRot.w;
        float rotDeltaSq = qx * qx + qy * qy + qz * qz + qw * qw;

        // When a TransformStreamDriver shares this slot, the stream is the
        // transport - writing the fields with sync generation would send every
        // pose twice. Silent writes keep change events (Godot hook, stream
        // sampling) while skipping the duplicate field sync.
        if (!_streamDriverChecked)
        {
            _streamDriver = Slot.GetComponent<TransformStreamDriver>()!;
            _streamDriverChecked = true;
        }
        bool streamed = _streamDriver != null && !_streamDriver.IsDestroyed;

        if (posDeltaSq > POS_THRESHOLD_SQ)
        {
            if (streamed)
                Slot.LocalPosition.SetValueSilently(pos, change: true);
            else
                Slot.LocalPosition.Value = pos;
        }
        if (rotDeltaSq > ROT_THRESHOLD_SQ)
        {
            if (streamed)
                Slot.LocalRotation.SetValueSilently(rot, change: true);
            else
                Slot.LocalRotation.Value = rot;
        }

        SetIfChanged(IsTracking, tracking);
        SetIfChanged(IsActive, isActive);

        if (node != BodyNode.NONE)
        {
            UpdateBodyNode();
        }
        else
        {
            RemoveBodyNode();
        }

        if (ReferenceModel.Target != null)
        {
            ReferenceModel.Target.ActiveSelf.Value = ShouldShowReferenceModel(device!, tracking);
        }
    }

    public void AfterInputUpdate()
    {
    }

    private bool ShouldShowReferenceModel(ITrackedDevice device, bool isTracking)
    {
        if (AlwaysRenderModel.Value)
            return true;

        if (!isTracking)
            return false;

        return true;
    }

    private float3 ClampPosition(float3 pos)
    {
        const float MAX_DISTANCE = 10000f;

        if (float.IsNaN(pos.x) || float.IsInfinity(pos.x))
            pos.x = 0;
        if (float.IsNaN(pos.y) || float.IsInfinity(pos.y))
            pos.y = 0;
        if (float.IsNaN(pos.z) || float.IsInfinity(pos.z))
            pos.z = 0;

        pos.x = System.Math.Clamp(pos.x, -MAX_DISTANCE, MAX_DISTANCE);
        pos.y = System.Math.Clamp(pos.y, -MAX_DISTANCE, MAX_DISTANCE);
        pos.z = System.Math.Clamp(pos.z, -MAX_DISTANCE, MAX_DISTANCE);

        return pos;
    }

    private floatQ FilterRotation(floatQ rot)
    {
        if (float.IsNaN(rot.x) || float.IsInfinity(rot.x) ||
            float.IsNaN(rot.y) || float.IsInfinity(rot.y) ||
            float.IsNaN(rot.z) || float.IsInfinity(rot.z) ||
            float.IsNaN(rot.w) || float.IsInfinity(rot.w))
        {
            return floatQ.Identity;
        }
        return rot;
    }
}

using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Components.Avatar;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Positions a slot based on VR tracking data from InputInterface body nodes.
/// This component registers as an IInputUpdateReceiver to update slot transforms
/// BEFORE other components read them, ensuring tracking data flows correctly.
/// Creates an AvatarObjectSlot for avatar systems to equip to.
/// </summary>
[ComponentCategory("Users")]
[DefaultUpdateOrder(-1000000)] // Runs very early - before IK and other components
public class TrackedDevicePositioner : Component, IInputUpdateReceiver
{
    /// <summary>
    /// Device index in the input system.
    /// </summary>
    public Sync<int> DeviceIndex { get; private set; }

    /// <summary>
    /// The body node this positioner corresponds to.
    /// </summary>
    public Sync<BodyNode> CorrespondingBodyNode { get; private set; }

    /// <summary>
    /// Auto-assign body node from device (if set, overrides DeviceIndex).
    /// </summary>
    public Sync<BodyNode?> AutoBodyNode { get; private set; }

    /// <summary>
    /// Whether to always render the reference model.
    /// </summary>
    public Sync<bool> AlwaysRenderModel { get; private set; }

    /// <summary>
    /// Reference to the reference model slot (controller model, etc).
    /// </summary>
    public SyncRef<Slot> ReferenceModel { get; private set; }

    /// <summary>
    /// Root slot for body node positioning offset.
    /// </summary>
    public SyncRef<Slot> BodyNodeRoot { get; private set; }

    /// <summary>
    /// Reference to the AvatarObjectSlot created for this body node.
    /// </summary>
    public SyncRef<AvatarObjectSlot> ObjectSlot { get; private set; }

    /// <summary>
    /// Whether this device is currently tracking.
    /// </summary>
    public Sync<bool> IsTracking { get; private set; }

    /// <summary>
    /// Whether this device is currently active.
    /// </summary>
    public Sync<bool> IsActive { get; private set; }

    /// <summary>
    /// Whether to create an AvatarObjectSlot for equipping.
    /// </summary>
    public Sync<bool> CreateAvatarObjectSlot { get; private set; }

    // Internal state
    private UserRoot _userRoot;
    private bool _isRegistered;
    private int _debugLogCounter = 0;

    /// <summary>
    /// Get the tracked device from InputInterface.
    /// </summary>
    public ITrackedDevice TrackedDevice
    {
        get
        {
            if (!IsUnderLocalUser)
                return null;

            var input = Engine.Current?.InputInterface;
            if (input == null)
                return null;

            // If AutoBodyNode is set, use that to find the device
            if (AutoBodyNode.Value.HasValue)
            {
                var device = input.GetBodyNode(AutoBodyNode.Value.Value);
                if (device != null && device.CorrespondingBodyNode == AutoBodyNode.Value.Value)
                {
                    DeviceIndex.Value = device is InputDevice inputDev ? inputDev.DeviceIndex : -1;
                    CorrespondingBodyNode.Value = AutoBodyNode.Value.Value;
                    return device;
                }
                return null;
            }

            // Otherwise use DeviceIndex
            if (DeviceIndex.Value >= 0 && DeviceIndex.Value < input.InputDeviceCount)
            {
                return input.GetDevice(DeviceIndex.Value) as ITrackedDevice;
            }

            return null;
        }
    }

    /// <summary>
    /// Check if this component is under the local user.
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
    /// Get the local UserRoot.
    /// </summary>
    public UserRoot LocalUserRoot
    {
        get
        {
            if (_userRoot == null)
                FindUserRoot();
            return _userRoot;
        }
    }

    public override void OnAwake()
    {
        base.OnAwake();

        DeviceIndex = new Sync<int>(this, -1);
        CorrespondingBodyNode = new Sync<BodyNode>(this, BodyNode.NONE);
        AutoBodyNode = new Sync<BodyNode?>(this, null);
        AlwaysRenderModel = new Sync<bool>(this, false);
        ReferenceModel = new SyncRef<Slot>(this, null);
        BodyNodeRoot = new SyncRef<Slot>(this, null);
        ObjectSlot = new SyncRef<AvatarObjectSlot>(this, null);
        IsTracking = new Sync<bool>(this, false);
        IsActive = new Sync<bool>(this, false);
        CreateAvatarObjectSlot = new Sync<bool>(this, true);

        FindUserRoot();
    }

    public override void OnStart()
    {
        base.OnStart();

        FindUserRoot();
        TryRegisterWithInput();
    }

    /// <summary>
    /// Called every frame. Check if we need to register (handles late SyncRef resolution).
    /// </summary>
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // If not registered yet, keep trying (SyncRef may resolve later on client)
        if (!_isRegistered)
        {
            FindUserRoot();
            TryRegisterWithInput();
        }
    }

    /// <summary>
    /// Try to register with InputInterface if we're under the local user.
    /// </summary>
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
            AquaLogger.Log($"TrackedDevicePositioner: Registered for input on '{Slot.SlotName.Value}' (node: {AutoBodyNode.Value})");
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
        _userRoot = Slot.GetComponent<UserRoot>();
        var current = Slot.Parent;
        while (_userRoot == null && current != null)
        {
            _userRoot = current.GetComponent<UserRoot>();
            current = current.Parent;
        }
    }

    /// <summary>
    /// Remove the body node slot and dequip any equipped object.
    /// </summary>
    private void RemoveBodyNode()
    {
        if (ObjectSlot.Target != null && ObjectSlot.Target.HasEquipped)
        {
            ObjectSlot.Target.Dequip(new System.Collections.Generic.HashSet<Avatar.IAvatarObject>());
        }
        BodyNodeRoot.Target?.Destroy();
    }

    /// <summary>
    /// Update or create the AvatarObjectSlot for this body node.
    /// </summary>
    private void UpdateObjectSlot()
    {
        if (BodyNodeRoot.Target == null)
        {
            // Create body node root slot
            BodyNodeRoot.Target = Slot.AddSlot("BodyNode");

            // Attach AvatarObjectSlot
            var objectSlot = BodyNodeRoot.Target.AttachComponent<AvatarObjectSlot>();
            ObjectSlot.Target = objectSlot;

            // Drive IsTracking and IsActive from this component
        }

        // Update node value if AutoBodyNode is set
        if (AutoBodyNode.Value.HasValue && ObjectSlot.Target != null)
        {
            ObjectSlot.Target.Node.Value = AutoBodyNode.Value.Value;
        }
    }

    /// <summary>
    /// Update body node from tracked device.
    /// </summary>
    private void UpdateBodyNode()
    {
        var device = TrackedDevice;
        if (CreateAvatarObjectSlot.Value)
        {
            UpdateObjectSlot();
        }

        if (device != null && BodyNodeRoot.Target != null)
        {
            // Only override authored pose when device is actually tracking; keeps desktop defaults intact.
            if (device.IsTracking)
            {
                BodyNodeRoot.Target.LocalPosition.Value = device.BodyNodePositionOffset;
                BodyNodeRoot.Target.LocalRotation.Value = device.BodyNodeRotationOffset;
            }

            if (ObjectSlot.Target != null)
            {
                ObjectSlot.Target.Node.Value = device.CorrespondingBodyNode;
                ObjectSlot.Target.IsTracking.Value = IsTracking.Value;
                ObjectSlot.Target.IsActive.Value = IsActive.Value;
            }
        }
    }

    /// <summary>
    /// Called before main input update. Updates slot transform from tracking data.
    /// Tracking updates happen here before any other components read the slot transforms.
    /// Sets LocalPosition directly from device.Position.
    /// </summary>
    public void BeforeInputUpdate()
    {
        var device = TrackedDevice;

        bool tracking = false;
        bool isActive = false;
        float3 pos = float3.Zero;
        floatQ rot = floatQ.Identity;
        BodyNode node = AutoBodyNode.Value ?? BodyNode.NONE;

        // Debug logging
        _debugLogCounter++;
        if (_debugLogCounter >= 60)
        {
            _debugLogCounter = 0;
            var nodeStr = AutoBodyNode.Value?.ToString() ?? "null";
            // AquaLogger.Log($"[TDP] {Slot.SlotName.Value} node:{nodeStr} device:{device != null} tracking:{device?.IsTracking} pos:{device?.RawPosition}");
        }

        if (device != null)
        {
            tracking = device.IsTracking;
            isActive = device.IsDeviceActive;

            if (tracking)
            {
                // Use tracked pose when available
                pos = device.RawPosition;
                rot = device.RawRotation;
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
            CorrespondingBodyNode.Value = node;
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

        // Update slot transform only if change is significant (avoid syncing VR tracking jitter)
        const float POS_THRESHOLD_SQ = 0.0001f * 0.0001f; // 0.1mm squared
        const float ROT_THRESHOLD = 0.0001f;

        var currentPos = Slot.LocalPosition.Value;
        var currentRot = Slot.LocalRotation.Value;

        float posDeltaSq = (pos - currentPos).LengthSquared;
        float dot = floatQ.Dot(rot, currentRot);
        float rotDelta = 1.0f - (dot < 0 ? -dot : dot);

        if (posDeltaSq > POS_THRESHOLD_SQ)
        {
            Slot.LocalPosition.Value = pos;
        }
        if (rotDelta > ROT_THRESHOLD)
        {
            Slot.LocalRotation.Value = rot;
        }

        // Update tracking state
        IsTracking.Value = tracking;
        IsActive.Value = isActive;

        // Update body node and object slot if we have a valid node
        if (node != BodyNode.NONE)
        {
            UpdateBodyNode();
        }
        else
        {
            RemoveBodyNode();
        }

        // Update reference model visibility
        if (ReferenceModel.Target != null)
        {
            ReferenceModel.Target.ActiveSelf.Value = ShouldShowReferenceModel(device, tracking);
        }
    }

    /// <summary>
    /// Called after main input update. Used for cleanup/post-processing.
    /// </summary>
    public void AfterInputUpdate()
    {
        // Nothing to do here for now
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

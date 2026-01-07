using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Input;

/// <summary>
/// Central manager for all input devices and drivers.
/// Manages keyboard, mouse, VR controllers, trackers, and body node tracking.
/// </summary>
public class InputInterface : IDisposable
{
    private class UpdateBucket
    {
        public readonly List<IInputDriver> InputDrivers = new List<IInputDriver>();
        public readonly int Order;

        public UpdateBucket(int order)
        {
            Order = order;
        }
    }

    public const float DEFAULT_USER_HEIGHT = 1.75f;
    public const float EYE_HEAD_OFFSET = 0.125f;

    private Lumora.Core.Engine _engine;
    private List<IInputDriver> _inputDrivers = new List<IInputDriver>();
    private List<UpdateBucket> _inputDriverUpdateBuckets = new List<UpdateBucket>();
    private List<IInputDevice> _inputDevices = new List<IInputDevice>();
    private bool _initialized = false;

    // Body node tracking for avatar and input mapping
    private ITrackedDevice[] _bodyNodes;

    // Input event receivers (for TrackedDevicePositioner etc.)
    private List<IInputUpdateReceiver> _inputReceivers = new List<IInputUpdateReceiver>();

    // Tracking space for VR coordinate transformation
    public TrackingSpace GlobalTrackingSpace { get; private set; } = new TrackingSpace();

    // Standard devices
    public Mouse Mouse { get; private set; }
    public Keyboard Keyboard { get; private set; }

    // VR devices (legacy compatibility)
    public VRController LeftController { get; private set; }
    public VRController RightController { get; private set; }
    public HeadDevice HeadDevice { get; private set; }

    // VR state - synced from VR drivers automatically
    public bool VR_Active => IsVRActive;
    public float UserHeight { get; set; } = DEFAULT_USER_HEIGHT;

    // Global tracking offset
    public float3 GlobalTrackingOffset { get; set; } = float3.Zero;
    public float3 CustomTrackingOffset { get; set; } = float3.Zero;

    // Driver interfaces
    private IKeyboardDriver _keyboardDriver;
    private IMouseDriver _mouseDriver;
    private List<IVRDriver> _vrDrivers = new List<IVRDriver>();
    private HashSet<string> _loggedBodyNodeAssignments = new HashSet<string>();

    public int InputDeviceCount => _inputDevices.Count;

    /// <summary>
    /// Check if any VR driver is active.
    /// </summary>
    public bool IsVRActive => _vrDrivers.Any(d => d.IsVRActive);

    /// <summary>
    /// Get the current head output device type based on VR status.
    /// </summary>
    public HeadOutputDevice CurrentHeadOutputDevice
    {
        get
        {
            if (_vrDrivers.Any(d => d.IsVRActive))
                return HeadOutputDevice.VR;
            return HeadOutputDevice.Screen;  // Desktop mode
        }
    }

    public InputInterface()
    {
    }

    /// <summary>
    /// Initialize the input interface asynchronously.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        _engine = Engine.Current;

        // Initialize body nodes array for tracked device mapping
        InitializeBodyNodes();

        // Create standard input devices
        Keyboard = new Keyboard();
        RegisterInputDevice(Keyboard, "Keyboard");

        // Create VR devices placeholders
        LeftController = new VRController(VRControllerSide.Left);
        RightController = new VRController(VRControllerSide.Right);
        HeadDevice = new HeadDevice();

        _initialized = true;

        await Task.CompletedTask;
        Logger.Log("InputInterface: Initialized successfully");
    }

    /// <summary>
    /// Initialize the body nodes array with default TrackedObjects.
    /// </summary>
    private void InitializeBodyNodes()
    {
        _bodyNodes = new ITrackedDevice[(int)BodyNode.END];

        // Create default TrackedObject for each body node
        foreach (BodyNode bodyNode in Enum.GetValues(typeof(BodyNode)))
        {
            if (bodyNode == BodyNode.NONE || bodyNode == BodyNode.END)
                continue;

            int index = (int)bodyNode;
            if (index >= 0 && index < _bodyNodes.Length)
            {
                var trackedObject = new TrackedObject();
                trackedObject.Initialize(this, index, bodyNode.ToString());
                trackedObject.CorrespondingBodyNode = bodyNode;
                trackedObject.IsDeviceActive = false;
                _bodyNodes[index] = trackedObject;
            }
        }
    }

    #region Body Node Access

    /// <summary>
    /// Get the tracked device for a specific body node.
    /// </summary>
    public ITrackedDevice GetBodyNode(BodyNode node)
    {
        int index = (int)node;
        if (index < 0 || index >= _bodyNodes.Length)
            return null;
        return _bodyNodes[index];
    }

    /// <summary>
    /// Update body node assignment from tracked devices.
    /// Called during input update to assign devices to body nodes based on priority.
    /// </summary>
    private void UpdateBodyNodeAssignments()
    {
        // Reset to default tracked objects first
        foreach (BodyNode bodyNode in Enum.GetValues(typeof(BodyNode)))
        {
            if (bodyNode == BodyNode.NONE || bodyNode == BodyNode.END)
                continue;

            int index = (int)bodyNode;
            if (index >= 0 && index < _bodyNodes.Length)
            {
                // Check if current device is still the best choice
                var current = _bodyNodes[index];
                if (current != null && current is TrackedObject defaultObj)
                {
                    defaultObj.IsDeviceActive = false;
                    defaultObj.IsTracking = false;
                }
            }
        }

        // Assign tracked devices to body nodes based on priority
        foreach (var device in _inputDevices)
        {
            if (device is ITrackedDevice trackedDevice &&
                trackedDevice.CorrespondingBodyNode != BodyNode.NONE &&
                trackedDevice.IsDeviceActive &&
                trackedDevice.IsTracking)
            {
                int index = (int)trackedDevice.CorrespondingBodyNode;
                if (index >= 0 && index < _bodyNodes.Length)
                {
                    var existing = _bodyNodes[index];
                    if (existing == null ||
                        !existing.IsDeviceActive ||
                        !existing.IsTracking ||
                        existing.Priority < trackedDevice.Priority)
                    {
                        _bodyNodes[index] = trackedDevice;
                        var logKey = $"{device.Name}_{trackedDevice.CorrespondingBodyNode}";
                        if (!_loggedBodyNodeAssignments.Contains(logKey))
                        {
                            _loggedBodyNodeAssignments.Add(logKey);
                            Logger.Log($"InputInterface: Assigned device '{device.Name}' to body node {trackedDevice.CorrespondingBodyNode}");
                        }
                    }
                }
            }
        }

        // Also update from VR devices (legacy compatibility)
        UpdateBodyNodeFromVRDevice(HeadDevice, BodyNode.Head);
        UpdateBodyNodeFromVRDevice(LeftController, BodyNode.LeftController);
        UpdateBodyNodeFromVRDevice(RightController, BodyNode.RightController);
    }

    private void UpdateBodyNodeFromVRDevice(IInputDevice device, BodyNode node)
    {
        if (device == null || !device.IsDeviceActive)
            return;

        int index = (int)node;
        if (index < 0 || index >= _bodyNodes.Length)
            return;

        // Create or update TrackedObject for this body node from VR device
        if (_bodyNodes[index] is TrackedObject trackedObj)
        {
            if (device is HeadDevice head)
            {
                trackedObj.RawPosition = new float3(head.Position.X, head.Position.Y, head.Position.Z);
                trackedObj.RawRotation = new floatQ(head.Rotation.X, head.Rotation.Y, head.Rotation.Z, head.Rotation.W);
                trackedObj.IsTracking = head.IsTracked;
                trackedObj.IsDeviceActive = true;
                trackedObj.TrackingSpace = GlobalTrackingSpace;
            }
            else if (device is VRController controller)
            {
                trackedObj.RawPosition = new float3(controller.Position.X, controller.Position.Y, controller.Position.Z);
                trackedObj.RawRotation = new floatQ(controller.Rotation.X, controller.Rotation.Y, controller.Rotation.Z, controller.Rotation.W);
                trackedObj.IsTracking = controller.IsTracked;
                trackedObj.IsDeviceActive = true;
                trackedObj.TrackingSpace = GlobalTrackingSpace;

                // Also update hand body nodes
                var handNode = node == BodyNode.LeftController ? BodyNode.LeftHand : BodyNode.RightHand;
                int handIndex = (int)handNode;
                if (handIndex >= 0 && handIndex < _bodyNodes.Length && _bodyNodes[handIndex] is TrackedObject handObj)
                {
                    handObj.RawPosition = trackedObj.RawPosition;
                    handObj.RawRotation = trackedObj.RawRotation;
                    handObj.IsTracking = trackedObj.IsTracking;
                    handObj.IsDeviceActive = true;
                    handObj.TrackingSpace = GlobalTrackingSpace;
                }
            }
        }
    }

    #endregion

    #region Input Update Receivers

    /// <summary>
    /// Register an input event receiver for before/after input updates.
    /// </summary>
    public void RegisterInputEventReceiver(IInputUpdateReceiver receiver)
    {
        if (!_inputReceivers.Contains(receiver))
        {
            _inputReceivers.Add(receiver);
        }
    }

    /// <summary>
    /// Unregister an input event receiver.
    /// </summary>
    public void UnregisterInputEventReceiver(IInputUpdateReceiver receiver)
    {
        _inputReceivers.Remove(receiver);
    }

    #endregion

    #region Driver Registration

    /// <summary>
    /// Register a generic input driver (for VR controllers, gamepads, etc.)
    /// </summary>
    public void RegisterInputDriver(IInputDriver driver)
    {
        _inputDrivers.Add(driver);
        driver.RegisterInputs(this);

        // Find or create UpdateBucket for this driver's order
        UpdateBucket bucket = _inputDriverUpdateBuckets.FirstOrDefault(b => b.Order == driver.UpdateOrder);
        if (bucket == null)
        {
            bucket = new UpdateBucket(driver.UpdateOrder);
            _inputDriverUpdateBuckets.Add(bucket);
            // Keep buckets sorted by order
            _inputDriverUpdateBuckets.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        bucket.InputDrivers.Add(driver);

        Logger.Log($"InputInterface: Registered driver with UpdateOrder {driver.UpdateOrder}");
    }

    /// <summary>
    /// Register the keyboard driver
    /// </summary>
    public void RegisterKeyboardDriver(IKeyboardDriver keyboardDriver)
    {
        if (_keyboardDriver != null)
            throw new InvalidOperationException("Keyboard Driver is already registered");

        _keyboardDriver = keyboardDriver;
        Logger.Log("InputInterface: Keyboard driver registered");
    }

    /// <summary>
    /// Register the mouse driver and create the Mouse device
    /// </summary>
    public void RegisterMouseDriver(IMouseDriver mouseDriver)
    {
        if (_mouseDriver != null)
            throw new InvalidOperationException("Mouse Driver already registered!");

        Mouse = new Mouse();
        RegisterInputDevice(Mouse, "Mouse");
        _mouseDriver = mouseDriver;

        Logger.Log("InputInterface: Mouse driver registered");
    }

    /// <summary>
    /// Register an input device (mouse, controller, etc.)
    /// </summary>
    public void RegisterInputDevice(IInputDevice device, string name)
    {
        int deviceIndex = _inputDevices.Count;
        _inputDevices.Add(device);
        device.Initialize(this, deviceIndex, name);

        Logger.Log($"InputInterface: Registered device '{name}' (index {deviceIndex})");
    }

    /// <summary>
    /// Create and register a new device of the specified type.
    /// </summary>
    public T CreateDevice<T>(string name) where T : IInputDevice, new()
    {
        var device = new T();
        RegisterInputDevice(device, name);
        return device;
    }

    /// <summary>
    /// Register a VR driver.
    /// </summary>
    public void RegisterVRDriver(IVRDriver vrDriver)
    {
        if (!_vrDrivers.Contains(vrDriver))
        {
            _vrDrivers.Add(vrDriver);

            // Register VR devices if not already registered
            if (!_inputDevices.Contains(LeftController))
            {
                RegisterInputDevice(LeftController, "LeftController");
                RegisterInputDevice(RightController, "RightController");
                RegisterInputDevice(HeadDevice, "HeadDevice");
            }

            // If the VR driver also implements IInputDriver, register it to create TrackedObjects
            if (vrDriver is IInputDriver inputDriver)
            {
                RegisterInputDriver(inputDriver);
                Logger.Log($"InputInterface: VR driver '{vrDriver.VRSystemName}' registered as IInputDriver");
            }
            else
            {
                Logger.Log($"InputInterface: VR driver '{vrDriver.VRSystemName}' registered (legacy only)");
            }
        }
    }

    #endregion

    #region Update Loop

    /// <summary>
    /// Process all input for the current frame.
    /// </summary>
    public void ProcessInput(double deltaTime)
    {
        if (!_initialized)
            return;

        UpdateInputs((float)deltaTime);
    }

    /// <summary>
    /// Update all input drivers and devices.
    /// Calls before/after input receiver callbacks.
    /// </summary>
    public void UpdateInputs(float deltaTime)
    {
        // Update drivers in order by their UpdateOrder buckets
        foreach (var bucket in _inputDriverUpdateBuckets)
        {
            foreach (var driver in bucket.InputDrivers)
            {
                driver.UpdateInputs(deltaTime);
            }
        }

        // Update mouse through driver
        if (_mouseDriver != null && Mouse != null)
        {
            _mouseDriver.UpdateMouse(Mouse);
        }

        // Update keyboard through driver
        if (_keyboardDriver != null && Keyboard != null)
        {
            _keyboardDriver.UpdateKeyboard(Keyboard);
        }

        // Update VR devices through drivers
        foreach (var vrDriver in _vrDrivers)
        {
            vrDriver.UpdateVRDevices(LeftController, RightController, HeadDevice);
        }

        // Update body node assignments from all tracked devices
        UpdateBodyNodeAssignments();

        // Call BeforeInputUpdate on all receivers (TrackedDevicePositioner updates slots here)
        foreach (var receiver in _inputReceivers)
        {
            try
            {
                receiver.BeforeInputUpdate();
            }
            catch (Exception ex)
            {
                Logger.Error($"InputInterface: Error in BeforeInputUpdate: {ex.Message}");
            }
        }

        // Call AfterInputUpdate on all receivers
        foreach (var receiver in _inputReceivers)
        {
            try
            {
                receiver.AfterInputUpdate();
            }
            catch (Exception ex)
            {
                Logger.Error($"InputInterface: Error in AfterInputUpdate: {ex.Message}");
            }
        }
    }

    #endregion

    #region Device Access

    public IInputDevice GetDevice(int index)
    {
        if (index < 0 || index >= _inputDevices.Count)
            return null;
        return _inputDevices[index];
    }

    public T GetDevice<T>(string name) where T : class, IInputDevice
    {
        return _inputDevices.FirstOrDefault(d => d.Name == name) as T;
    }

    public T GetDevice<T>(Predicate<T> predicate = null) where T : class, IInputDevice
    {
        foreach (var device in _inputDevices)
        {
            if (device is T typedDevice && (predicate == null || predicate(typedDevice)))
            {
                return typedDevice;
            }
        }
        return null;
    }

    public void GetDevices<T>(List<T> list, Predicate<T> predicate = null) where T : class, IInputDevice
    {
        foreach (var device in _inputDevices)
        {
            if (device is T typedDevice && (predicate == null || predicate(typedDevice)))
            {
                list.Add(typedDevice);
            }
        }
    }

    public IKeyboardDriver GetKeyboardDriver() => _keyboardDriver;
    public IMouseDriver GetMouseDriver() => _mouseDriver;

    #endregion

    #region Disposal

    /// <summary>
    /// Dispose of the input interface and clean up resources.
    /// </summary>
    public void Dispose()
    {
        if (!_initialized)
            return;

        // Clear input receivers
        _inputReceivers.Clear();

        // Clear all drivers
        _inputDrivers.Clear();
        _inputDriverUpdateBuckets.Clear();
        _vrDrivers.Clear();

        // Clear devices
        _inputDevices.Clear();

        // Clear body nodes
        _bodyNodes = null;

        // Clear references
        _keyboardDriver = null;
        _mouseDriver = null;
        Mouse = null;
        Keyboard = null;
        LeftController = null;
        RightController = null;
        HeadDevice = null;

        _initialized = false;
        Logger.Log("InputInterface: Disposed");
    }

    #endregion
}

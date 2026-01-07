using Godot;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Logging;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for Slot → Godot Node3D.
/// Platform slot hook for Godot.
///
/// Uses lazy creation pattern:
/// - Node3D is only created when RequestNode3D() is first called
/// - Reference counting tracks how many components need the Node3D
/// - When count reaches 0 and shouldDestroy is set, Node3D is freed
/// </summary>
public class SlotHook : Hook<Slot>, ISlotHook
{
    private Slot _lastParent;
    private int _node3DRequests;
    private bool _shouldDestroy;
    private SlotHook _parentHook;
    private WorldHook _worldHook;
    private bool _didDeferLog;

    /// <summary>
    /// Whether hierarchy updates should be deferred.
    /// Defers when:
    /// - World not running yet (client still connecting)
    /// - Currently in batch decode (sync fields not populated yet)
    /// </summary>
    private bool ShouldDeferHierarchy
    {
        get
        {
            if (Owner?.World == null)
                return false;

            // Defer if we're still decoding a batch - sync fields (Name, ParentSlotRef) may not be populated yet
            var refController = Owner.World.ReferenceController;
            if (refController?.IsDecodingBatch == true)
                return true;

            // Defer if world not running yet (client still connecting)
            if (!Owner.World.IsAuthority && Owner.World.State != World.WorldState.Running)
                return true;

            return false;
        }
    }

    /// <summary>
    /// The generated Node3D for this slot (created on-demand).
    /// </summary>
    public Node3D GeneratedNode3D { get; private set; }

    /// <summary>
    /// Get the world hook for this slot.
    /// </summary>
    public WorldHook WorldHook => _worldHook ??= (WorldHook)Owner.World.Hook;

    /// <summary>
    /// Factory method for creating slot hooks.
    /// </summary>
    public static IHook<Slot> Constructor()
    {
        return new SlotHook();
    }

    /// <summary>
    /// Force get the Node3D (create if needed).
    /// </summary>
    public Node3D ForceGetNode3D()
    {
        if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
        {
            GenerateNode3D();
        }
        return GeneratedNode3D;
    }

    /// <summary>
    /// Request the Node3D for this slot.
    /// Increments reference count.
    /// </summary>
    public Node3D RequestNode3D()
    {
        _node3DRequests++;
        return ForceGetNode3D();
    }

    /// <summary>
    /// Free the Node3D request.
    /// Decrements reference count.
    /// </summary>
    public void FreeNode3D()
    {
        _node3DRequests--;
        TryDestroy();
    }

    /// <summary>
    /// Try to destroy the Node3D if no longer needed.
    /// </summary>
    private void TryDestroy(bool destroyingWorld = false)
    {
        if (!_shouldDestroy || _node3DRequests > 0)
        {
            return;
        }

        if (!destroyingWorld)
        {
            if (GeneratedNode3D != null && GodotObject.IsInstanceValid(GeneratedNode3D))
            {
                GeneratedNode3D.QueueFree();
            }

            // Free parent request if we had one
            _parentHook?.FreeNode3D();
        }

        GeneratedNode3D = null;
        _lastParent = null;
        _parentHook = null;
    }

    /// <summary>
    /// Generate the Node3D for this slot.
    /// </summary>
    private void GenerateNode3D()
    {

        GeneratedNode3D = new Node3D();
        GeneratedNode3D.Name = Owner.SlotName.Value;

        UpdateParent();
        SetData();
    }

    /// <summary>
    /// Update the parent hierarchy.
    /// </summary>
    private void UpdateParent()
    {
        if (ShouldDeferHierarchy)
        {
            return;
        }

        // Defer if parent is unknown (ParentSlotRef not decoded yet) or pending (waiting for async resolution)
        // This prevents orphaned slots from being incorrectly attached to world root
        // during network decode when sync members haven't been decoded yet
        if (Owner.IsParentUnknown)
        {
            Lumora.Core.Logging.Logger.Log($"SlotHook: Deferring hierarchy for '{Owner.SlotName.Value}' - parent ref not decoded yet");
            return;
        }
        if (Owner.HasPendingParent)
        {
            Lumora.Core.Logging.Logger.Log($"SlotHook: Deferring hierarchy for '{Owner.SlotName.Value}' - parent pending resolution");
            return;
        }

        if (_lastParent == Owner.Parent && !Owner.IsRootSlot)
        {
            return;
        }

        _lastParent = Owner.Parent;

        // Free old parent hook request
        if (_parentHook != null)
        {
            _parentHook.FreeNode3D();
            _parentHook = null;
        }

        // Set new parent
        if (_lastParent != null && !Owner.IsRootSlot)
        {
            _parentHook = (SlotHook)_lastParent.Hook;
            if (_parentHook != null)
            {
                Node3D parentNode = _parentHook.RequestNode3D();
                if (GeneratedNode3D.GetParent() != parentNode)
                {
                    if (GeneratedNode3D.GetParent() != null)
                    {
                        GeneratedNode3D.Reparent(parentNode, false);
                    }
                    else
                    {
                        parentNode.AddChild(GeneratedNode3D);
                    }
                }
                Lumora.Core.Logging.Logger.Log($"SlotHook: Attached child slot '{Owner.SlotName.Value}' to parent '{_lastParent.SlotName.Value}'");
            }
            else
            {
                Lumora.Core.Logging.Logger.Warn($"SlotHook: Parent hook is null for slot '{Owner.SlotName.Value}' (parent: '{_lastParent.SlotName.Value}')");
            }
        }
        else
        {
            // Root slot - attach to world root ONLY if this is the actual World.RootSlot
            // Don't use IsTrueRootSlot because ParentSlotRef.IsInInitPhase becomes false
            // during slot initialization, but the actual Value is decoded later as a separate
            // sync element. This causes false positives for "true root slot" detection.
            bool isActualRootSlot = Owner == Owner.World?.RootSlot;
            if (isActualRootSlot)
            {
                // Get the world's Godot scene root directly
                var worldRoot = Owner.World.GodotSceneRoot as Node3D;
                if (worldRoot != null)
                {
                    if (GeneratedNode3D.GetParent() != worldRoot)
                    {
                        if (GeneratedNode3D.GetParent() != null)
                        {
                            GeneratedNode3D.Reparent(worldRoot, false);
                        }
                        else
                        {
                            worldRoot.AddChild(GeneratedNode3D);
                        }
                    }
                    Lumora.Core.Logging.Logger.Log($"SlotHook: Attached root slot '{Owner.SlotName.Value}' to world root");
                }
                else
                {
                    Lumora.Core.Logging.Logger.Warn($"SlotHook: World root not found for root slot '{Owner.SlotName.Value}'");
                }
            }
            else
            {
                // Not World.RootSlot and no parent - check if we should fall back to RootSlot
                // This happens when:
                // 1. ParentSlotRef.Value was decoded as RefID.Null (true orphan, should parent to RootSlot)
                // 2. We're still waiting for parent decode (defer)
                var parentRefValue = Owner.ParentSlotRef?.Value ?? RefID.Null;
                bool worldIsRunning = Owner.World?.State == World.WorldState.Running;
                bool parentRefIsNull = parentRefValue.IsNull;

                // If world is running and parent ref is explicitly null, fall back to RootSlot
                if (worldIsRunning && parentRefIsNull && Owner.World?.RootSlot != null)
                {
                    // Parent to RootSlot's Node3D
                    var rootSlot = Owner.World.RootSlot;
                    var rootHook = rootSlot.Hook as SlotHook;
                    if (rootHook != null)
                    {
                        _parentHook = rootHook;
                        Node3D parentNode = _parentHook.RequestNode3D();
                        if (GeneratedNode3D.GetParent() != parentNode)
                        {
                            if (GeneratedNode3D.GetParent() != null)
                            {
                                GeneratedNode3D.Reparent(parentNode, false);
                            }
                            else
                            {
                                parentNode.AddChild(GeneratedNode3D);
                            }
                        }
                        Lumora.Core.Logging.Logger.Log($"SlotHook: Attached orphan slot '{Owner.SlotName.Value}' to RootSlot '{rootSlot.SlotName.Value}' (fallback)");
                    }
                }
                else
                {
                    // Still waiting for parent decode or world not running yet
                    Lumora.Core.Logging.Logger.Log($"SlotHook: Deferring attachment for '{Owner.SlotName.Value}' - waiting for parent decode (ParentRef.Value={parentRefValue}, WorldRunning={worldIsRunning})");
                }
            }
        }
    }

    /// <summary>
    /// Set initial transform and visibility data.
    /// </summary>
    private void SetData()
    {
        if (GeneratedNode3D == null) return;

        GeneratedNode3D.Visible = Owner.ActiveSelf.Value;
        GeneratedNode3D.Position = ToGodotVector3(Owner.LocalPosition.Value);
        GeneratedNode3D.Quaternion = ToGodotQuaternion(Owner.LocalRotation.Value);
        GeneratedNode3D.Scale = ToGodotVector3(Owner.LocalScale.Value);
    }

    /// <summary>
    /// Update transform and visibility data.
    /// </summary>
    private void UpdateData()
    {
        if (GeneratedNode3D == null) return;

        if (Owner.ActiveSelf.GetWasChangedAndClear())
        {
            GeneratedNode3D.Visible = Owner.ActiveSelf.Value;
        }

        // Check dirty flag BEFORE clearing
        bool positionDirty = Owner.LocalPosition.IsDirty;
        bool rotationDirty = Owner.LocalRotation.IsDirty;

        if (Owner.LocalPosition.GetWasChangedAndClear())
        {
            var newPos = ToGodotVector3(Owner.LocalPosition.Value);
            GeneratedNode3D.Position = newPos;
        }

        if (Owner.LocalRotation.GetWasChangedAndClear())
        {
            GeneratedNode3D.Quaternion = ToGodotQuaternion(Owner.LocalRotation.Value);
        }

        if (Owner.LocalScale.GetWasChangedAndClear())
        {
            GeneratedNode3D.Scale = ToGodotVector3(Owner.LocalScale.Value);
        }

        var slotName = Owner.SlotName.Value ?? string.Empty;
        if (Owner.SlotName.GetWasChangedAndClear())
        {
            GeneratedNode3D.Name = slotName;
        }
        else if (!string.IsNullOrEmpty(slotName) && GeneratedNode3D.Name != slotName)
        {
            GeneratedNode3D.Name = slotName;
        }
    }

    public override void Initialize()
    {
        if (ShouldDeferHierarchy)
        {
            if (!_didDeferLog)
            {
                Lumora.Core.Logging.Logger.Log($"SlotHook.Initialize: Deferring Node3D creation for '{Owner.SlotName.Value}'");
                _didDeferLog = true;
            }
            return;
        }

        GenerateNode3D();
        Lumora.Core.Logging.Logger.Log($"SlotHook.Initialize: Created Node3D for slot '{Owner.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
        {
            if (ShouldDeferHierarchy)
            {
                return;
            }

            GenerateNode3D();
        }

        // Only apply changes if Node3D exists
        if (GeneratedNode3D != null && GodotObject.IsInstanceValid(GeneratedNode3D))
        {
            // Check if parent changed
            Slot parent = Owner.Parent;
            if (parent != _lastParent)
            {
                UpdateParent();
            }

            UpdateData();
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        _shouldDestroy = true;
        TryDestroy(destroyingWorld);
    }

    private static Vector3 ToGodotVector3(float3 v)
    {
        return new Vector3(v.x, v.y, v.z);
    }

    private static Quaternion ToGodotQuaternion(floatQ q)
    {
        return new Quaternion(q.x, q.y, q.z, q.w);
    }
}

/// <summary>
/// Interface for slot hooks (marker interface).
/// </summary>
public interface ISlotHook : IHook<Slot>
{
}

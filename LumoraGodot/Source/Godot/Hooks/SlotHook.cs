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
            // Root slot - attach to world root
            if (Owner.IsRootSlot)
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
                Lumora.Core.Logging.Logger.Warn($"SlotHook: Slot '{Owner.SlotName.Value}' has no parent (_lastParent={_lastParent != null}, IsRootSlot={Owner.IsRootSlot})");
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

        if (Owner.SlotName.GetWasChangedAndClear())
        {
            GeneratedNode3D.Name = Owner.SlotName.Value;
        }
    }

    public override void Initialize()
    {
        // Create Node3D immediately for hierarchical node structure
        // This ensures ALL slots appear in the scene tree, not just requested ones
        GenerateNode3D();
        Lumora.Core.Logging.Logger.Log($"SlotHook.Initialize: Created Node3D for slot '{Owner.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
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

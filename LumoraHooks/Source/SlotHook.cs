// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Logging;

namespace Lumora.Godot.Hooks;

// A slot's Node3D is made when the slot is, except for a slot inside a canvas or a local one, which gets
// one only when RequestNode3D() is first called. Reference counting tracks how many components need the
// Node3D; when count reaches 0 and shouldDestroy is set, it's freed.
[ImplementableHook(typeof(Slot))]
public class SlotHook : Hook<Slot>, ISlotHook
{
	// Static registry to find Slot from Godot Node
	private static readonly Dictionary<Node, Slot> _nodeToSlot = new();

	private Slot _lastParent = null!;
	private int _node3DRequests;
	private bool _shouldDestroy;
	private SlotHook _parentHook = null!;
	private WorldHook _worldHook = null!;
	private bool _didDeferLog;

	// LocalViewOverride pushes its per-view transform through here instead of writing the node itself.
	// The slot stays the only writer of its own transform, so an override cannot lose a race with the
	// slot's own update and flicker for a frame. Null falls through to the slot's real value. -xlinka
	private float3? _overridePosition;
	private floatQ? _overrideRotation;
	private float3? _overrideScale;

	// What this hook last pushed to the node. A flush lands here for every change event on the slot, and
	// the silent write paths (IK bind-pose reset, stream playback, dynamic bones) fire that event without
	// checking whether the value moved, so a resting avatar re-flushes every bone every frame with the
	// numbers it already has. Comparing against the last push turns those into a compare and a return
	// instead of three marshalled setters and three transform propagations through the node's subtree.
	// The name is the same story with an allocation on top: reading Node.Name marshals a StringName and
	// comparing it to a string marshals another, both finalizable, on every flush of every slot. -xlinka
	private Vector3 _appliedPosition;
	private Quaternion _appliedRotation;
	private Vector3 _appliedScale;
	private bool _appliedVisible;
	private string _appliedName = null!;
	// Pose changes were taken while nothing read this node and never written.
	private bool _poseStale;
	private bool _cutLoose;

	// What reads this node's pose: a hook that hangs something under the node and lets it follow (a skin, a light,
	// a particle system), a child node whose own pose is read, anything attached to the node by hand. A renderer
	// places its instance from the slot's world transform and a collider its body from the slot itself, so a slot
	// carrying only those, or nothing, leaves its node where it is when it moves. Twelve walking avatars moved a
	// couple of hundred nodes a frame that carried nothing but other nodes and top-level instances. -xlinka
	private int _poseReaders;
	private bool _poseReadOnParent;
	private bool _poseHeldForAttachments;

	// Drawn somewhere its own pose does not say: a view override, a body drawn between its physics steps, a body
	// placing its renderers itself. Counted over every world, so a renderer only looks up its parents while one is.
	private static int _drawnElsewhere;
	private bool _countedElsewhere;
	private bool _drivenValid;
	private float3 _drivenPosition;
	private floatQ _drivenRotation;
	private bool _drawsDirect;

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

	public Node3D GeneratedNode3D { get; private set; } = null!;

	public WorldHook WorldHook => _worldHook ??= (WorldHook)Owner.World.Hook;

	public static IHook<Slot> Constructor()
	{
		return new SlotHook();
	}

	public static Slot? GetSlotFromNode(Node? node)
	{
		var current = node;
		while (current != null)
		{
			if (_nodeToSlot.TryGetValue(current, out var slot))
				return slot;
			current = current.GetParent();
		}
		return null;
	}

	public Node3D ForceGetNode3D()
	{
		if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
		{
			GenerateNode3D();
		}
		return GeneratedNode3D!;
	}

	public Node3D RequestNode3D() => RequestNode3D(true);

	public Node3D RequestNode3D(bool readsPose)
	{
		_node3DRequests++;
		if (readsPose)
			AddPoseReader();
		return ForceGetNode3D();
	}

	public void FreeNode3D() => FreeNode3D(true);

	public void FreeNode3D(bool readsPose)
	{
		if (readsPose)
			RemovePoseReader();
		_node3DRequests--;
		TryDestroy();
	}

	// Something attached to the node by hand (a gizmo's mesh or label) follows it from then on.
	public void HoldPoseForAttachments()
	{
		if (_poseHeldForAttachments)
			return;
		_poseHeldForAttachments = true;
		AddPoseReader();
	}

	private void AddPoseReader()
	{
		if (_poseReaders++ == 0)
			PoseReadChanged();
	}

	private void RemovePoseReader()
	{
		if (--_poseReaders == 0)
			PoseReadChanged();
	}

	// Read or not passes up the chain, and the node takes its pose (or lets go of it) on its next flush.
	private void PoseReadChanged()
	{
		bool read = _poseReaders > 0;
		if (_parentHook != null && _poseReadOnParent != read)
		{
			_poseReadOnParent = read;
			if (read)
				_parentHook.AddPoseReader();
			else
				_parentHook.RemovePoseReader();
		}
		if (!_shouldDestroy && Owner is { IsDestroyed: false } && Owner.World != null)
			Owner.World.UpdateManager?.RegisterHookUpdate(Owner);
	}

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
				_nodeToSlot.Remove(GeneratedNode3D);
				GeneratedNode3D.QueueFree();
			}

			_parentHook?.FreeNode3D(_poseReadOnParent);
		}
		else if (GeneratedNode3D != null)
		{
			// Still need to unregister even when destroying world
			_nodeToSlot.Remove(GeneratedNode3D);
		}

		GeneratedNode3D = null!;
		_lastParent = null!;
		_parentHook = null!;
		_poseReadOnParent = false;
	}

	private void GenerateNode3D()
	{
		GeneratedNode3D = new Node3D();
		_appliedName = SafeNodeName(Owner.SlotName.Value);
		GeneratedNode3D.Name = _appliedName;

		_nodeToSlot[GeneratedNode3D] = Owner;

		UpdateParent();
		SetData();
	}

	// Godot forbids empty node names. During load a child slot is created
	// before its name member decodes, so fall back to a non-empty default instead of erroring.
	private static string SafeNodeName(string? name)
		=> string.IsNullOrWhiteSpace(name) ? "Slot" : name;

	private bool _hierarchyRetryQueued;

	private void RetryHierarchy()
	{
		if (_hierarchyRetryQueued || _shouldDestroy || Owner.IsDestroyed)
			return;
		_hierarchyRetryQueued = true;
		// Never enqueue directly from ApplyChanges: the slot queue drains until empty.
		Owner.World.RunInUpdates(1, () =>
		{
			_hierarchyRetryQueued = false;
			if (!_shouldDestroy && !Owner.IsDestroyed)
				Owner.World.UpdateManager?.RegisterHookUpdate(Owner);
		});
	}

	private void UpdateParent()
	{
		if (ShouldDeferHierarchy || Owner.IsParentUnknown || Owner.HasPendingParent)
		{
			RetryHierarchy();
			return;
		}

		var parent = Owner.Parent;
		if (_lastParent == parent && GeneratedNode3D.GetParent() != null && !Owner.IsRootSlot)
			return;

		SlotHook? parentHook = null;
		Node3D? parentNode;
		if (Owner == Owner.World.RootSlot)
		{
			parentNode = Owner.World.GodotSceneRoot as Node3D;
		}
		else
		{
			var parentSlot = parent;
			if (parentSlot == null && Owner.ParentSlotRef.Value.IsNull
				&& Owner.World.State == World.WorldState.Running)
				parentSlot = Owner.World.RootSlot;
			parentHook = parentSlot?.Hook as SlotHook;
			if (parentHook == null)
			{
				RetryHierarchy();
				return;
			}
			parentNode = parentHook.ForceGetNode3D();
		}

		if (parentNode == null || !GodotObject.IsInstanceValid(parentNode) || parentNode.IsQueuedForDeletion())
		{
			RetryHierarchy();
			return;
		}

		// Keep the existing attachment until the replacement is usable, and cache the parent only
		// after success. A missing parent hook used to be cached forever, until grabbing reparented it.
		bool read = _poseReaders > 0;
		if (parentHook != _parentHook)
			parentHook?.RequestNode3D(read);
		if (GeneratedNode3D.GetParent() != parentNode)
		{
			if (GeneratedNode3D.GetParent() != null)
			{
				GeneratedNode3D.Reparent(parentNode, false);
				// Coming back into the tree, every instance below hands its node's pose to the render server, and a
				// renderer's node holds only the pose that last wrote it.
				PlaceRenderersUnder(Owner, true);
			}
			else
				parentNode.AddChild(GeneratedNode3D);
		}
		if (parentHook != _parentHook)
		{
			_parentHook?.FreeNode3D(_poseReadOnParent);
			_poseReadOnParent = parentHook != null && read;
		}
		_parentHook = parentHook!;
		_lastParent = parent;
	}
	// Set or clear the render-only transform override. Pass null for a component to leave that part of
	// the transform alone. Callers must be on the main thread; this writes the node immediately so an
	// override that changes without the slot changing still lands.
	public void SetLocalTransformOverride(float3? position, floatQ? rotation, float3? scale)
	{
		if (SameOverride(_overridePosition, position)
			&& SameOverride(_overrideRotation, rotation)
			&& SameOverride(_overrideScale, scale))
			return;

		_overridePosition = position;
		_overrideRotation = rotation;
		_overrideScale = scale;
		NoteDrawnElsewhere();

		if (GeneratedNode3D != null && GodotObject.IsInstanceValid(GeneratedNode3D))
		{
			SetData();
			MovingShadowCasters.NoteMoved(this);
		}
		PlaceRenderersUnder(Owner, false);
	}

	private bool HasOverride => _overridePosition.HasValue || _overrideRotation.HasValue || _overrideScale.HasValue;

	private void NoteDrawnElsewhere()
	{
		bool elsewhere = _drawsDirect || _drivenValid || HasOverride;
		if (elsewhere == _countedElsewhere)
			return;
		_countedElsewhere = elsewhere;
		_drawnElsewhere += elsewhere ? 1 : -1;
	}

	// Where a renderer on the slot is drawn, in the world's space (the world's root node sits at the origin): the
	// slot's world transform, unless the slot or something above it is drawn somewhere its own pose does not say.
	// False while a body places the slot's renderers on the render server itself.
	internal static bool TryGetDrawnTransform(Slot slot, out Transform3D transform)
	{
		if (_drawnElsewhere == 0)
		{
			transform = ToTransform(slot.LocalToWorld);
			return true;
		}
		if (slot.Hook is SlotHook own && own._drawsDirect)
		{
			transform = default;
			return false;
		}
		transform = ToTransform(DrawnWorld(slot));
		return true;
	}

	// The nearest slot at or above this one drawn elsewhere takes its drawn local pose on top of its parent's drawn
	// world; everything below it follows by its own poses.
	private static float4x4 DrawnWorld(Slot slot)
	{
		Slot? at = slot;
		SlotHook? hook = null;
		for (; at != null; at = at.Parent)
		{
			if (at.Hook is SlotHook h && (h._drivenValid || h.HasOverride))
			{
				hook = h;
				break;
			}
		}
		if (at == null || hook == null)
			return slot.LocalToWorld;
		var parent = at.Parent;
		var world = parent == null ? hook.DrawnLocal() : DrawnWorld(parent) * hook.DrawnLocal();
		return ReferenceEquals(at, slot) ? world : world * Between(at, slot);
	}

	private static float4x4 Between(Slot top, Slot slot)
		=> ReferenceEquals(slot.Parent, top) || slot.Parent == null ? slot.LocalTransform : Between(top, slot.Parent) * slot.LocalTransform;

	private float4x4 DrawnLocal()
	{
		var slot = Owner;
		var position = _drivenValid ? _drivenPosition : _overridePosition ?? slot.LocalPosition.Value;
		var rotation = _drivenValid ? _drivenRotation : _overrideRotation ?? slot.LocalRotation.Value;
		return float4x4.TRS(position, rotation, _overrideScale ?? slot.LocalScale.Value);
	}

	private static Transform3D ToTransform(in float4x4 m)
		=> new(new Basis(new Vector3(m.c0.x, m.c0.y, m.c0.z), new Vector3(m.c1.x, m.c1.y, m.c1.z), new Vector3(m.c2.x, m.c2.y, m.c2.z)),
			new Vector3(m.c3.x, m.c3.y, m.c3.z));

	// Every renderer at or under the slot takes where it is drawn again.
	internal static void PlaceRenderersUnder(Slot? slot, bool force)
	{
		if (slot == null || slot.IsDestroyed)
			return;
		foreach (var component in slot.Components)
		{
			if (component is Lumora.Core.Components.MeshRenderer renderer && renderer.Hook is IPlacedRenderer placed)
				placed.Place(force);
		}
		foreach (var child in slot.Children)
			PlaceRenderersUnder(child, force);
		foreach (var child in slot.LocalChildren)
			PlaceRenderersUnder(child, force);
	}

	// A body places the slot's renderers on the render server itself while on (RigidBodyHook); the renderers take
	// the slot's pose back when it lets go.
	internal void DrawRenderersDirect(bool on)
	{
		if (_drawsDirect == on)
			return;
		_drawsDirect = on;
		NoteDrawnElsewhere();
		if (!on)
			PlaceRenderersUnder(Owner, true);
	}

	private void SetData()
	{
		if (GeneratedNode3D == null) return;

		_appliedVisible = Owner.ActiveSelf.Value && !Owner.IsLocallyHidden;
		GeneratedNode3D.Visible = _appliedVisible;
		_appliedPosition = ToGodotVector3(_overridePosition ?? Owner.LocalPosition.Value);
		GeneratedNode3D.Position = _appliedPosition;
		_appliedRotation = ToGodotQuaternion(_overrideRotation ?? Owner.LocalRotation.Value);
		GeneratedNode3D.Quaternion = _appliedRotation;
		_appliedScale = ToGodotVector3(_overrideScale ?? Owner.LocalScale.Value);
		GeneratedNode3D.Scale = _appliedScale;
	}

	private void UpdateData()
	{
		if (GeneratedNode3D == null) return;

		// Slot.SetLocallyHidden moves no field, so the answer is compared rather than waited for.
		bool activeChanged = Owner.ActiveSelf.GetWasChangedAndClear();
		bool visible = Owner.ActiveSelf.Value && !Owner.IsLocallyHidden;
		if (activeChanged || visible != _appliedVisible)
		{
			_appliedVisible = visible;
			GeneratedNode3D.Visible = visible;
		}

		// A simulated body draws its node itself (DrivePose); the slot's pose would only drag it back to the
		// last physics step.
		bool driven = Owner.NodePoseDriven;
		bool positionMoved = false, rotationMoved = false, scaled = false;
		if (Owner.LocalPosition.GetWasChangedAndClear() && !driven)
		{
			var position = ToGodotVector3(_overridePosition ?? Owner.LocalPosition.Value);
			if (position != _appliedPosition)
			{
				_appliedPosition = position;
				positionMoved = true;
			}
		}

		if (Owner.LocalRotation.GetWasChangedAndClear() && !driven)
		{
			var rotation = ToGodotQuaternion(_overrideRotation ?? Owner.LocalRotation.Value);
			if (rotation != _appliedRotation)
			{
				_appliedRotation = rotation;
				rotationMoved = true;
			}
		}

		if (Owner.LocalScale.GetWasChangedAndClear())
		{
			var scale = ToGodotVector3(_overrideScale ?? Owner.LocalScale.Value);
			if (scale != _appliedScale)
			{
				_appliedScale = scale;
				scaled = true;
			}
		}

		bool moved = positionMoved || rotationMoved || scaled;

		// Nothing reads this node's pose (a bone nothing hangs off, a slot carrying only renderers and colliders):
		// the pose is kept here and the node left. When something comes to need it the whole pose goes over at once.
		if (Owner.NodePoseUnused || _poseReaders == 0)
		{
			if (moved)
				_poseStale = true;
			// Nothing reads where this node is, so it stops following its parent as well: every move of a person's
			// root told every node of their skeleton that it had moved, a frame at a time. Only where the parent's
			// pose is read, since nothing moves a node below one already cut loose. -xlinka
			if (!_cutLoose && (_parentHook == null || _parentHook._poseReaders > 0) && GeneratedNode3D.IsInsideTree())
			{
				_cutLoose = true;
				GeneratedNode3D.TopLevel = true;
			}
		}
		else if (_cutLoose || _poseStale)
		{
			if (_cutLoose)
			{
				_cutLoose = false;
				GeneratedNode3D.TopLevel = false;
			}
			_poseStale = false;
			WriteWholePose();
		}
		// Each write tells every node below this one that it moved, and a person's root carries their whole
		// avatar, so a slot that moved and turned in the same frame gets the two in one write. Only for a
		// positive scale: the node reads its scale back out of the matrix for a later turn on its own, and a
		// mirrored one would come back with every axis flipped. -xlinka
		else if (positionMoved && rotationMoved && !scaled
			&& _appliedScale.X > 0f && _appliedScale.Y > 0f && _appliedScale.Z > 0f)
		{
			var basis = new Basis(_appliedRotation);
			basis.Column0 *= _appliedScale.X;
			basis.Column1 *= _appliedScale.Y;
			basis.Column2 *= _appliedScale.Z;
			GeneratedNode3D.Transform = new Transform3D(basis, _appliedPosition);
		}
		else
		{
			if (positionMoved)
				GeneratedNode3D.Position = _appliedPosition;
			if (rotationMoved)
				GeneratedNode3D.Quaternion = _appliedRotation;
			if (scaled)
				GeneratedNode3D.Scale = _appliedScale;
		}

		// A slot that keeps moving hands the shadows under it to copies that follow at a lower rate, so the
		// lights around it are not re-rendered every frame (see MovingShadowCasters).
		if (moved)
			MovingShadowCasters.NoteMoved(this);

		// Compared against what WE asked for, not what the node reports: siblings that share a name get
		// uniquified by the engine on attach, and asking the node back and re-setting it on every flush
		// only made it uniquify the same name again, forever. -xlinka
		//
		// Never on the changed flag alone. A copy's name is written as it is made, so the first flush saw the
		// flag up and named the node again, and naming a node beside siblings of the same name has the engine
		// try Name2, Name3 and so on until one is free: ten thousand copied cubes under one holder took six
		// seconds of that in a single frame. A node that already carries the name it was made with keeps it.
		Owner.SlotName.GetWasChangedAndClear();
		var slotName = SafeNodeName(Owner.SlotName.Value);
		if (!string.Equals(_appliedName, slotName, System.StringComparison.Ordinal))
		{
			_appliedName = slotName;
			GeneratedNode3D.Name = slotName;
		}
	}

	// A slot inside a canvas is laid out and drawn by the canvas: its rect, its words and its pictures all go
	// into the canvas's chunk meshes, which sit on slots of their own. A local slot is this machine's own
	// helper, a canvas chunk, a cache of material clones, a mesh a view draws. Either gets a node only when
	// something on it asks for one, a mesh or a collider, and that asks for its parents' too. Every slot got
	// one, so a thousand open graph cards carried thirty thousand empty nodes through every Godot frame.
	// -xlinka
	private bool WaitsForRequest
		=> _node3DRequests == 0 && (Owner.IsLocalElement || Owner.Parent?.GetComponent<Helio.UI.RectTransform>() != null);

	public override void Initialize()
	{
		if (WaitsForRequest)
			return;

		if (ShouldDeferHierarchy)
		{
			if (!_didDeferLog)
			{
				Lumora.Core.Logging.Logger.Debug($"SlotHook.Initialize: Deferring Node3D creation for '{Owner.SlotName.Value}'");
				_didDeferLog = true;
			}
			return;
		}

		GenerateNode3D();
		Lumora.Core.Logging.Logger.Debug($"SlotHook.Initialize: Created Node3D for slot '{Owner.SlotName.Value}'");
	}

	public override void ApplyChanges()
	{
		if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
		{
			if (ShouldDeferHierarchy || WaitsForRequest)
			{
				return;
			}

			GenerateNode3D();
			if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
			{
				return;
			}
		}

		if (Owner.Parent != _lastParent || GeneratedNode3D.GetParent() == null)
		{
			UpdateParent();
		}

		UpdateData();
	}

	// Where a simulated body is drawn, between physics steps as well as on them, given in the parent node's
	// space. One marshalled write for both, and none while it has not moved.
	public void DrivePose(in Vector3 position, in Quaternion rotation)
	{
		var node = GeneratedNode3D;
		if (node == null || (position == _appliedPosition && rotation == _appliedRotation))
			return;
		_appliedPosition = position;
		_appliedRotation = rotation;
		var basis = new Basis(rotation);
		basis.Column0 *= _appliedScale.X;
		basis.Column1 *= _appliedScale.Y;
		basis.Column2 *= _appliedScale.Z;
		node.Transform = new Transform3D(basis, position);
		// The renderers are placed past the node, so they are told where the body is drawn.
		_drivenPosition = new float3(position.X, position.Y, position.Z);
		_drivenRotation = new floatQ(rotation.X, rotation.Y, rotation.Z, rotation.W);
		_drivenValid = true;
		NoteDrawnElsewhere();
		PlaceRenderersUnder(Owner, false);
	}

	// The node was left where it was while its renderers were drawn elsewhere: whatever comes next writes its
	// pose, even one equal to the last this hook wrote.
	public void ForgetDrivenPose()
	{
		_appliedPosition = new Vector3(float.NaN, float.NaN, float.NaN);
		_appliedRotation = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
	}

	// Something came to need a pose that was left unwritten: all of it goes over, whatever changed since.
	private void WriteWholePose()
	{
		if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
			return;
		bool driven = Owner.NodePoseDriven;
		if (!driven)
		{
			_appliedPosition = ToGodotVector3(_overridePosition ?? Owner.LocalPosition.Value);
			_appliedRotation = ToGodotQuaternion(_overrideRotation ?? Owner.LocalRotation.Value);
		}
		_appliedScale = ToGodotVector3(_overrideScale ?? Owner.LocalScale.Value);
		bool posed = !float.IsNaN(_appliedPosition.X) && !float.IsNaN(_appliedRotation.X);
		if (posed && _appliedScale.X > 0f && _appliedScale.Y > 0f && _appliedScale.Z > 0f)
		{
			var basis = new Basis(_appliedRotation);
			basis.Column0 *= _appliedScale.X;
			basis.Column1 *= _appliedScale.Y;
			basis.Column2 *= _appliedScale.Z;
			GeneratedNode3D.Transform = new Transform3D(basis, _appliedPosition);
			return;
		}
		if (posed)
		{
			GeneratedNode3D.Position = _appliedPosition;
			GeneratedNode3D.Quaternion = _appliedRotation;
		}
		GeneratedNode3D.Scale = _appliedScale;
	}

	// The drive is over: the node takes the slot's own pose again.
	public void EndDrive()
	{
		if (_drivenValid)
		{
			_drivenValid = false;
			NoteDrawnElsewhere();
		}
		PlaceRenderersUnder(Owner, false);
		if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
			return;
		var position = ToGodotVector3(_overridePosition ?? Owner.LocalPosition.Value);
		var rotation = ToGodotQuaternion(_overrideRotation ?? Owner.LocalRotation.Value);
		if (position != _appliedPosition)
		{
			_appliedPosition = position;
			GeneratedNode3D.Position = position;
		}
		if (rotation != _appliedRotation)
		{
			_appliedRotation = rotation;
			GeneratedNode3D.Quaternion = rotation;
		}
	}

	public override void Destroy(bool destroyingWorld)
	{
		if (!destroyingWorld)
			MovingShadowCasters.Forget(this);
		_drivenValid = false;
		_drawsDirect = false;
		_overridePosition = null;
		_overrideRotation = null;
		_overrideScale = null;
		NoteDrawnElsewhere();
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

	private static bool SameOverride(float3? a, float3? b)
	{
		if (a.HasValue != b.HasValue) return false;
		return !a.HasValue || a.Value == b!.Value;
	}

	private static bool SameOverride(floatQ? a, floatQ? b)
	{
		if (a.HasValue != b.HasValue) return false;
		return !a.HasValue || a.Value == b!.Value;
	}
}

public interface ISlotHook : IHook<Slot>
{
}

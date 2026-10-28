// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Godot.Hooks;
using Godot;
using Lumora.Core;
using Lumora.Core.Components.Gizmos;
using Lumora.Godot.Helpers;

namespace Lumora.Godot.Hooks.Gizmos;

#nullable enable

// Renders the SlotGizmo's frame chrome: the cyan bounds wireframe and the name label. The
// manipulation handles and the translucent mode cubes are engine content built by SlotGizmo.
//
// Draws only, it does not measure. The box arrives already computed in the target's own local
// space and cached engine-side, and it hangs off a slot whose transform mirrors the target, so
// following a moving object is the scene graph's job and reaches no code here at all. Walking the
// node tree to union every mesh AABB - which is what this used to do on every notify, and the gizmo
// notified on every move - is what made dragging a selected object crawl on anything with a real
// mesh under it. Geometry is re-emitted only when the engine bumps its bounds version. -xlinka
[ImplementableHook(typeof(SlotGizmo))]
public sealed class SlotGizmoHook : ComponentHook<SlotGizmo>
{
	private StandardMaterial3D? _boundsMaterial;
	private MeshInstance3D? _boundsMesh;
	private ImmediateMesh? _boundsImmediateMesh;
	private Label3D? _nameLabel;

	private Node3D? _boundsParent;
	private Node3D? _labelParent;
	private int _drawnBoundsVersion = -1;

	// Smallest box worth drawing, in the target's local units: a slot with no geometry at all still
	// needs an outline you can see and aim at.
	private const float MinExtent = 0.01f;

	// Selection reads cyan, like the classic editor look.
	private static readonly Color BoundsColor = new(0f, 1f, 1f, 0.9f);

	// While a handle is held the box drops to a faint outline: the extent is still there to judge by, and it
	// stops drawing over whatever is being lined up.
	private static readonly Color BoundsHeldColor = new(0f, 1f, 1f, 0.25f);

	// The box stands this share of its largest half extent off the bounds on every side. Drawn exactly on the
	// bounds, a boxy object's own edges sat under the cyan lines and fought them for the same depth.
	private const float BoundsPad = 0.015f;

	public static IHook<SlotGizmo> Constructor()
	{
		return new SlotGizmoHook();
	}

	public override void Initialize()
	{
		base.Initialize();

		_boundsMaterial = CreateOverlayMaterial(BoundsColor);
		UpdateVisuals();
	}

	private static StandardMaterial3D CreateOverlayMaterial(Color color)
	{
		var mat = new StandardMaterial3D();
		mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		mat.AlbedoColor = color;
		mat.NoDepthTest = false;
		return mat;
	}

	public override void ApplyChanges()
	{
		UpdateVisuals();
	}

	private void UpdateVisuals()
	{
		var target = Owner.TargetSlot;
		if (target == null)
			return;

		// The gizmo SLOT is the single transform writer (SlotGizmo.FollowTarget drives position and
		// rotation; the SlotHook flushes it to this node). Writing attachedNode here too made two
		// writers fight over the same node - the slot flush stomped the rotation every move. This hook
		// only fills in local-space children under slots the engine placed.
		bool visible = Owner.Active.Value && !Owner.IsFolded.Value;

		EnsureBoundsMesh();
		if (_boundsMesh != null && GodotObject.IsInstanceValid(_boundsMesh))
		{
			if (_drawnBoundsVersion != Owner.BoundsVersion)
			{
				_drawnBoundsVersion = Owner.BoundsVersion;
				DrawWireBounds();
			}
			_boundsMesh.Visible = visible;
		}

		var boundsColor = Owner.IsInteracting ? BoundsHeldColor : BoundsColor;
		if (_boundsMaterial != null && _boundsMaterial.AlbedoColor != boundsColor)
			_boundsMaterial.AlbedoColor = boundsColor;

		EnsureNameLabel();
		if (_nameLabel != null && GodotObject.IsInstanceValid(_nameLabel))
		{
			_nameLabel.Text = target.Name.Value;
			// The label hides while a handle is mid-drag so it doesn't sit in the user's way.
			_nameLabel.Visible = visible && !Owner.IsInteracting;
		}
	}

	// The bounds and label slots are engine content created with the gizmo, so their nodes are just
	// children of this one. Re-parent if the slot was rebuilt under us (a re-targeted gizmo).
	private void EnsureBoundsMesh()
	{
		var parent = Owner.BoundsRoot?.GetGeneratedNode3D(forceGenerate: true);
		if (parent == null || !GodotObject.IsInstanceValid(parent))
			return;

		if (_boundsMesh != null && GodotObject.IsInstanceValid(_boundsMesh) && ReferenceEquals(_boundsParent, parent))
			return;

		if (_boundsMesh != null && GodotObject.IsInstanceValid(_boundsMesh))
			_boundsMesh.QueueFree();

		_boundsImmediateMesh ??= new ImmediateMesh();
		_boundsMesh = new MeshInstance3D
		{
			Name = "BoundingBox",
			Mesh = _boundsImmediateMesh,
			MaterialOverride = _boundsMaterial,
		};
		_boundsParent = parent;
		parent.AddChild(_boundsMesh);
		_drawnBoundsVersion = -1;
	}

	private void EnsureNameLabel()
	{
		var parent = Owner.LabelRoot?.GetGeneratedNode3D(forceGenerate: true);
		if (parent == null || !GodotObject.IsInstanceValid(parent))
			return;

		if (_nameLabel != null && GodotObject.IsInstanceValid(_nameLabel) && ReferenceEquals(_labelParent, parent))
			return;

		if (_nameLabel != null && GodotObject.IsInstanceValid(_nameLabel))
			_nameLabel.QueueFree();

		_nameLabel = new Label3D
		{
			Name = "NameLabel",
			Text = Owner.TargetSlot?.Name.Value ?? "Unknown",
			FontSize = 32,
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			NoDepthTest = false,
			Modulate = new Color(1f, 1f, 1f, 1f),
			OutlineModulate = new Color(0.25f, 0f, 0.25f, 1f),
			OutlineSize = 4,
			PixelSize = 0.001f,
			// The label slot already sits on top of the box; this is the gap above it.
			Position = new Vector3(0f, 0.05f, 0f),
		};
		_labelParent = parent;
		parent.AddChild(_nameLabel);
	}

	private void DrawWireBounds()
	{
		if (_boundsImmediateMesh == null || _boundsMaterial == null)
			return;

		var box = Owner.LocalBounds;
		var min = new Vector3(box.Min.x, box.Min.y, box.Min.z);
		var max = new Vector3(box.Max.x, box.Max.y, box.Max.z);
		var center = (min + max) * 0.5f;
		var half = new Vector3(
			Mathf.Max((max.X - min.X) * 0.5f, MinExtent),
			Mathf.Max((max.Y - min.Y) * 0.5f, MinExtent),
			Mathf.Max((max.Z - min.Z) * 0.5f, MinExtent));
		float pad = Mathf.Max(half.X, Mathf.Max(half.Y, half.Z)) * BoundsPad;
		half += new Vector3(pad, pad, pad);

		_boundsImmediateMesh.ClearSurfaces();

		var p000 = center + new Vector3(-half.X, -half.Y, -half.Z);
		var p100 = center + new Vector3(half.X, -half.Y, -half.Z);
		var p010 = center + new Vector3(-half.X, half.Y, -half.Z);
		var p110 = center + new Vector3(half.X, half.Y, -half.Z);
		var p001 = center + new Vector3(-half.X, -half.Y, half.Z);
		var p101 = center + new Vector3(half.X, -half.Y, half.Z);
		var p011 = center + new Vector3(-half.X, half.Y, half.Z);
		var p111 = center + new Vector3(half.X, half.Y, half.Z);

		_boundsImmediateMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _boundsMaterial);

		// Bottom
		_boundsImmediateMesh.SurfaceAddVertex(p000); _boundsImmediateMesh.SurfaceAddVertex(p100);
		_boundsImmediateMesh.SurfaceAddVertex(p100); _boundsImmediateMesh.SurfaceAddVertex(p101);
		_boundsImmediateMesh.SurfaceAddVertex(p101); _boundsImmediateMesh.SurfaceAddVertex(p001);
		_boundsImmediateMesh.SurfaceAddVertex(p001); _boundsImmediateMesh.SurfaceAddVertex(p000);

		// Top
		_boundsImmediateMesh.SurfaceAddVertex(p010); _boundsImmediateMesh.SurfaceAddVertex(p110);
		_boundsImmediateMesh.SurfaceAddVertex(p110); _boundsImmediateMesh.SurfaceAddVertex(p111);
		_boundsImmediateMesh.SurfaceAddVertex(p111); _boundsImmediateMesh.SurfaceAddVertex(p011);
		_boundsImmediateMesh.SurfaceAddVertex(p011); _boundsImmediateMesh.SurfaceAddVertex(p010);

		// Vertical
		_boundsImmediateMesh.SurfaceAddVertex(p000); _boundsImmediateMesh.SurfaceAddVertex(p010);
		_boundsImmediateMesh.SurfaceAddVertex(p100); _boundsImmediateMesh.SurfaceAddVertex(p110);
		_boundsImmediateMesh.SurfaceAddVertex(p101); _boundsImmediateMesh.SurfaceAddVertex(p111);
		_boundsImmediateMesh.SurfaceAddVertex(p001); _boundsImmediateMesh.SurfaceAddVertex(p011);

		_boundsImmediateMesh.SurfaceEnd();
	}

	public override void Destroy(bool destroyingWorld)
	{
		if (!destroyingWorld)
		{
			if (_boundsMesh != null && GodotObject.IsInstanceValid(_boundsMesh))
				_boundsMesh.QueueFree();
			if (_nameLabel != null && GodotObject.IsInstanceValid(_nameLabel))
				_nameLabel.QueueFree();

			_boundsMaterial?.Dispose();
			_boundsImmediateMesh?.Dispose();
		}

		_boundsMesh = null;
		_nameLabel = null;
		_boundsParent = null;
		_labelParent = null;
		_boundsMaterial = null;
		_boundsImmediateMesh = null;

		base.Destroy(destroyingWorld);
	}
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// The rectangle a text renderer's glyphs actually occupy, which is the thing alignment and line
// spacing move around and which is invisible until something is laid out against it.
//
// Measured from the generated mesh rather than recomputed from the font: the renderer already built
// the glyph quads, and re-deriving the box from metrics would be a second implementation of the
// layout that can disagree with the first. No drag pads - text extent is a RESULT, and a pad that appeared
// to drag it would be editing nothing. The one pad it has is the pencil at the top corner, which types into
// the text in place (TextEditSession). -xlinka
[ComponentCategory("Utility/Gizmos")]
[GizmoForComponent(typeof(TextRenderer))]
public class TextGizmo : ComponentGizmo
{
    // Soft magenta, so text chrome is not mistaken for a collider or a camera.
    protected override color BaseTint => EditTint;

    public static readonly color EditTint = new(0.95f, 0.5f, 0.95f, 0.85f);

    protected override void CollectShapeFields(List<IChangeable> fields)
    {
        if (TargetAs<TextRenderer>() is not { } text)
            return;
        fields.Add(text.Text);
        fields.Add(text.Size);
        fields.Add(text.HorizontalAlign);
        fields.Add(text.VerticalAlign);
        fields.Add(text.LineSpacing);
    }

    protected override void BuildWire(GizmoWireBuilder wire)
    {
        if (TargetAs<TextRenderer>() is not { } text)
            return;

        var bounds = text.GetBoundingBox();
        if (bounds.Min.x > bounds.Max.x || bounds.Min.y > bounds.Max.y)
            return; // nothing generated yet

        var size = bounds.Size;
        var center = bounds.Center;

        // Flat text gets a rectangle; anything with real depth (an extruded or outlined build) gets the
        // full box, because a rectangle would hide half of it.
        if (size.z <= FlatThreshold)
            wire.Rect(new float3(center.x, center.y, center.z), size.x, size.y);
        else
            wire.Box(bounds);

        // Baseline origin tick: where the renderer's own zero sits inside the block, which is what
        // alignment is actually moving.
        wire.Cross(float3.Zero, OriginMarkerSize);
    }

    private const float FlatThreshold = 1e-4f;
    private const float OriginMarkerSize = 0.01f;

    // EDIT PAD

    private const string PadName = "Edit";
    private const string PencilIcon = "res://Assets/Textures/Icons/edit-widgets.svg";
    private const float PadMin = 0.03f;
    private const float PadMax = 0.08f;

    private Slot? _pad;
    private float2 _renderedSize = new(float.NaN, 0f);

    protected override void BuildHandles()
    {
        var gizmoSlot = Slot;
        if (gizmoSlot == null || gizmoSlot.IsRemoved || TargetAs<TextRenderer>() is not { } text)
            return;
        _pad = gizmoSlot.FindChild(PadName, recursive: false) ?? gizmoSlot.AddSlot(PadName);
        _pad.Persistent.Value = false;
        var pad = _pad.GetComponent<TextEditPad>() ?? _pad.AttachComponent<TextEditPad>();
        pad.Text.Target = text;
        if (_pad.FindChild("Plate", recursive: false) != null)
            return;

        var plate = _pad.AddSlot("Plate");
        var plateMesh = plate.AttachComponent<RoundedQuadMesh>();
        plateMesh.Size.Value = float2.One;
        plateMesh.CornerRadius.Value = 0.25f;
        var face = Overlay(plate, new colorHDR(0.16f, 0.12f, 0.22f, 0.7f));
        Render(plate, plateMesh, face);
        pad.Face.Target = face;

        var icon = _pad.AddSlot("Icon");
        icon.LocalPosition.Value = new float3(0f, 0f, 0.02f);
        icon.LocalScale.Value = float3.One * 0.68f;
        var picture = icon.AttachComponent<ImageProvider>();
        picture.GenerateMipmaps.Value = false;
        picture.URL.Value = new Uri(PencilIcon);
        var quad = icon.AttachComponent<QuadMesh>();
        quad.Size.Value = float2.One;
        var ink = Overlay(icon, new colorHDR(1f, 0.92f, 1f, 1f));
        ink.Texture.Target = picture;
        Render(icon, quad, ink);
    }

    // Top corner on the reading side, a little out from the glyphs, as big as a line of the text within limits
    // so it is neither lost beside a headline nor bigger than a footnote.
    protected override void LayoutHandles()
    {
        var pad = _pad;
        if (pad == null || pad.IsRemoved || TargetAs<TextRenderer>() is not { } text)
            return;
        var bounds = text.GetBoundingBox();
        bool laidOut = bounds.Min.x <= bounds.Max.x && bounds.Min.y <= bounds.Max.y;
        var scale = TargetScale;
        float size = System.Math.Clamp(text.Size.Value * MathF.Abs(scale.y) * 0.9f, PadMin, PadMax);
        bool backward = text.FaceBackward.Value;
        float edge = !laidOut ? 0f : backward ? bounds.Min.x * scale.x : bounds.Max.x * scale.x;
        float top = laidOut ? bounds.Max.y * scale.y : 0f;
        pad.LocalPosition.Value = new float3(edge + (backward ? -size : size) * 0.8f, top - size * 0.5f, 0f);
        pad.LocalRotation.Value = backward ? floatQ.AxisAngleRad(float3.Up, MathF.PI) : floatQ.Identity;
        pad.LocalScale.Value = float3.One * size;
        pad.ActiveSelf.Value = IsVisible;
    }

    // The glyph mesh is built after the text field changes, so the box can grow a frame or more after the write
    // that changed it. Typing in place would otherwise leave the wire and the pencil on the old size.
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (TargetAs<TextRenderer>() is not { } text)
            return;
        var size = text.RenderedSize;
        if (size == _renderedSize)
            return;
        _renderedSize = size;
        MarkShapeDirty();
    }

    private static OverlayUnlitMaterial Overlay(Slot slot, in colorHDR tint)
    {
        var material = slot.AttachComponent<OverlayUnlitMaterial>();
        material.BlendMode.Value = BlendMode.Alpha;
        material.Culling.Value = Culling.None;
        material.FrontTintColor.Value = tint;
        material.BehindTintColor.Value = new colorHDR(tint.r, tint.g, tint.b, tint.a * 0.6f);
        return material;
    }

    private static void Render(Slot slot, ProceduralMesh mesh, OverlayUnlitMaterial material)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }
}

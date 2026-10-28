// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Helio.UI;

// Ring-segment graphic for radial menus. Angles are degrees, 0 degrees = right,
// increasing clockwise (same convention as ContextMenuPage.LayoutItems);
// radii are canvas units measured from the rect center. Hit-testing is
// polar, so interaction follows the arc shape exactly. - xlinka
//
// The shape is one mesh: the rounded outline of the segment is traced once, the fill, the outline band and
// a soft edge band are strips off that one trace, and neighbouring strips share their seam vertices exactly.
// Built as separate overlapping bands at different steps, every seam was a T-junction the rasteriser left
// pixel cracks along, and the 6 degree steps showed as flats up close. Curves are now split finely enough
// that no chord strays more than a twentieth of a canvas unit off the true circle. -xlinka
[ComponentCategory("UI/Helio/Graphics")]
public sealed class ArcSegment : Graphic
{
    public readonly Sync<float> AngleStart;
    public readonly Sync<float> ArcLength;
    public readonly Sync<float> InnerRadius;
    public readonly Sync<float> OuterRadius;
    public readonly Sync<color> Tint;
    public readonly Sync<color> OutlineColor;
    public readonly Sync<float> OutlineThickness;
    // Corner rounding in canvas units. Zero keeps corners as sharp as the outline allows.
    public readonly Sync<float> CornerRadius;
    // Width of the soft band outside the edge, in canvas units: the edge fades out over it instead of
    // stair-stepping, which is most of what reads as clean at a distance.
    public readonly Sync<float> EdgeSoftness;
    // The radii as shares of half the rect's shorter side rather than canvas units, so the ring grows and
    // shrinks with the rect it is laid out into.
    public readonly Sync<bool> RadiiFromRect;

    private const float MaxChordError = 0.05f;

    private float _angleStart;
    private float _arcLength;
    private float _innerRadius;
    private float _outerRadius;
    private color _tint;
    private color _outlineColor;
    private float _outlineThickness;
    private float _cornerRadius;
    private float _edgeSoftness;
    private float _innerSetting;
    private float _outerSetting;
    private bool _radiiFromRect;

    [ThreadStatic] private static List<float2>? _outerChain;
    [ThreadStatic] private static List<float2>? _outerNormals;
    [ThreadStatic] private static List<float2>? _innerChain;
    [ThreadStatic] private static List<float2>? _innerNormals;

    public ArcSegment()
    {
        AngleStart = new Sync<float>(this, 0f);
        ArcLength = new Sync<float>(this, 90f);
        InnerRadius = new Sync<float>(this, 55f);
        OuterRadius = new Sync<float>(this, 135f);
        Tint = new Sync<color>(this, color.White);
        OutlineColor = new Sync<color>(this, new color(0.45f, 0.45f, 0.45f, 1f));
        OutlineThickness = new Sync<float>(this, 3f);
        CornerRadius = new Sync<float>(this, 0f);
        EdgeSoftness = new Sync<float>(this, 0.75f);
        RadiiFromRect = new Sync<bool>(this, false);
    }

    public override bool RequiresPreGraphicsCompute => false;

    protected override void FlagChanges(RectTransform rect)
    {
        rect.MarkGraphicDirty();
    }

    public override void PrepareCompute()
    {
        _angleStart = AngleStart.Value;
        _arcLength = ArcLength.Value;
        _innerSetting = InnerRadius.Value;
        _outerSetting = OuterRadius.Value;
        _radiiFromRect = RadiiFromRect.Value;
        _innerRadius = _innerSetting;
        _outerRadius = _outerSetting;
        _tint = Tint.Value;
        _outlineColor = OutlineColor.Value;
        _outlineThickness = OutlineThickness.Value;
        _cornerRadius = CornerRadius.Value;
        _edgeSoftness = EdgeSoftness.Value;
    }

    public override void ComputeGraphic(GraphicsChunk.RenderData renderData)
    {
        var rectTransform = RectTransform;
        if (rectTransform == null || _arcLength <= 0f)
            return;

        var rect = rectTransform.LocalComputeRect;
        ResolveRadii(in rect);
        if (_outerRadius <= _innerRadius)
            return;
        float2 center = new float2(rect.xMin + rect.width * 0.5f, rect.yMin + rect.height * 0.5f);

        var mesh = renderData.Mesh;
        mesh.HasColors = true;
        mesh.SetHasUV(0, true);
        var submesh = renderData.GetSubmesh((IAssetProvider<MaterialAsset>?)null);

        float innerRadius = MathF.Max(_innerRadius, 0f);
        float band = _outerRadius - innerRadius;
        bool outlined = _outlineThickness > 0f && _outlineColor.a > 0f;
        float thickness = outlined ? MathF.Min(_outlineThickness, band * 0.4f) : 0f;
        float soft = MathF.Max(_edgeSoftness, 0f);
        var edgeColor = outlined ? _outlineColor : _tint;

        if (_arcLength >= 359.9f)
        {
            EmitRing(mesh, submesh, center, innerRadius, _outerRadius, thickness, soft, edgeColor);
            return;
        }

        BuildContour(center, innerRadius, thickness);
        EmitShape(mesh, submesh, thickness, soft, edgeColor);
    }

    // The segment's outline as two chains from the middle of its start edge to the middle of its end edge,
    // one along the outer arc and one along the inner, with the same number of points so the fill can be
    // strung between them. Every point carries the outward normal there, which places the inset and the
    // soft edge exactly parallel to it.
    private void BuildContour(in float2 center, float innerRadius, float thickness)
    {
        var outer = _outerChain ??= new List<float2>();
        var outerNormals = _outerNormals ??= new List<float2>();
        var inner = _innerChain ??= new List<float2>();
        var innerNormals = _innerNormals ??= new List<float2>();
        outer.Clear();
        outerNormals.Clear();
        inner.Clear();
        innerNormals.Clear();

        float a0 = _angleStart;
        float a1 = _angleStart + _arcLength;
        float band = _outerRadius - innerRadius;

        // The rounding cannot be tighter than the outline (the inset corner would turn inside out) or wider
        // than the slice has room for at its inner edge.
        float radius = MathF.Max(_cornerRadius, thickness);
        radius = MathF.Min(radius, band * 0.45f);
        float room = MathF.Sin(MathF.Min(_arcLength * 0.45f, 89f) * (MathF.PI / 180f));
        float innerRadiusCorner = innerRadius > 0f ? MathF.Min(radius, room * innerRadius / MathF.Max(1f - room, 1e-3f)) : 0f;
        innerRadiusCorner = MathF.Max(innerRadiusCorner, MathF.Min(thickness, radius));
        float outerRadiusCorner = radius;

        // Corner centres sit on the circle a corner radius in from each arc, turned in from each edge by
        // exactly the angle that puts them a corner radius off it.
        float innerRing = innerRadius + innerRadiusCorner;
        float outerRing = _outerRadius - outerRadiusCorner;
        float innerInset = innerRing > 1e-4f ? MathF.Asin(MathF.Min(innerRadiusCorner / innerRing, 1f)) * (180f / MathF.PI) : 0f;
        float outerInset = outerRing > 1e-4f ? MathF.Asin(MathF.Min(outerRadiusCorner / outerRing, 1f)) * (180f / MathF.PI) : 0f;

        float2 startDir = Direction(a0);
        float2 endDir = Direction(a1);
        float2 middleDir = Direction((a0 + a1) * 0.5f);
        float2 startNormal = OutwardEdgeNormal(startDir, middleDir);
        float2 endNormal = OutwardEdgeNormal(endDir, middleDir);

        int cornerSteps = Steps(90f, MathF.Max(outerRadiusCorner, innerRadiusCorner));
        int arcSteps = Steps(_arcLength, _outerRadius);
        float midRadius = (innerRadius + _outerRadius) * 0.5f;

        // Outer chain: start edge (middle -> corner), corner, outer arc, corner, end edge (corner -> middle).
        Add(outer, outerNormals, center + startDir * midRadius, startNormal);
        AddCorner(outer, outerNormals, center, a0 + outerInset, outerRing, outerRadiusCorner, startDir, startNormal, outwardArc: true, cornerSteps, atStart: true);
        AddArc(outer, outerNormals, center, a0 + outerInset, a1 - outerInset, _outerRadius, outward: true, arcSteps);
        AddCorner(outer, outerNormals, center, a1 - outerInset, outerRing, outerRadiusCorner, endDir, endNormal, outwardArc: true, cornerSteps, atStart: false);
        Add(outer, outerNormals, center + endDir * midRadius, endNormal);

        Add(inner, innerNormals, center + startDir * midRadius, startNormal);
        AddCorner(inner, innerNormals, center, a0 + innerInset, innerRing, innerRadiusCorner, startDir, startNormal, outwardArc: false, cornerSteps, atStart: true);
        AddArc(inner, innerNormals, center, a0 + innerInset, a1 - innerInset, innerRadius, outward: false, arcSteps);
        AddCorner(inner, innerNormals, center, a1 - innerInset, innerRing, innerRadiusCorner, endDir, endNormal, outwardArc: false, cornerSteps, atStart: false);
        Add(inner, innerNormals, center + endDir * midRadius, endNormal);
    }

    private void EmitShape(PhosMesh mesh, PhosTriangleSubmesh submesh, float thickness, float soft, in color edgeColor)
    {
        var outer = _outerChain!;
        var outerNormals = _outerNormals!;
        var inner = _innerChain!;
        var innerNormals = _innerNormals!;
        int count = outer.Count;

        // Fill between the two inset chains.
        int fillOuter = AddRow(mesh, outer, outerNormals, -thickness, _tint);
        int fillInner = AddRow(mesh, inner, innerNormals, -thickness, _tint);
        for (int i = 0; i < count - 1; i++)
            AddQuad(mesh, submesh, fillInner + i, fillOuter + i, fillOuter + i + 1, fillInner + i + 1);

        // The closed outline: outer chain forward, inner chain back, as one loop of rings.
        int loopCount = count * 2;
        var loop = new float2[loopCount];
        var loopNormals = new float2[loopCount];
        for (int i = 0; i < count; i++)
        {
            loop[i] = outer[i];
            loopNormals[i] = outerNormals[i];
            loop[count + i] = inner[count - 1 - i];
            loopNormals[count + i] = innerNormals[count - 1 - i];
        }

        if (thickness > 0f)
        {
            int inset = AddRow(mesh, loop, loopNormals, -thickness, edgeColor);
            int edge = AddRow(mesh, loop, loopNormals, 0f, edgeColor);
            AddLoopStrip(mesh, submesh, inset, edge, loopCount);
        }
        if (soft > 0f)
        {
            int edge = AddRow(mesh, loop, loopNormals, 0f, edgeColor);
            var clear = new color(edgeColor.r, edgeColor.g, edgeColor.b, 0f);
            int fade = AddRow(mesh, loop, loopNormals, soft, clear);
            AddLoopStrip(mesh, submesh, edge, fade, loopCount);
        }
    }

    // A full ring or a disc: concentric circles on one set of angles.
    private void EmitRing(PhosMesh mesh, PhosTriangleSubmesh submesh, in float2 center, float innerRadius, float outerRadius,
        float thickness, float soft, in color edgeColor)
    {
        int steps = Steps(360f, outerRadius + soft);
        bool disc = innerRadius <= 1e-3f;
        var clear = new color(edgeColor.r, edgeColor.g, edgeColor.b, 0f);

        int outerFill = AddCircle(mesh, center, outerRadius - thickness, steps, _tint);
        if (disc)
        {
            int middle = mesh.VertexCount;
            mesh.IncreaseVertexCount(1);
            SetVertex(mesh, middle, center, _tint);
            for (int i = 0; i < steps; i++)
                AddTriangle(mesh, submesh, middle, outerFill + i, outerFill + (i + 1) % steps);
        }
        else
        {
            int innerFill = AddCircle(mesh, center, innerRadius + thickness, steps, _tint);
            AddRingStrip(mesh, submesh, innerFill, outerFill, steps);
        }

        if (thickness > 0f)
        {
            AddRingStrip(mesh, submesh, AddCircle(mesh, center, outerRadius - thickness, steps, edgeColor),
                AddCircle(mesh, center, outerRadius, steps, edgeColor), steps);
            if (!disc)
                AddRingStrip(mesh, submesh, AddCircle(mesh, center, innerRadius, steps, edgeColor),
                    AddCircle(mesh, center, innerRadius + thickness, steps, edgeColor), steps);
        }
        if (soft > 0f)
        {
            AddRingStrip(mesh, submesh, AddCircle(mesh, center, outerRadius, steps, edgeColor),
                AddCircle(mesh, center, outerRadius + soft, steps, clear), steps);
            if (!disc && innerRadius > soft)
                AddRingStrip(mesh, submesh, AddCircle(mesh, center, innerRadius - soft, steps, clear),
                    AddCircle(mesh, center, innerRadius, steps, edgeColor), steps);
        }
    }

    // Steps for a turn of `degrees` at `radius` that keep every chord within MaxChordError of the circle.
    private static int Steps(float degrees, float radius)
    {
        if (radius <= MaxChordError * 2f)
            return System.Math.Max(2, (int)MathF.Ceiling(degrees / 30f));
        float stepRadians = 2f * MathF.Acos(1f - MaxChordError / radius);
        float stepDegrees = MathF.Max(stepRadians * (180f / MathF.PI), 0.5f);
        return System.Math.Clamp((int)MathF.Ceiling(degrees / stepDegrees), 2, 720);
    }

    // Clockwise screen convention: +angle goes down-screen.
    private static float2 Direction(float degrees)
    {
        float rad = degrees * (MathF.PI / 180f);
        return new float2(MathF.Cos(rad), -MathF.Sin(rad));
    }

    private static float2 OutwardEdgeNormal(in float2 edgeDir, in float2 middleDir)
    {
        var normal = new float2(-edgeDir.y, edgeDir.x);
        return normal.x * middleDir.x + normal.y * middleDir.y > 0f ? new float2(-normal.x, -normal.y) : normal;
    }

    private static void Add(List<float2> points, List<float2> normals, in float2 point, in float2 normal)
    {
        points.Add(point);
        normals.Add(normal);
    }

    // A corner: from the foot on the edge round to the touch point on the arc (atStart), or back again.
    // The circle is centred on `ring` at angle `centreAngle`; its outward directions run from the edge's
    // normal to the arc's own normal, the short way round.
    private static void AddCorner(List<float2> points, List<float2> normals, in float2 center, float centreAngle, float ring,
        float radius, in float2 edgeDir, in float2 edgeNormal, bool outwardArc, int steps, bool atStart)
    {
        float2 radial = Direction(centreAngle);
        float2 corner = center + radial * ring;
        float2 arcNormal = outwardArc ? radial : new float2(-radial.x, -radial.y);
        float from = MathF.Atan2(edgeNormal.y, edgeNormal.x);
        float to = MathF.Atan2(arcNormal.y, arcNormal.x);
        float sweep = to - from;
        while (sweep > MathF.PI) sweep -= 2f * MathF.PI;
        while (sweep < -MathF.PI) sweep += 2f * MathF.PI;

        for (int i = 0; i <= steps; i++)
        {
            float t = atStart ? i / (float)steps : 1f - i / (float)steps;
            float angle = from + sweep * t;
            var normal = new float2(MathF.Cos(angle), MathF.Sin(angle));
            Add(points, normals, corner + normal * radius, normal);
        }
    }

    private static void AddArc(List<float2> points, List<float2> normals, in float2 center, float fromDegrees, float toDegrees,
        float radius, bool outward, int steps)
    {
        // The end points belong to the corners on either side.
        for (int i = 1; i < steps; i++)
        {
            var dir = Direction(fromDegrees + (toDegrees - fromDegrees) * (i / (float)steps));
            Add(points, normals, center + dir * radius, outward ? dir : new float2(-dir.x, -dir.y));
        }
    }

    private static int AddRow(PhosMesh mesh, IReadOnlyList<float2> points, IReadOnlyList<float2> normals, float offset, in color tint)
    {
        int first = mesh.VertexCount;
        mesh.IncreaseVertexCount(points.Count);
        for (int i = 0; i < points.Count; i++)
            SetVertex(mesh, first + i, points[i] + normals[i] * offset, tint);
        return first;
    }

    private static int AddCircle(PhosMesh mesh, in float2 center, float radius, int steps, in color tint)
    {
        int first = mesh.VertexCount;
        mesh.IncreaseVertexCount(steps);
        for (int i = 0; i < steps; i++)
            SetVertex(mesh, first + i, center + Direction(360f * i / steps) * radius, tint);
        return first;
    }

    private static void SetVertex(PhosMesh mesh, int index, in float2 position, in color tint)
    {
        mesh.RawPositions[index] = new float3(position.x, position.y, 0f);
        mesh.RawColors[index] = tint;
        mesh.SetUV(0, index, float2.Zero);
    }

    private static void AddLoopStrip(PhosMesh mesh, PhosTriangleSubmesh submesh, int a, int b, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            AddQuad(mesh, submesh, a + i, b + i, b + next, a + next);
        }
    }

    private static void AddRingStrip(PhosMesh mesh, PhosTriangleSubmesh submesh, int a, int b, int steps)
        => AddLoopStrip(mesh, submesh, a, b, steps);

    private static void AddQuad(PhosMesh mesh, PhosTriangleSubmesh submesh, int a, int b, int c, int d)
    {
        AddTriangle(mesh, submesh, a, b, c);
        AddTriangle(mesh, submesh, a, c, d);
    }

    // Every triangle wound counter-clockwise in canvas space, the way the canvas faces; a culled canvas drops
    // the other way round. A collapsed triangle (two chains meeting at an edge's middle) is dropped.
    private static void AddTriangle(PhosMesh mesh, PhosTriangleSubmesh submesh, int a, int b, int c)
    {
        var pa = mesh.RawPositions[a];
        var pb = mesh.RawPositions[b];
        var pc = mesh.RawPositions[c];
        float cross = (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
        if (MathF.Abs(cross) < 1e-7f)
            return;
        if (cross > 0f)
            submesh.AddTriangle(a, b, c);
        else
            submesh.AddTriangle(a, c, b);
    }

    private void ResolveRadii(in Rect rect)
    {
        if (!_radiiFromRect)
        {
            _innerRadius = _innerSetting;
            _outerRadius = _outerSetting;
            return;
        }
        float half = MathF.Min(rect.width, rect.height) * 0.5f;
        _innerRadius = _innerSetting * half;
        _outerRadius = _outerSetting * half;
    }

    public override bool IsPointInside(in float2 point)
    {
        var rectTransform = RectTransform;
        if (rectTransform == null)
            return false;

        var rect = rectTransform.LocalComputeRect;
        ResolveRadii(in rect);
        float dx = point.x - (rect.xMin + rect.width * 0.5f);
        float dy = point.y - (rect.yMin + rect.height * 0.5f);

        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance < _innerRadius || distance > _outerRadius)
            return false;

        // Invert dy to match the clockwise emission convention.
        float angle = MathF.Atan2(-dy, dx) * (180f / MathF.PI);
        float relative = angle - _angleStart;
        relative -= MathF.Floor(relative / 360f) * 360f;
        return relative <= _arcLength;
    }
}

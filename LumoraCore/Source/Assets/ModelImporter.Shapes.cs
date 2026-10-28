// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

public static partial class ModelImporter
{
    // Recognise primitive surfaces, otherwise use the part's convex hull. A small enclosing volume
    // alone cannot distinguish a cone or an irregular prop from a ball or box. -xlinka
    private sealed class ShapeFit
    {
        private enum Shape { Box, Sphere, Capsule, Cylinder, Cone, Hull }

        private Shape _shape;
        private float3 _center;
        private float3 _size;
        private float _radius;
        private float _height;
        private int _axis;
        private bool _flipped;

        public static ShapeFit? Of(List<float3> points)
        {
            if (points == null || points.Count == 0)
                return null;

            float3 lo = points[0], hi = points[0];
            foreach (var p in points)
            {
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z))
                    return null;
                lo = new float3(MathF.Min(lo.x, p.x), MathF.Min(lo.y, p.y), MathF.Min(lo.z, p.z));
                hi = new float3(MathF.Max(hi.x, p.x), MathF.Max(hi.y, p.y), MathF.Max(hi.z, p.z));
            }
            var center = (lo + hi) * 0.5f;
            var size = hi - lo;
            float longest = MathF.Max(size.x, MathF.Max(size.y, size.z));
            if (!float.IsFinite(longest) || longest <= 0f)
                return null;

            var fit = new ShapeFit { _shape = Shape.Hull, _center = center, _size = size };
            // A flat surface still needs thickness to collide reliably. -xlinka
            float floor = MathF.Max(longest * 0.01f, 0.001f);
            if (MathF.Min(size.x, MathF.Min(size.y, size.z)) < floor)
            {
                fit._shape = Shape.Box;
                fit._size = new float3(MathF.Max(size.x, floor), MathF.Max(size.y, floor), MathF.Max(size.z, floor));
                return fit;
            }

            float tolerance = longest * 0.005f;
            int corners = 0;
            bool boxSurface = true;
            float radius = 0f;
            foreach (var p in points)
            {
                var d = p - center;
                var edge = new float3(MathF.Abs(MathF.Abs(d.x) - size.x * 0.5f),
                    MathF.Abs(MathF.Abs(d.y) - size.y * 0.5f), MathF.Abs(MathF.Abs(d.z) - size.z * 0.5f));
                boxSurface &= MathF.Min(edge.x, MathF.Min(edge.y, edge.z)) <= tolerance;
                if (edge.x <= tolerance && edge.y <= tolerance && edge.z <= tolerance)
                    corners |= 1 << ((d.x > 0f ? 1 : 0) | (d.y > 0f ? 2 : 0) | (d.z > 0f ? 4 : 0));
                radius = MathF.Max(radius, d.Length);
            }
            if (boxSurface && corners == 255)
            {
                fit._shape = Shape.Box;
                return fit;
            }

            // Distinct positions avoid counting split normals and UV seams as extra surface coverage.
            // Twelve positions include an unsubdivided icosphere but exclude a tetrahedron. -xlinka
            var distinct = new HashSet<float3>();
            foreach (var p in points)
            {
                distinct.Add(p);
                if (distinct.Count >= 12)
                    break;
            }
            if (distinct.Count >= 12 && points.TrueForAll(p => MathF.Abs((p - center).Length - radius) <= tolerance))
            {
                fit._shape = Shape.Sphere;
                fit._radius = radius;
                return fit;
            }

            for (int axis = 0; axis < 3; axis++)
            {
                float height = Component(size, axis);
                float half = height * 0.5f;
                float radiusSq = 0f;
                foreach (var p in points)
                    radiusSq = MathF.Max(radiusSq, RadialSquared(p - center, axis));
                radius = MathF.Sqrt(radiusSq);
                float crossA = Component(size, (axis + 1) % 3);
                float crossB = Component(size, (axis + 2) % 3);
                if (distinct.Count < 12 || MathF.Abs(crossA - crossB) > radius * 0.04f)
                    continue;

                for (int sign = -1; sign <= 1; sign += 2)
                {
                    bool cone = true, apex = false, baseRim = false;
                    foreach (var p in points)
                    {
                        var d = p - center;
                        float along = Component(d, axis) * sign;
                        float radial = MathF.Sqrt(RadialSquared(d, axis));
                        apex |= half - along <= tolerance && radial <= tolerance;
                        baseRim |= along + half <= tolerance && MathF.Abs(radial - radius) <= tolerance;
                        float surfaceRadius = radius * (half - along) / height;
                        if (along + half > tolerance && MathF.Abs(radial - surfaceRadius) > tolerance)
                        {
                            cone = false;
                            break;
                        }
                    }
                    if (cone && apex && baseRim)
                    {
                        fit._shape = Shape.Cone;
                        fit._radius = radius;
                        fit._height = height;
                        fit._axis = axis;
                        fit._flipped = sign < 0;
                        return fit;
                    }
                }

                bool cylinder = true, topRim = false, bottomRim = false;
                float halfLength = 0f;
                foreach (var p in points)
                {
                    var d = p - center;
                    float along = Component(d, axis);
                    float rhoSq = RadialSquared(d, axis);
                    bool rim = MathF.Abs(MathF.Sqrt(rhoSq) - radius) <= tolerance;
                    topRim |= half - along <= tolerance && rim;
                    bottomRim |= along + half <= tolerance && rim;
                    if (MathF.Abs(along) < half - tolerance && !rim)
                        cylinder = false;
                    halfLength = MathF.Max(halfLength, MathF.Abs(along) - MathF.Sqrt(MathF.Max(0f, radiusSq - rhoSq)));
                }
                if (cylinder && topRim && bottomRim)
                {
                    fit._shape = Shape.Cylinder;
                    fit._radius = radius;
                    fit._height = height;
                    fit._axis = axis;
                    return fit;
                }
                bool capsule = halfLength > tolerance;
                foreach (var p in points)
                {
                    var d = p - center;
                    float end = MathF.Max(0f, MathF.Abs(Component(d, axis)) - halfLength);
                    if (MathF.Abs(MathF.Sqrt(RadialSquared(d, axis) + end * end) - radius) > tolerance)
                    {
                        capsule = false;
                        break;
                    }
                }
                if (capsule)
                {
                    fit._shape = Shape.Capsule;
                    fit._radius = radius;
                    fit._height = 2f * (halfLength + radius);
                    fit._axis = axis;
                    return fit;
                }
            }
            return fit;
        }

        private static float Component(float3 v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;

        private static float RadialSquared(float3 v, int axis)
        {
            float a = Component(v, (axis + 1) % 3), b = Component(v, (axis + 2) % 3);
            return a * a + b * b;
        }

        public string Name => _shape.ToString();

        public void Attach(Slot part, Component mesh, bool solidToPeople)
        {
            Collider collider;
            var host = part;
            var offset = _center;
            if ((_shape is Shape.Capsule or Shape.Cylinder or Shape.Cone) && (_axis != 1 || _flipped))
            {
                host = part.AddSlot("Collider");
                host.LocalPosition.Value = _center;
                var rotation = _axis == 0
                    ? floatQ.AxisAngle(new float3(0f, 0f, 1f), -MathF.PI * 0.5f)
                    : _axis == 2 ? floatQ.AxisAngle(new float3(1f, 0f, 0f), MathF.PI * 0.5f) : floatQ.Identity;
                if (_flipped)
                    rotation *= floatQ.AxisAngle(float3.Right, MathF.PI);
                host.LocalRotation.Value = rotation;
                offset = float3.Zero;
            }
            switch (_shape)
            {
                case Shape.Sphere:
                    var sphere = host.AttachComponent<SphereCollider>();
                    sphere.Offset.Value = offset;
                    sphere.Radius.Value = _radius;
                    collider = sphere;
                    break;
                case Shape.Capsule:
                    var capsule = host.AttachComponent<CapsuleCollider>();
                    capsule.Offset.Value = offset;
                    capsule.Radius.Value = _radius;
                    capsule.Height.Value = MathF.Max(_height, _radius * 2f);
                    collider = capsule;
                    break;
                case Shape.Cylinder:
                    var cylinder = host.AttachComponent<CylinderCollider>();
                    cylinder.Offset.Value = offset;
                    cylinder.Radius.Value = _radius;
                    cylinder.Height.Value = _height;
                    collider = cylinder;
                    break;
                case Shape.Cone:
                    var cone = host.AttachComponent<ConeCollider>();
                    cone.Offset.Value = offset;
                    cone.Radius.Value = _radius;
                    cone.Height.Value = _height;
                    collider = cone;
                    break;
                case Shape.Hull:
                    var hull = host.AttachComponent<ConvexHullCollider>();
                    hull.Mesh.Target = mesh;
                    collider = hull;
                    break;
                default:
                    var box = host.AttachComponent<BoxCollider>();
                    box.Offset.Value = offset;
                    box.Size.Value = _size;
                    collider = box;
                    break;
            }
            collider.CharacterCollider.Value = solidToPeople;
        }
    }
}

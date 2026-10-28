// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Godot.Hooks;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Rendering;

// The renderer's shadow-caster culler intersects a camera's six frustum planes three at a time and
// gives up on the frame (two engine errors per directional light, no camera named) when any triple is
// near-parallel. Only three things get a camera there: a field of view within a hair of 180, a
// viewport whose aspect has collapsed, or a global basis squashed towards singular by an ancestor.
// This runs the culler's own test on every camera about to render, before the renderer does, names
// the camera and its numbers once, and repairs what can be repaired from here: a clip pair or field
// of view that is not a number passes the engine's own setters (NaN compares false against every
// bound) and turns the whole projection to NaN. A squashed basis is reported together with the
// ancestor that squashed it, since rewriting the camera's transform under a singular parent only
// trades one bad frame for another. -xlinka
public static class CameraSanity
{
    // The engine's CMP_EPSILON: what its three-plane intersection calls parallel.
    private const float ParallelEpsilon = 1e-5f;

    private static readonly HashSet<string> _reported = new();
    private static readonly List<SubViewport> _scratch = new();
    private static readonly Vector3[] _normals = new Vector3[6];
    private static readonly string[] PlaneNames = { "near", "far", "left", "top", "right", "bottom" };
    private static readonly (int a, int b, int c)[] Triples =
    {
        (1, 2, 3), (1, 2, 5), (1, 4, 3), (1, 4, 5), (0, 2, 3), (0, 2, 5), (0, 4, 3), (0, 4, 5),
    };

    public static void Check(Viewport? root, Viewport? xr)
    {
        try
        {
            if (root != null && GodotObject.IsInstanceValid(root))
                CheckViewport(root, "root");
            if (xr != null && xr != root && GodotObject.IsInstanceValid(xr) && xr.IsInsideTree())
                CheckViewport(xr, null);
            _scratch.Clear();
            RenderTextureHook.CollectLiveViewports(_scratch);
            CameraPortalHook.CollectLiveViewports(_scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var viewport = _scratch[i];
                if (viewport != null && GodotObject.IsInstanceValid(viewport) && viewport.IsInsideTree())
                    CheckViewport(viewport, null);
            }
            _scratch.Clear();
        }
        catch (Exception ex)
        {
            ReportOnce("self", $"CameraSanity: the check itself failed: {ex.Message}");
        }
    }

    // A viewport's name is only read for a report: read every frame, it was a new string per viewport per frame.
    private static void CheckViewport(Viewport viewport, string? label)
    {
        // A headset viewport's projection comes from the runtime, not from a Camera3D.
        if (viewport.UseXR)
            return;
        if (viewport is SubViewport sub && sub.RenderTargetUpdateMode == SubViewport.UpdateMode.Disabled)
            return;
        var camera = viewport.GetCamera3D();
        if (camera == null || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree())
            return;

        string? problem = null;
        string kinds = "";
        float near = camera.Near;
        float far = camera.Far;
        if (!float.IsFinite(near) || near <= 0f)
        {
            problem = Add(problem, $"near clip {near} (reset to 0.05)");
            kinds += "near;";
            camera.Near = near = 0.05f;
        }
        if (!float.IsFinite(far) || far <= near)
        {
            problem = Add(problem, $"far clip {far} not beyond near {near} (reset to {near + 100f})");
            kinds += "far;";
            camera.Far = far = near + 100f;
        }
        if (camera.Projection == Camera3D.ProjectionType.Perspective && !float.IsFinite(camera.Fov))
        {
            problem = Add(problem, $"fov {camera.Fov} (reset to 75)");
            kinds += "fov;";
            camera.Fov = 75f;
        }
        var global = camera.GlobalTransform;
        if (!global.IsFinite())
        {
            problem = Add(problem, "non-finite global transform" + DescribeAncestors(camera));
            kinds += "transform;";
        }
        else
        {
            float det = global.Basis.Determinant();
            if (MathF.Abs(det) < 1e-6f)
            {
                problem = Add(problem, $"global basis squashed to det {det:E2}" + DescribeAncestors(camera));
                kinds += "basis;";
            }
        }
        if (problem == null && !FrustumIntersects(camera, viewport, out string why))
        {
            problem = why;
            kinds += "frustum;";
        }
        if (problem != null)
            ReportOnce($"{camera.GetInstanceId()}:{kinds}",
                $"CameraSanity: '{camera.Name}' rendering viewport '{label ?? viewport.Name}' would fail the renderer's frustum test: {problem}");
    }

    // The culler's test in camera space. The planes come straight out of the projection the way the
    // engine extracts them; a rigid camera transform cannot change whether two of them are parallel,
    // and a non-rigid one is caught by the determinant check above.
    private static bool FrustumIntersects(Camera3D camera, Viewport viewport, out string why)
    {
        why = "";
        var p = camera.GetCameraProjection();
        _normals[0] = Normalized(new Vector3(p.X.W + p.X.Z, p.Y.W + p.Y.Z, p.Z.W + p.Z.Z));
        _normals[1] = Normalized(new Vector3(p.X.W - p.X.Z, p.Y.W - p.Y.Z, p.Z.W - p.Z.Z));
        _normals[2] = Normalized(new Vector3(p.X.W + p.X.X, p.Y.W + p.Y.X, p.Z.W + p.Z.X));
        _normals[3] = Normalized(new Vector3(p.X.W - p.X.Y, p.Y.W - p.Y.Y, p.Z.W - p.Z.Y));
        _normals[4] = Normalized(new Vector3(p.X.W - p.X.X, p.Y.W - p.Y.X, p.Z.W - p.Z.X));
        _normals[5] = Normalized(new Vector3(p.X.W + p.X.Y, p.Y.W + p.Y.Y, p.Z.W + p.Z.Y));
        for (int i = 0; i < Triples.Length; i++)
        {
            var (a, b, c) = Triples[i];
            float denom = _normals[a].Cross(_normals[b]).Dot(_normals[c]);
            if (float.IsNaN(denom) || MathF.Abs(denom) >= ParallelEpsilon)
                continue;
            var size = viewport.GetVisibleRect().Size;
            why = $"planes {PlaneNames[a]}/{PlaneNames[b]}/{PlaneNames[c]} nearly parallel (triple product {denom:E2}); "
                + $"mode={camera.Projection} fov={camera.Fov:F2} size={camera.Size:F4} near={camera.Near:F4} far={camera.Far:F1} "
                + $"keepAspect={camera.KeepAspect} viewport={size.X:F0}x{size.Y:F0} scale3d={viewport.Scaling3DScale:F2}";
            return false;
        }
        return true;
    }

    private static Vector3 Normalized(Vector3 v)
    {
        float length = v.Length();
        return length > 0f && float.IsFinite(length) ? v / length : Vector3.Zero;
    }

    // Which ancestor collapsed the camera: the first node up the chain with a scale axis at or near
    // zero, or a transform that is not a number.
    private static string DescribeAncestors(Node3D camera)
    {
        string trail = "";
        Node? node = camera;
        int hops = 0;
        while (node != null && hops++ < 64)
        {
            if (node is Node3D spatial)
            {
                var scale = spatial.Scale;
                bool bad = !scale.IsFinite() || MathF.Abs(scale.X) < 1e-4f || MathF.Abs(scale.Y) < 1e-4f || MathF.Abs(scale.Z) < 1e-4f
                    || !spatial.Transform.IsFinite();
                if (bad)
                    trail += $"; '{spatial.Name}' scale=({scale.X:E1},{scale.Y:E1},{scale.Z:E1})";
            }
            node = node.GetParent();
        }
        return trail.Length > 0 ? " via" + trail : " (no ancestor with a collapsed scale)";
    }

    private static string Add(string? problem, string more)
        => problem == null ? more : problem + "; " + more;

    private static void ReportOnce(string key, string message)
    {
        if (_reported.Add(key))
            LumoraLogger.Warn(message);
    }
}

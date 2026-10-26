// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Godot.Helpers;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Rectangular mirrors use a glass-aligned off-axis frustum for each eye and surface UVs.
// Generic CameraPortal surfaces retain screen projection and oblique clipping.
[ImplementableHook(typeof(CameraPortal), typeof(Lumora.Core.Components.Mirror))]
public sealed partial class CameraPortalHook : ComponentHook<CameraPortal>
{
    private const int MaxViews = 2;
    private const int MinSide = 16;
    // The surface is pulled this far toward the capture camera, so geometry resting on the glass is
    // reflected whole rather than cut along the line it touches.
    private const float ClipSlack = 0.0005f;
    private const int ShadowAtlas = 1024;
    // A rectangular mirror's capture follows how big its glass is on screen: this much finer than the
    // pixels it covers, in steps of this many, never under the floor.
    private const float MirrorOversample = 1.25f;
    private const int MirrorStep = 128;
    private const int MirrorFloor = 256;
    private const int MirrorFirstPicture = 512;
    private readonly Vector2I[] _mirrorSizes = new Vector2I[MaxViews];
    private readonly bool[] _everRendered = new bool[MaxViews];

    private PortalProcessNode _processNode = null!;
    private readonly SubViewport?[] _viewports = new SubViewport?[MaxViews];
    private readonly Camera3D?[] _cameras = new Camera3D?[MaxViews];
    // One projection per capture, handed to the renderer every frame; a Godot array made per call was a
    // finalizer job per eye per frame.
    private readonly global::Godot.Collections.Array<Projection>?[] _projections = new global::Godot.Collections.Array<Projection>?[MaxViews];
    private static readonly global::Godot.Collections.Array<Transform3D> NoOffset = new() { Transform3D.Identity };
    private static readonly List<SubViewport> _liveViewports = new();
    private static readonly object _liveViewportLock = new();
    public static void CollectLiveViewports(List<SubViewport> into)
    {
        lock (_liveViewportLock)
        {
            for (int i = _liveViewports.Count - 1; i >= 0; i--)
            {
                var viewport = _liveViewports[i];
                if (viewport == null || !GodotObject.IsInstanceValid(viewport))
                {
                    _liveViewports.RemoveAt(i);
                    continue;
                }
                into.Add(viewport);
            }
        }
    }
    private RenderTextureHook? _boundTexture;
    private global::Godot.Environment? _clearEnvironment;
    private int _publishedViews = -1;
    private bool _parked = true;
    private bool _loggedNoViewer;
    private Lumora.Core.Assets.RenderTexture? _boundAsset;
    private Texture2D? _offTexture;
    private global::Godot.Environment? _sourceEnvironment;
    private global::Godot.Environment? _captureEnvironment;
    private bool _environmentDirty = true;
    private bool _usersOnlyEnvironment;
    private MirrorCaptureScene? _captureScene;
    private readonly List<User> _captureUsers = new();
    private readonly List<Node3D> _captureUserRoots = new();

    public override void Initialize()
    {
        base.Initialize();
        _processNode = new PortalProcessNode(this) { Name = "CameraPortal" };
        attachedNode.AddChild(_processNode);
    }

    // Everything here is per frame; a changed member is picked up on the next tick.
    public override void ApplyChanges() { }

    public override void Destroy(bool destroyingWorld)
    {
        // Viewports are hosted outside the owning world, so world teardown must free them too.
        {
            ReleaseTexture();
            for (int i = 0; i < MaxViews; i++)
            {
                var viewport = _viewports[i];
                if (viewport != null && GodotObject.IsInstanceValid(viewport))
                    viewport.QueueFree();
                _viewports[i] = null;
                _cameras[i] = null;
                _projections[i] = null;
            }
            _clearEnvironment?.Dispose();
            _clearEnvironment = null;
            if (_sourceEnvironment != null && GodotObject.IsInstanceValid(_sourceEnvironment))
                _sourceEnvironment.Changed -= EnvironmentChanged;
            _sourceEnvironment = null;
            _captureEnvironment?.Dispose();
            _captureEnvironment = null;
            _offTexture?.Dispose();
            _offTexture = null;
            _captureScene?.Dispose();
            _captureScene = null;
            if (_processNode != null && GodotObject.IsInstanceValid(_processNode))
                _processNode.QueueFree();
        }
        _processNode = null!;
        base.Destroy(destroyingWorld);
    }

    internal void Tick()
    {
        var owner = Owner;
        if (owner == null || owner.IsDestroyed || owner.Slot == null)
        {
            Park();
            return;
        }

        var textureHook = owner.ReflectionTexture.Target?.Asset?.Hook as RenderTextureHook;
        if (!ReferenceEquals(textureHook, _boundTexture))
        {
            ReleaseTexture();
            _boundTexture = textureHook;
            _boundAsset = owner.ReflectionTexture.Target?.Asset;
            _publishedViews = -1;
            _parked = false;
        }

        if (!owner.Enabled || !owner.LocalRenderingEnabled || textureHook == null || !owner.Slot.IsActive
            || !owner.World.IsFocused)
        {
            Park();
            return;
        }

        var viewer = RenderHelper.ViewerCamera?.Invoke() ?? _processNode.GetViewport()?.GetCamera3D();
        if (viewer == null || !GodotObject.IsInstanceValid(viewer) || !viewer.IsInsideTree())
        {
            if (!_loggedNoViewer)
            {
                _loggedNoViewer = true;
                LumoraLogger.Warn($"CameraPortal '{owner.Slot.SlotName.Value}': no viewer camera to render from.");
            }
            Park();
            return;
        }
        var viewerViewport = viewer.GetViewport();
        if (viewerViewport == null)
        {
            Park();
            return;
        }

        // The plane, off the node this hook owns: current for this frame, whatever moved the slot.
        var portalXf = attachedNode.GlobalTransform;
        var localNormal = owner.PlaneNormal.Value;
        if (localNormal.LengthSquared < 1e-8f)
            localNormal = Lumora.Core.Math.float3.Backward;
        var planeNormal = (portalXf.Basis.Inverse().Transposed()
            * new Vector3(localNormal.x, localNormal.y, localNormal.z)).Normalized();
        var planePos = portalXf.Origin + planeNormal * owner.PlaneOffset.Value;
        var glassPos = planePos;

        bool portal = owner.RenderMode.Value == CameraPortal.Mode.Portal;
        bool rectangularMirror = !portal && owner is Lumora.Core.Components.Mirror;
        var portalMap = Transform3D.Identity;
        if (portal)
        {
            var target = owner.PortalTarget.Target;
            if (target == null || target.IsDestroyed)
            {
                Park();
                return;
            }
            var tp = target.GlobalPosition;
            var tr = target.GlobalRotation;
            var targetXf = new Transform3D(new Basis(new Quaternion(tr.x, tr.y, tr.z, tr.w)), new Vector3(tp.x, tp.y, tp.z));
            var here = new Transform3D(portalXf.Basis.Orthonormalized(), portalXf.Origin);
            portalMap = targetXf * here.AffineInverse();
            // The clip plane for a portal is the far end: what is between the mapped camera and the
            // target's surface is behind the target, not in the picture.
            planeNormal = (targetXf.Basis * new Vector3(localNormal.x, localNormal.y, localNormal.z)).Normalized();
            planePos = targetXf.Origin + planeNormal * owner.PlaneOffset.Value;
        }

        var viewerPos = viewer.GlobalTransform.Origin;
        if (owner.ActivationDistance.Value > 0f
            && viewerPos.DistanceTo(portal ? glassPos : planePos) > owner.ActivationDistance.Value)
        {
            Park();
            return;
        }
        // A bare portal in mirror mode reflects on whichever side the viewer stands. Package mirrors come with
        // their normal either way round: the default one points it out of the glass, the canvas one on the
        // camera turned half a turn, into the wall. Our own Mirror is one-sided glass and keeps the cut below.
        if (!portal && !rectangularMirror && (viewerPos - planePos).Dot(planeNormal) <= 0f)
        {
            planeNormal = -planeNormal;
            planePos = portalXf.Origin + planeNormal * owner.PlaneOffset.Value;
            glassPos = planePos;
        }
        if (!portal && (viewerPos - planePos).Dot(planeNormal) <= 0f)
        {
            // Behind the glass there is nothing to reflect, and nothing to pay for.
            Park();
            return;
        }

        var host = WorldManagerHook.Instance?.Root;
        if (host == null || !GodotObject.IsInstanceValid(host))
        {
            Park();
            return;
        }

        var xr = XRServer.PrimaryInterface;
        bool stereo = viewerViewport.UseXR && xr != null && xr.IsInitialized() && xr.GetViewCount() >= 2;
        int views = stereo ? 2 : 1;

        // Match XRCamera3D's tracker and camera transform, including recentering and user scale.
        // The deprecated GetTransformForView reconstructs a different head pose in newer XR drivers.
        var originXf = viewer.GetParent() is Node3D origin ? origin.GlobalTransform : Transform3D.Identity;
        var viewerSize = ViewerPixels(viewerViewport);
        float viewerAspect = viewerSize.Y > 0f ? viewerSize.X / viewerSize.Y : 1f;

        // Mirrors allocate the selected longest side. Generic screen-projected portals retain a
        // ceiling on the eye's pixel dimensions.
        var asset = owner.ReflectionTexture.Target?.Asset;
        int ceiling = System.Math.Clamp(
            owner.LocalResolution > 0 ? owner.LocalResolution
                : owner is Lumora.Core.Components.Mirror mirrorOwner ? mirrorOwner.Resolution.Value
                : System.Math.Max(asset?.RenderWidth ?? 2048, asset?.RenderHeight ?? 2048),
            Lumora.Core.Components.Mirror.MinimumResolution, Lumora.Core.Components.Mirror.MaximumResolution);
        float renderScale = viewerViewport.Scaling3DScale > 0f && float.IsFinite(viewerViewport.Scaling3DScale)
            ? viewerViewport.Scaling3DScale : 1f;
        Vector2 eyePixels = (stereo ? xr!.GetRenderTargetSize() : viewerSize) * renderScale;
        var surfacePixels = new Vector2(owner.SurfaceSize.Value.x * portalXf.Basis.X.Length(),
            owner.SurfaceSize.Value.y * portalXf.Basis.Y.Length());
        var size = rectangularMirror ? MirrorCaptureMath.TextureSize(surfacePixels, ceiling)
            : FitResolution(eyePixels, ceiling);

        float near = System.Math.Max(0.001f, owner.OverrideNearClip.Value ?? viewer.Near);
        float far = System.Math.Max(near + 0.01f, owner.LocalRenderDistance ?? owner.OverrideFarClip.Value ?? viewer.Far);
        var glassSize = owner.SurfaceSize.Value;
        bool hasGlass = glassSize.x > 0f && glassSize.y > 0f;
        var glass = new Transform3D(portalXf.Basis, glassPos);
        // A mirror keeps what is in front of its glass; a portal keeps what lies beyond its target.
        var keepNormal = portal ? -planeNormal : planeNormal;

        var eyeOffsets = stereo && viewer is XRCamera3D xrCamera
            ? XRServer.GetCameraOffsets(xrCamera.Tracker) : null;
        var eyeProjections = stereo && viewer is XRCamera3D xrLens
            ? XRServer.GetCameraProjections(xrLens.Tracker, viewerAspect, near, far) : null;

        var captureWorld = host.GetWorld3D();
        if (owner.ShadowsDisabled || owner.LightsDisabled || owner.LocalUsersOnly)
        {
            _captureScene ??= new MirrorCaptureScene();
            _captureUserRoots.Clear();
            if (owner.LocalUsersOnly)
            {
                owner.World.GetAllUsers(_captureUsers);
                foreach (var user in _captureUsers)
                    if (user.Root?.Slot?.Hook is SlotHook hook && hook.GeneratedNode3D is { } root)
                        _captureUserRoots.Add(root);
            }
            _captureScene.Update((owner.World.Hook as WorldHook)?.WorldRoot ?? host, _captureUserRoots,
                owner.LocalUsersOnly, owner.ShadowsDisabled || owner.LightsDisabled, owner.LightsDisabled);
            captureWorld = _captureScene.World;
        }
        else if (_captureScene != null)
        {
            // Detach both eyes before releasing the private scenario.
            foreach (var oldViewport in _viewports)
                if (oldViewport != null && GodotObject.IsInstanceValid(oldViewport)) oldViewport.World3D = captureWorld;
            _captureScene.Dispose();
            _captureScene = null;
        }

        int renderedViews = 0;
        var renderedSize = size;

        for (int v = 0; v < views; v++)
        {
            Transform3D eye;
            Projection projection;
            if (stereo)
            {
                if (eyeOffsets?.Count >= views && eyeProjections?.Count >= views)
                {
                    eye = viewer.GetCameraTransform() * eyeOffsets[v];
                    projection = eyeProjections[v];
                }
                else
                {
#pragma warning disable CS0618 // Fallback for XR interfaces without camera-tracker support.
                    eye = xr!.GetTransformForView((uint)v, originXf);
                    projection = xr.GetProjectionForView((uint)v, viewerAspect, near, far);
#pragma warning restore CS0618
                }
            }
            else
            {
                eye = viewer.GetCameraTransform();
                projection = WithDepthRange(viewer.GetCameraProjection(), near, far);
            }

            // None of the glass is in this eye's view: keep the last picture and skip the render. A mirror that
            // has no picture yet takes one small one first, so a camera or a photo pointed at glass nobody has
            // faced finds a reflection rather than black.
            bool inView = !hasGlass || SurfaceInView(eye, projection, glass, new Vector2(glassSize.x, glassSize.y));
            if (!inView && _everRendered[v])
            {
                SuspendView(v);
                continue;
            }

            var viewSize = size;
            Transform3D cam;
            if (rectangularMirror)
            {
                viewSize = inView
                    ? FitMirror(v, GlassPixels(eye, projection, glass, new Vector2(glassSize.x, glassSize.y), eyePixels),
                        surfacePixels, ceiling)
                    : MirrorCaptureMath.TextureSize(surfacePixels, System.Math.Min(ceiling, MirrorFirstPicture));
                if (!MirrorCaptureMath.TryFrame(eye.Origin, glass, new Vector2(glassSize.x, glassSize.y),
                    far, out cam, out projection))
                {
                    SuspendView(v);
                    continue;
                }
            }
            else if (portal)
            {
                cam = portalMap * eye;
            }
            else
            {
                // A camera cannot carry a reflection (its basis would be left-handed), so it is turned
                // round instead and its projection mirrored left to right; the material flips the
                // picture back when it samples it.
                var pos = ReflectPoint(eye.Origin, planeNormal, planePos);
                var forward = ReflectDirection(-eye.Basis.Z, planeNormal);
                var up = ReflectDirection(eye.Basis.Y, planeNormal);
                cam = new Transform3D(Basis.LookingAt(forward, up), pos);
                projection = MirrorProjection(projection);
            }

            // The surface in this camera's view space, facing away from it. A camera on the kept side of
            // its own plane is looking at the back of the glass and has nothing to show.
            if (!rectangularMirror)
            {
                var toView = cam.AffineInverse();
                var clipNormal = (toView.Basis * keepNormal).Normalized();
                float clipDistance = clipNormal.Dot(toView * planePos) - ClipSlack;
                if (!(clipDistance > 1e-5f) || !clipNormal.IsFinite())
                {
                    SuspendView(v);
                    continue;
                }
                projection = ObliqueNearPlane(projection, clipNormal, clipDistance);
            }
            if (!IsFinite(projection))
            {
                SuspendView(v);
                continue;
            }

            var viewport = EnsureViewport(v, host);
            var camera = _cameras[v]!;
            if (!ReferenceEquals(viewport.World3D, captureWorld)) viewport.World3D = captureWorld;
            if (viewport.Size != viewSize)
                viewport.Size = viewSize;
            if (rectangularMirror && (renderedViews == 0 || viewSize.X * viewSize.Y > renderedSize.X * renderedSize.Y))
                renderedSize = viewSize;
            ConfigureCapture(camera, viewport, owner, viewerViewport);
            camera.GlobalTransform = cam;
            // Last: a change to the camera's own lens settings resets the projection to the node's.
            var projections = _projections[v] ??= new global::Godot.Collections.Array<Projection> { Projection.Identity };
            projections[0] = projection;
            RenderingServer.CameraSetXRProjections(camera.GetCameraRid(), projections, NoOffset);
            _everRendered[v] = true;
            renderedViews++;
        }

        // An eye that is not in use this frame stops rendering rather than showing a stale picture.
        for (int v = views; v < MaxViews; v++)
            SuspendView(v);

        _parked = false;
        owner.LocalRenderedResolution = renderedViews > 0 ? new Lumora.Core.Math.int2(renderedSize.X, renderedSize.Y) : default;
        owner.LocalRenderedViews = renderedViews;
        Publish(views);
    }

    // The viewer's real pixels. Under canvas stretch the visible rect is the design size, so a mirror
    // sized from it came out 1152 wide on any window and was stretched onto the glass. -xlinka
    private static Vector2 ViewerPixels(Viewport viewport) => viewport switch
    {
        Window window => new Vector2(window.Size.X, window.Size.Y),
        SubViewport sub => new Vector2(sub.Size.X, sub.Size.Y),
        _ => viewport.GetVisibleRect().Size,
    };

    private static Vector2I FitResolution(Vector2 eyePixels, int ceiling)
    {
        if (!(eyePixels.X >= 1f) || !(eyePixels.Y >= 1f) || !eyePixels.IsFinite())
            eyePixels = new Vector2(1024f, 1024f);
        float longSide = System.MathF.Max(eyePixels.X, eyePixels.Y);
        float scale = longSide > ceiling ? ceiling / longSide : 1f;
        return new Vector2I(
            System.Math.Max(MinSide, (int)System.MathF.Round(eyePixels.X * scale)),
            System.Math.Max(MinSide, (int)System.MathF.Round(eyePixels.Y * scale)));
    }

    // The viewer's perspective with its depth range swapped. The near plane is replaced by the surface
    // later; the far plane is where the reflection stops.
    private static Projection WithDepthRange(Projection projection, float near, float far)
    {
        float depth = far - near;
        projection.Z.Z = -(far + near) / depth;
        projection.W.Z = -2f * near * far / depth;
        return projection;
    }

    // The projection seen in a mirror: x in and x out both negated, which keeps every off-axis and skew
    // term of a headset's eye frustum and only swaps its left and right.
    private static Projection MirrorProjection(Projection p)
    {
        p.Y.X = -p.Y.X;
        p.Z.X = -p.Z.X;
        p.W.X = -p.W.X;
        p.X.Y = -p.X.Y;
        p.X.Z = -p.X.Z;
        p.X.W = -p.X.W;
        return p;
    }

    // Replaces the near plane of a perspective projection with an arbitrary plane in view space (normal
    // facing the kept side, camera behind it), keeping the far plane as close to where it was as the
    // tilt allows: the clip-space corner opposite the plane stays on the far plane.
    private static Projection ObliqueNearPlane(Projection p, Vector3 normal, float distance)
    {
        var plane = new Vector4(normal.X, normal.Y, normal.Z, -distance);
        var corner = new Vector4(
            (System.MathF.Sign(plane.X) + p.Z.X) / p.X.X,
            (System.MathF.Sign(plane.Y) + p.Z.Y) / p.Y.Y,
            -1f,
            (1f + p.Z.Z) / p.W.Z);
        var scaled = plane * (2f / plane.Dot(corner));
        p.X.Z = scaled.X;
        p.Y.Z = scaled.Y;
        p.Z.Z = scaled.Z + 1f;
        p.W.Z = scaled.W;
        return p;
    }

    private static bool IsFinite(in Projection p)
        => p.X.IsFinite() && p.Y.IsFinite() && p.Z.IsFinite() && p.W.IsFinite();

    private void ConfigureCapture(Camera3D camera, SubViewport viewport, CameraPortal owner, Viewport viewerViewport)
    {
        // Make the capture a render dependency of the eye viewport, including after a VR mode switch.
        // Otherwise sibling viewport ordering can make the glass sample the previous frame.
        RenderingServer.ViewportSetParentViewport(viewport.GetViewportRid(), viewerViewport.GetViewportRid());
        camera.CullMask = RenderHelper.WithoutFilmedAlone(unchecked((uint)(RenderHelper.PUBLIC_RENDER_MASK
            & ~RenderHelper.FREECAM_INDICATOR_LAYER & ~RenderHelper.MIRROR_LAYER)));
        var viewer = RenderHelper.ViewerCamera?.Invoke();
        var source = owner.OverrideClear.Value ? ClearEnvironment(owner)
            : viewer?.Environment ?? viewer?.GetWorld3D()?.Environment;
        if (_usersOnlyEnvironment != owner.LocalUsersOnly)
        {
            _usersOnlyEnvironment = owner.LocalUsersOnly;
            _environmentDirty = true;
        }
        if (!ReferenceEquals(source, _sourceEnvironment))
        {
            if (_sourceEnvironment != null && GodotObject.IsInstanceValid(_sourceEnvironment))
                _sourceEnvironment.Changed -= EnvironmentChanged;
            _sourceEnvironment = source;
            if (source != null) source.Changed += EnvironmentChanged;
            _environmentDirty = true;
        }
        if (_environmentDirty)
        {
            _captureEnvironment?.Dispose();
            _captureEnvironment = source?.Duplicate() as global::Godot.Environment ?? new global::Godot.Environment();
            // Keep scene lighting in linear HDR. The eye viewport applies the user's tone map and
            // bloom once, after the mirror is composited into the world.
            _captureEnvironment.TonemapMode = global::Godot.Environment.ToneMapper.Linear;
            _captureEnvironment.TonemapExposure = 1f;
            _captureEnvironment.GlowEnabled = false;
            _captureEnvironment.AdjustmentEnabled = false;
            _captureEnvironment.SsrEnabled = false;
            if (owner.LocalUsersOnly)
            {
                _captureEnvironment.BackgroundMode = global::Godot.Environment.BGMode.Color;
                _captureEnvironment.BackgroundColor = new Color(0.018f, 0.025f, 0.04f);
                _captureEnvironment.FogEnabled = false;
                _captureEnvironment.VolumetricFogEnabled = false;
                _captureEnvironment.SsaoEnabled = false;
                _captureEnvironment.SsilEnabled = false;
                _captureEnvironment.SdfgiEnabled = false;
            }
            _environmentDirty = false;
        }
        if (!ReferenceEquals(camera.Environment, _captureEnvironment))
            camera.Environment = _captureEnvironment;
        if (_captureScene != null && !ReferenceEquals(_captureScene.World.Environment, _captureEnvironment))
            _captureScene.World.Environment = _captureEnvironment;
        // Match the viewer's edge antialiasing; the reflection shader filters surface minification.
        if (viewport.Msaa3D != viewerViewport.Msaa3D)
            viewport.Msaa3D = viewerViewport.Msaa3D;
        viewport.MeshLodThreshold = EngineSettings.MeshLodThreshold;
        viewport.PositionalShadowAtlasSize = owner.ShadowsDisabled || owner.LightsDisabled ? 0 : ShadowAtlas;
        viewport.DebugDraw = owner.LightsDisabled ? Viewport.DebugDrawEnum.Unshaded : Viewport.DebugDrawEnum.Disabled;
        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        // This view shows the wearer's own body, so the lights have to reach it this frame.
        ShadowBudget.FilmLocalHead();
    }

    private void EnvironmentChanged() => _environmentDirty = true;

    private static Vector3 ReflectPoint(Vector3 point, Vector3 normal, Vector3 planePoint)
        => point - normal * (2f * (point - planePoint).Dot(normal));

    private static Vector3 ReflectDirection(Vector3 direction, Vector3 normal)
        => direction - normal * (2f * direction.Dot(normal));

    private SubViewport EnsureViewport(int index, Node3D host)
    {
        var viewport = _viewports[index];
        if (viewport != null && GodotObject.IsInstanceValid(viewport))
            return viewport;

        viewport = new SubViewport
        {
            Name = index == 0 ? "CameraPortalLeft" : "CameraPortalRight",
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            OwnWorld3D = false,
            HandleInputLocally = false,
            Disable3D = false,
            // The reflected camera's frustum changes with every step the viewer takes; mesh detail must
            // not pop with it.
            MeshLodThreshold = EngineSettings.MeshLodThreshold,
            Scaling3DScale = 1f,
            UseTaa = false,
            PositionalShadowAtlas16Bits = false,
            UseOcclusionCulling = false,
            TransparentBg = false,
            UseHdr2D = true,
        };
        var camera = new Camera3D
        {
            Name = "PortalCamera",
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
        };
        viewport.AddChild(camera);
        host.AddChild(viewport);
        var world = host.GetWorld3D();
        if (world != null)
            viewport.World3D = world;
        camera.Current = true;

        _viewports[index] = viewport;
        _cameras[index] = camera;
        lock (_liveViewportLock)
            _liveViewports.Add(viewport);
        _publishedViews = -1;
        return viewport;
    }

    // Whether any of the glass lies inside this eye's view. A corner behind the eye counts as in view:
    // that close, rendering is the safe answer.
    // A corner behind the eye leaves the projected box unknowable, so that counts as in view, unless all four
    // are behind it: then the whole glass is, and the grid's mirror sat behind everyone at spawn rendering
    // 4096 across every frame for nobody. -xlinka
    private static bool SurfaceInView(in Transform3D eye, in Projection projection, in Transform3D surface, Vector2 size)
    {
        var toView = eye.AffineInverse();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        int behind = 0;
        for (int i = 0; i < 4; i++)
        {
            float sx = (i & 1) == 0 ? -0.5f : 0.5f;
            float sy = (i & 2) == 0 ? -0.5f : 0.5f;
            var corner = surface.Origin + surface.Basis.X * (size.X * sx) + surface.Basis.Y * (size.Y * sy);
            var view = toView * corner;
            var clip = projection * new Vector4(view.X, view.Y, view.Z, 1f);
            if (!(clip.W > 1e-5f))
            {
                behind++;
                continue;
            }
            float nx = clip.X / clip.W;
            float ny = clip.Y / clip.W;
            if (!float.IsFinite(nx) || !float.IsFinite(ny))
                return true;
            minX = System.MathF.Min(minX, nx);
            maxX = System.MathF.Max(maxX, nx);
            minY = System.MathF.Min(minY, ny);
            maxY = System.MathF.Max(maxY, ny);
        }
        if (behind == 4)
            return false;
        if (behind > 0)
            return true;
        return maxX >= -1f && minX <= 1f && maxY >= -1f && minY <= 1f;
    }

    // How many of this eye's pixels the whole glass spans along its longer side, or -1 with a corner behind
    // the eye (standing at the glass, where it can fill the view).
    private static float GlassPixels(in Transform3D eye, in Projection projection, in Transform3D surface, Vector2 size,
        Vector2 eyePixels)
    {
        var toView = eye.AffineInverse();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            float sx = (i & 1) == 0 ? -0.5f : 0.5f;
            float sy = (i & 2) == 0 ? -0.5f : 0.5f;
            var corner = surface.Origin + surface.Basis.X * (size.X * sx) + surface.Basis.Y * (size.Y * sy);
            var view = toView * corner;
            var clip = projection * new Vector4(view.X, view.Y, view.Z, 1f);
            if (!(clip.W > 1e-5f))
                return -1f;
            float nx = clip.X / clip.W;
            float ny = clip.Y / clip.W;
            if (!float.IsFinite(nx) || !float.IsFinite(ny))
                return -1f;
            minX = System.MathF.Min(minX, nx);
            maxX = System.MathF.Max(maxX, nx);
            minY = System.MathF.Min(minY, ny);
            maxY = System.MathF.Max(maxY, ny);
        }
        return System.MathF.Max((maxX - minX) * 0.5f * eyePixels.X, (maxY - minY) * 0.5f * eyePixels.Y);
    }

    // A mirror's picture can only be as sharp as its glass is big on screen. Across a room the glass covers a
    // few hundred pixels, and capturing it at the full resolution there was the most expensive thing in the
    // frame (4096 across for the grid's mirror at spawn, 2.5 ms of GPU). So the capture follows the glass's
    // size on screen, never past the mirror's own resolution. It grows at once and only shrinks once that
    // saves a good share, in steps, so walking about does not reallocate it every frame. -xlinka
    private Vector2I FitMirror(int view, float glassPixels, Vector2 surfaceSize, int ceiling)
    {
        int target = glassPixels < 0f
            ? ceiling
            : System.Math.Min(ceiling, (int)(System.MathF.Ceiling(glassPixels * MirrorOversample / MirrorStep) * MirrorStep));
        target = System.Math.Clamp(target, System.Math.Min(MirrorFloor, ceiling), ceiling);
        var last = _mirrorSizes[view];
        int lastLong = System.Math.Max(last.X, last.Y);
        if (lastLong > 0 && target < lastLong && target > lastLong * 0.7f)
            return last;
        var size = MirrorCaptureMath.TextureSize(surfaceSize, target);
        _mirrorSizes[view] = size;
        return size;
    }

    private void SuspendView(int view)
    {
        var viewport = _viewports[view];
        if (viewport != null && GodotObject.IsInstanceValid(viewport))
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    // The pictures reach the material through the render texture asset, which every material bound
    // through the ordinary path. Re-notifying it is what rebinds them, and that only has to happen when
    // the set of pictures changes, not every frame. -xlinka
    private void Publish(int views)
    {
        if (_boundTexture == null || _publishedViews == views)
            return;
        var left = _viewports[0];
        var right = views > 1 ? _viewports[1] : null;
        _boundTexture.SetPortalTextures(
            left != null && GodotObject.IsInstanceValid(left) ? left.GetTexture() : null,
            right != null && GodotObject.IsInstanceValid(right) ? right.GetTexture() : null);
        _publishedViews = views;
        _boundAsset?.NotifyBackingReady();
    }

    private global::Godot.Environment ClearEnvironment(CameraPortal owner)
    {
        var env = _clearEnvironment ??= new global::Godot.Environment();
        switch (owner.Clear.Value)
        {
            case ClearMode.Skybox:
                env.BackgroundMode = global::Godot.Environment.BGMode.Sky;
                env.Sky = SkyEnvironment.CurrentSky;
                break;
            case ClearMode.Color:
                env.BackgroundMode = global::Godot.Environment.BGMode.Color;
                var c = owner.ClearColor.Value;
                env.BackgroundColor = new Color(c.r, c.g, c.b, c.a);
                break;
            default:
                env.BackgroundMode = global::Godot.Environment.BGMode.Color;
                env.BackgroundColor = new Color(0f, 0f, 0f, 0f);
                break;
        }
        SkyEnvironment.WritePost(env);
        return env;
    }

    private void Park()
    {
        if (Owner != null)
        {
            Owner.LocalRenderedResolution = default;
            Owner.LocalRenderedViews = 0;
        }
        if (_parked)
            return;
        _parked = true;
        if (_boundTexture != null)
        {
            if (_offTexture == null)
            {
                using var offImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
                offImage.Fill(new Color(0.018f, 0.025f, 0.04f));
                _offTexture = ImageTexture.CreateFromImage(offImage);
            }
            _boundTexture.SetPortalTextures(_offTexture, null);
            _boundAsset?.NotifyBackingReady();
            _publishedViews = -1;
        }
        for (int i = 0; i < MaxViews; i++)
            SuspendView(i);
    }

    private void ReleaseTexture()
    {
        if (_boundTexture == null)
            return;
        _boundTexture.SetPortalTextures(null, null);
        _boundAsset?.NotifyBackingReady();
        _boundTexture = null;
        _boundAsset = null;
        _publishedViews = -1;
    }

    private sealed partial class PortalProcessNode : Node3D
    {
        private readonly CameraPortalHook _hook;
        public PortalProcessNode(CameraPortalHook hook) => _hook = hook;
        public PortalProcessNode() => _hook = null!;

        public override void _EnterTree() => RenderingServer.FramePreDraw += UpdateView;
        public override void _ExitTree() => RenderingServer.FramePreDraw -= UpdateView;
        private void UpdateView() => _hook?.Tick();
    }
}

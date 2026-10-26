// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(RenderTexture))]
public class RenderTextureHook : AssetHook, IRenderTextureAssetHook, IStereoGodotTexture
{
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;

    private bool _renderEnabled = true;
    private int _width = 1024;
    private int _height = 1024;
    private uint _cullMask;
    private Color _clearColor;
    private Vector3 _cameraPosition;
    private Quaternion _cameraRotation = Quaternion.Identity;
    private float _orthoSize = 1f;

    // Set while a Camera component is filming into this texture. It replaces the provider's fixed
    // orthographic framing wholesale and flips the viewport from render-on-change to continuous, because
    // a camera on a slot in a live world has no way to tell us when its picture changed. -xlinka
    private Lumora.Core.Assets.RenderCameraParameters? _cameraOverride;
    private global::Godot.Environment? _cameraEnvironment;
    private global::Godot.Environment? _uiEnvironment;

    // Every live offscreen viewport, so the profiler can cost them individually instead of lumping them
    // into one global draw-call number. The dash, every mirror and every camera each own one, and "the
    // frame is expensive" is a useless answer when four viewports are rendering the same world.
    //
    // A registry rather than a scene-tree walk: the tree under the world root runs to tens of thousands
    // of nodes and FindChildren over it four times a second would cost more than the thing it measures.
    // -xlinka
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

    // A CameraPortal rendering into this texture brings its own viewports - one per eye - and hands
    // their textures here. While it does, this hook's own viewport stays parked: the asset is the same
    // asset every material bound, it just draws from somewhere else. The portal owns those pictures
    // and clears them when it goes. -xlinka
    private Texture2D? _portalLeft;
    private Texture2D? _portalRight;

    public Texture2D? GodotTexture2D => _portalLeft
        ?? (_viewport != null && GodotObject.IsInstanceValid(_viewport) ? _viewport.GetTexture() : null);

    public Texture2D? RightEyeTexture2D => _portalRight;

    // The viewport it films into, for showing on the desktop window.
    public SubViewport? FilmViewport => _viewport != null && GodotObject.IsInstanceValid(_viewport) ? _viewport : null;

    public bool IsValid => _portalLeft != null
        || (_viewport != null && GodotObject.IsInstanceValid(_viewport) && _viewport.IsInsideTree());

    public void SetPortalTextures(Texture2D? left, Texture2D? right)
    {
        _portalLeft = left;
        _portalRight = right;
        if (_viewport == null || !GodotObject.IsInstanceValid(_viewport))
            return;
        ApplySize();
        _viewport.RenderTargetUpdateMode = left != null
            ? SubViewport.UpdateMode.Disabled
            : (_renderEnabled ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Disabled);
    }

    // A PICTURE NOBODY DRAWS HOLDS NO MEMORY.
    //
    // The viewport's own target is made at the texture's size the moment the texture exists, whether or not it
    // ever draws there. A mirror draws through viewports of its own and a parked camera draws nothing, so their
    // textures' own targets were dead weight: a dozen 4096 by 2048 mirror textures and a dozen 2048 camera
    // textures in a hidden group came to well over half a gigabyte. Such a target stays at a token size until it
    // has something to draw, and keeps its full size from then on so the last picture survives. -xlinka
    private static readonly Vector2I ParkedSize = new(2, 2);
    private bool _everFilmed;

    private void ApplySize()
    {
        if (_viewport == null || !GodotObject.IsInstanceValid(_viewport))
            return;
        bool parked = _portalLeft != null || (_cameraOverride.HasValue && !_everFilmed);
        var size = parked ? ParkedSize : new Vector2I(_width, _height);
        if (_viewport.Size != size)
            _viewport.Size = size;
    }

    public void Configure(
        int width,
        int height,
        int cullMask,
        Lumora.Core.Math.color clearColor,
        Lumora.Core.Math.float3 cameraPosition,
        Lumora.Core.Math.floatQ cameraRotation,
        float orthographicSize)
    {
        _width = System.Math.Max(1, width);
        _height = System.Math.Max(1, height);
        if (cullMask != 0)
            _cullMask = (uint)cullMask;
        _clearColor = new Color(clearColor.r, clearColor.g, clearColor.b, clearColor.a);
        _cameraPosition = new Vector3(cameraPosition.x, cameraPosition.y, cameraPosition.z);
        _cameraRotation = new Quaternion(cameraRotation.x, cameraRotation.y, cameraRotation.z, cameraRotation.w);
        _orthoSize = orthographicSize <= 0f ? 1f : orthographicSize;

        Callable.From(ApplyOnMainThread).CallDeferred();
    }

    private void ApplyOnMainThread()
    {
        var host = WorldManagerHook.Instance?.Root;
        if (host == null || !GodotObject.IsInstanceValid(host))
        {
            LumoraLogger.Warn("RenderTextureHook: no WorldManager root to host the viewport yet");
            return;
        }

        if (_viewport == null || !GodotObject.IsInstanceValid(_viewport))
        {
            _viewport = new SubViewport
            {
                Name = "RenderTexture",
                // Render ON DEMAND (one frame per change via RequestRender) instead of every frame. The
                // captured scene is just the UI canvas, which only changes when its mesh is rebuilt, so
                // re-rendering this full-res supersampled viewport every frame was pure waste. Disabled keeps
                // the last rendered frame on the texture. -xlinka
                // Its first frame waits a few frames (see FirstFrame): a camera or a mirror may be about to
                // take it over, and a picture of its own would be drawn for nothing.
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                OwnWorld3D = false,
                HandleInputLocally = false,
                Disable3D = false,
            };

            _camera = new Camera3D
            {
                Name = "CaptureCamera",
                Projection = Camera3D.ProjectionType.Orthogonal,
                Near = 0.01f,
                Far = 50f,
                PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
            };
            _viewport.AddChild(_camera);
            host.AddChild(_viewport);

            var world = host.GetWorld3D();
            if (world != null)
                _viewport.World3D = world;

            lock (_liveViewportLock)
                _liveViewports.Add(_viewport);
            FirstFrame();

            // IsValid turns true here; a material that applied before now bound a null.
            asset?.NotifyBackingReady();
        }

        ApplySize();

        if (_cameraOverride.HasValue)
            ApplyCameraOverride(_cameraOverride.Value);
        else
            ApplyProviderFraming();

        _camera.Current = true;
    }

    private void ApplyProviderFraming()
    {
        _viewport.TransparentBg = _clearColor.A < 1f;
        _viewport.Msaa3D = Viewport.Msaa.Disabled;
        _viewport.UseOcclusionCulling = false;
        _viewport.PositionalShadowAtlasSize = 2048;

        if (_cullMask != 0)
            _camera.CullMask = _cullMask;
        // Never the eyes-only band: that is the wearer's headless twin, and this camera is not an eye.
        _camera.CullMask &= ~(uint)Helpers.RenderHelper.EYES_ONLY_LAYER;
        _camera.Projection = Camera3D.ProjectionType.Orthogonal;
        _camera.Size = _orthoSize;
        _camera.Near = 0.01f;
        _camera.Far = 50f;
        _camera.Position = _cameraPosition;
        _camera.Quaternion = _cameraRotation;
        ApplyUiEnvironment();
    }

    // A UI capture films unshaded canvases, which the screen-space passes (occlusion, indirect light,
    // reflections, SDFGI) never touch, yet under the world's own environment every capture paid for all of
    // them at capture size. It gets a copy of that environment with them off: tonemapper, glow, fog and
    // background stay the world's. Copied again for each render, so it follows the world. -xlinka
    private void ApplyUiEnvironment()
    {
        if (_cameraOverride.HasValue || _camera == null || !GodotObject.IsInstanceValid(_camera)
            || _viewport == null || !GodotObject.IsInstanceValid(_viewport))
            return;
        var world = _viewport.FindWorld3D();
        var source = world?.Environment ?? world?.FallbackEnvironment;
        if (source == null)
        {
            _camera.Environment = null;
            _uiEnvironment = null;
            return;
        }
        var env = (global::Godot.Environment)source.Duplicate();
        env.SsaoEnabled = false;
        env.SsilEnabled = false;
        env.SsrEnabled = false;
        env.SdfgiEnabled = false;
        _camera.Environment = env;
        _uiEnvironment = env;
    }

    private void ApplyCameraOverride(Lumora.Core.Assets.RenderCameraParameters p)
    {
        _camera.Projection = p.Perspective ? Camera3D.ProjectionType.Perspective : Camera3D.ProjectionType.Orthogonal;
        _camera.Fov = float.IsFinite(p.FieldOfView) ? Mathf.Clamp(p.FieldOfView, 1f, 179f) : 75f;
        _camera.Size = p.OrthographicSize <= 0f ? 1f : p.OrthographicSize;
        _camera.Near = System.Math.Max(0.001f, p.NearClip);
        _camera.Far = System.Math.Max(_camera.Near + 0.001f, p.FarClip);
        _camera.Position = new Vector3(p.Position.x, p.Position.y, p.Position.z);
        _camera.Quaternion = new Quaternion(p.Rotation.x, p.Rotation.y, p.Rotation.z, p.Rotation.w);
        // Not an eye, so never the eyes-only twin; unless it films as the wearer, which sees what the eyes do:
        // the twin instead of the real head, and not the wearer's own name plate.
        _cameraMask = p.WearerView
            ? (unchecked((uint)p.CullMask) & ~(uint)(Helpers.RenderHelper.LOCAL_HEAD_LAYER | Helpers.RenderHelper.OWN_NAMEPLATE_LAYER))
                | (uint)Helpers.RenderHelper.EYES_ONLY_LAYER
            : unchecked((uint)p.CullMask) & ~(uint)Helpers.RenderHelper.EYES_ONLY_LAYER;
        ApplyFilmedAlone();

        _onWindow = OnWindow;
        MatchWindow(p);
        _viewport.UseOcclusionCulling = p.OcclusionCulling;
        // The only per-viewport shadow switch Godot has. It covers point and spot lights; the sun's
        // shadow belongs to the light, so a scene lit by a directional still casts one.
        _viewport.PositionalShadowAtlasSize = p.RenderShadows ? 2048 : 0;

        bool wantsSky = p.Clear == Lumora.Core.Components.ClearMode.Skybox;
        _viewport.TransparentBg = !wantsSky && p.BackgroundColor.a < 1f;
        _camera.Environment = BuildCameraEnvironment(p);
    }

    // A feed on the desktop window is the picture people see, so it takes the window's own smoothing rather than
    // the camera's cheaper default.
    private bool _onWindow;

    private bool OnWindow => asset != null && ReferenceEquals(Lumora.Core.Photos.DesktopFeed.Shown, asset);

    private void MatchWindow(in Lumora.Core.Assets.RenderCameraParameters p)
    {
        var window = (global::Godot.Engine.GetMainLoop() as SceneTree)?.Root;
        if (_onWindow && window != null)
        {
            _viewport.Msaa3D = window.Msaa3D;
            _viewport.ScreenSpaceAA = window.ScreenSpaceAA;
            _viewport.UseDebanding = window.UseDebanding;
            return;
        }
        _viewport.Msaa3D = p.Msaa ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Disabled;
        _viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
        _viewport.UseDebanding = false;
    }

    // The mask the filming camera asked for, before the bits other cameras film alone come off it; those come and
    // go with the other cameras, so it is checked again every frame while filming (TickFilming).
    private uint _cameraMask;
    private uint _aloneSeen;
    private int _ownAloneSeen;

    private int OwnFilmedAloneBit => (_filmingCamera?.Hook as CameraHook)?.FilmedAloneBit ?? 0;

    private bool FilmedAloneChanged
        => _aloneSeen != Helpers.RenderHelper.FilmedAloneBits || _ownAloneSeen != OwnFilmedAloneBit;

    private void ApplyFilmedAlone()
    {
        _aloneSeen = Helpers.RenderHelper.FilmedAloneBits;
        _ownAloneSeen = OwnFilmedAloneBit;
        if (_camera != null && GodotObject.IsInstanceValid(_camera))
            _camera.CullMask = Helpers.RenderHelper.WithoutFilmedAlone(_cameraMask, _ownAloneSeen);
    }

    // Null means "inherit the world's environment", which is what Clear = Skybox with post-processing on
    // actually wants - the sky and the tonemapper both live there. Everything else needs an environment
    // of our own, and a sky-clearing one has to borrow the world's Sky resource or it would clear to
    // nothing while claiming to draw the sky. -xlinka
    private global::Godot.Environment? BuildCameraEnvironment(Lumora.Core.Assets.RenderCameraParameters p)
    {
        bool wantsSky = p.Clear == Lumora.Core.Components.ClearMode.Skybox;
        if (wantsSky && p.PostProcessing && !p.StudioLight)
        {
            _cameraEnvironment = null;
            return null;
        }

        var env = _cameraEnvironment ??= new global::Godot.Environment();
        env.Sky = null;
        env.AmbientLightSource = global::Godot.Environment.AmbientSource.Bg;
        env.ReflectedLightSource = global::Godot.Environment.ReflectionSource.Bg;

        if (wantsSky)
        {
            env.BackgroundMode = global::Godot.Environment.BGMode.Sky;
            env.Sky = SkyEnvironment.CurrentSky;
        }
        else if (p.Clear == Lumora.Core.Components.ClearMode.Color)
        {
            env.BackgroundMode = global::Godot.Environment.BGMode.Color;
            env.BackgroundColor = new Color(
                p.BackgroundColor.r, p.BackgroundColor.g, p.BackgroundColor.b, p.BackgroundColor.a);
        }
        else
        {
            // DepthOnly and Nothing both land here: Godot always clears, so the honest version of both
            // is a transparent clear, which is also what makes a camera compositable.
            env.BackgroundMode = global::Godot.Environment.BGMode.Color;
            env.BackgroundColor = new Color(0f, 0f, 0f, 0f);
        }

        // Post on means the user's stack, the same one the view gets: a photo should look like the
        // world it was taken in. Off is a raw linear read of the scene.
        if (p.PostProcessing)
        {
            SkyEnvironment.WritePost(env);
        }
        else
        {
            env.TonemapMode = global::Godot.Environment.ToneMapper.Linear;
            env.GlowEnabled = false;
            env.SsaoEnabled = false;
            env.SsrEnabled = false;
            env.AdjustmentEnabled = false;
        }
        env.SsilEnabled = false;

        if (p.StudioLight)
            LightStudio(env, p, wantsSky);

        return env;
    }

    // The studio is a sky nobody sees: dim all round and darker underneath, with a bright patch where a key
    // light would hang over the lens's left shoulder and a smaller one behind for the rim. It only lights and
    // reflects, in this picture alone. The screen-space passes are tuned for a room seen from inside it and
    // smear a single thing filmed close, and glow has nowhere to go on a clear background. -xlinka
    private void LightStudio(global::Godot.Environment env, in Lumora.Core.Assets.RenderCameraParameters p, bool wantsSky)
    {
        env.GlowEnabled = false;
        env.SsaoEnabled = false;
        env.SsrEnabled = false;
        env.SsilEnabled = false;
        // The world's own sky is the background there, and an environment holds one sky.
        if (wantsSky)
        {
            env.AmbientLightSource = global::Godot.Environment.AmbientSource.Color;
            env.AmbientLightColor = new Color(0.55f, 0.55f, 0.58f);
            env.AmbientLightEnergy = 1f;
            return;
        }
        var basis = new Basis(new Quaternion(p.Rotation.x, p.Rotation.y, p.Rotation.z, p.Rotation.w));
        var material = _studioMaterial ??= new ShaderMaterial { Shader = StudioShader };
        material.SetShaderParameter("key_direction", (basis * new Vector3(-0.55f, 0.65f, 0.55f)).Normalized());
        material.SetShaderParameter("rim_direction", (basis * new Vector3(0.6f, 0.45f, -0.65f)).Normalized());
        _studioSky ??= new Sky
        {
            SkyMaterial = material,
            ProcessMode = Sky.ProcessModeEnum.Quality,
            RadianceSize = Sky.RadianceSizeEnum.Size64,
        };
        env.Sky = _studioSky;
        env.AmbientLightSource = global::Godot.Environment.AmbientSource.Sky;
        env.AmbientLightSkyContribution = 1f;
        env.AmbientLightEnergy = 1f;
        env.ReflectedLightSource = global::Godot.Environment.ReflectionSource.Sky;
    }

    private ShaderMaterial? _studioMaterial;
    private Sky? _studioSky;
    private static Shader? _studioShader;

    private static Shader StudioShader => _studioShader ??= new Shader
    {
        Code = @"shader_type sky;
uniform vec3 key_direction = vec3(0.0, 1.0, 0.0);
uniform vec3 rim_direction = vec3(0.0, 1.0, 0.0);
uniform vec3 key_color = vec3(2.7, 2.62, 2.55);
uniform vec3 rim_color = vec3(3.0, 3.05, 3.2);
uniform vec3 top_color = vec3(0.38, 0.39, 0.42);
uniform vec3 bottom_color = vec3(0.05, 0.05, 0.06);

void sky() {
	vec3 d = normalize(EYEDIR);
	vec3 fill = mix(bottom_color, top_color, smoothstep(-0.7, 0.8, d.y));
	float key = smoothstep(0.72, 0.94, dot(d, key_direction));
	float rim = smoothstep(0.82, 0.95, dot(d, rim_direction));
	COLOR = fill + key_color * key + rim_color * rim;
}
",
    };

    public void SetCameraOverride(Lumora.Core.Assets.RenderCameraParameters? parameters)
    {
        _cameraOverride = parameters;
        Callable.From(ApplyOnMainThread).CallDeferred();
        Callable.From(ApplyRenderEnabled).CallDeferred();
    }

    public void SetRenderEnabled(bool enabled)
    {
        _renderEnabled = enabled;
        Callable.From(ApplyRenderEnabled).CallDeferred();
    }

    private void ApplyRenderEnabled()
    {
        if (_viewport == null || !GodotObject.IsInstanceValid(_viewport)) return;
        // Enabling draws one frame now; subsequent frames come from RequestRender on actual UI changes. -xlinka
        // A camera-driven viewport is filming a live scene and nothing tells us when that scene changed,
        // so it renders every frame it is wanted (WantsFilm). The provider-driven one only ever captures UI
        // that announces its own changes, which is why it stays on render-on-request.
        if (_cameraOverride.HasValue)
        {
            Filming(true);
            bool was = _filmingNow;
            _filmingNow = _renderEnabled && WantsFilm();
            if (_filmingNow && !was)
                _filmFrom = global::Godot.Engine.GetFramesDrawn();
            if (_filmingNow)
                _everFilmed = true;
            ApplySize();
            _viewport.RenderTargetUpdateMode = _filmingNow ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            return;
        }
        Filming(false);
        ApplySize();
        _viewport.RenderTargetUpdateMode = _renderEnabled ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Disabled;
    }

    // A CAMERA FILMS ONLY WHAT SOMEBODY CAN SEE.
    //
    // A camera-driven texture used to render every frame from the moment a camera pointed at it, whatever the
    // camera was doing: switched off, on a slot nobody had switched on, in a world in the background, or
    // feeding a screen on the far side of the building. A world with a dozen mirrors' worth of cameras parked
    // in a hidden group drew a dozen 2048 pictures every frame for nothing. Each frame, before the draw, every
    // filming texture asks: is its camera on, is its world the one being looked at, and is any renderer
    // showing the picture in front of the viewer and big enough to see. Only then does it draw that frame;
    // otherwise its last picture stays on the texture. A picture whose users cannot be traced (a script, a
    // desktop feed, nothing at all) keeps filming as before. -xlinka
    private Lumora.Core.Components.Camera? _filmingCamera;
    private bool _filmingNow;
    // The draw count when it last started filming. Until a draw has gone by since, the texture still holds
    // whatever it showed before (a camera that was off keeps its last picture), so the window waits for it.
    private long _filmFrom;

    public bool PictureCurrent => _filmingNow && global::Godot.Engine.GetFramesDrawn() > _filmFrom;
    private bool _registeredFilming;
    // The camera filling it holds it too.
    private readonly TextureViewers _viewers = new() { IgnoreHolder = static holder => holder is Lumora.Core.Components.Camera };
    private static readonly List<RenderTextureHook> _filming = new();
    private static bool _filmTicking;

    // Set by the Camera filming into this texture, so the texture can ask whether that camera is on.
    public void SetFilmingCamera(Lumora.Core.Components.Camera? camera)
    {
        if (ReferenceEquals(_filmingCamera, camera))
            return;
        _filmingCamera = camera;
        _viewers.Forget();
    }

    private void Filming(bool on)
    {
        if (on == _registeredFilming)
            return;
        _registeredFilming = on;
        if (on)
        {
            _filming.Add(this);
            EnsureTicking();
        }
        else
        {
            _filming.Remove(this);
        }
    }

    // A NEW TEXTURE'S FIRST PICTURE.
    //
    // A texture used to draw its own picture the moment its viewport was made, before anything had said what
    // it was for. A mirror's texture or a camera's is filled by that mirror or camera; the picture of its own
    // was a full scene at the texture's size, and drawing one also made the viewport keep a whole set of
    // buffers at that size for good: a dozen 4096 by 2048 mirror textures in a hidden group held a few
    // hundred megabytes each of video memory nobody ever looked at. It now waits a few frames and draws only
    // if nothing else has taken it over. -xlinka
    private const int FirstFrameDelay = 3;
    private int _firstFrameIn;
    private static readonly List<RenderTextureHook> _firstFrames = new();

    private void FirstFrame()
    {
        if (_firstFrameIn > 0)
            return;
        _firstFrameIn = FirstFrameDelay;
        _firstFrames.Add(this);
        EnsureTicking();
    }

    private static void EnsureTicking()
    {
        if (!_filmTicking && global::Godot.Engine.GetMainLoop() is SceneTree tree)
        {
            _filmTicking = true;
            tree.ProcessFrame += TickFilming;
        }
    }

    private bool TakenOver()
    {
        if (_cameraOverride.HasValue || _portalLeft != null)
            return true;
        if ((asset as Asset)?.Owner is not Lumora.Core.Assets.IAssetProvider provider)
            return false;
        try
        {
            foreach (var reference in provider.References)
            {
                if (TextureViewers.OwnerOf(reference) is Lumora.Core.Components.Camera or Lumora.Core.Components.CameraPortal)
                    return true;
            }
        }
        catch (System.InvalidOperationException)
        {
        }
        return false;
    }

    // Before every frame's draw, on the main thread.
    private static void TickFilming()
    {
        for (int i = _firstFrames.Count - 1; i >= 0; i--)
        {
            var hook = _firstFrames[i];
            if (--hook._firstFrameIn > 0)
                continue;
            _firstFrames.RemoveAt(i);
            var viewport = hook._viewport;
            if (viewport != null && GodotObject.IsInstanceValid(viewport) && hook._renderEnabled && !hook.TakenOver()
                && viewport.RenderTargetUpdateMode == SubViewport.UpdateMode.Disabled)
                viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }

        for (int i = _filming.Count - 1; i >= 0; i--)
        {
            var hook = _filming[i];
            var viewport = hook._viewport;
            if (viewport == null || !GodotObject.IsInstanceValid(viewport) || !hook._cameraOverride.HasValue)
            {
                hook._registeredFilming = false;
                _filming.RemoveAt(i);
                continue;
            }
            if (hook.FilmedAloneChanged)
                hook.ApplyFilmedAlone();
            if (hook._onWindow != hook.OnWindow)
            {
                hook._onWindow = !hook._onWindow;
                hook.MatchWindow(hook._cameraOverride.Value);
            }
            bool film = hook._renderEnabled && hook.WantsFilm();
            if (film == hook._filmingNow)
                continue;
            hook._filmingNow = film;
            if (film)
                hook._filmFrom = global::Godot.Engine.GetFramesDrawn();
            if (film && !hook._everFilmed)
            {
                hook._everFilmed = true;
                hook.ApplySize();
            }
            // A capture asks for one frame with Once; it keeps it.
            if (!film && viewport.RenderTargetUpdateMode == SubViewport.UpdateMode.Once)
                continue;
            viewport.RenderTargetUpdateMode = film ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        }
    }

    private bool WantsFilm()
    {
        var camera = _filmingCamera;
        if (camera == null)
            return true;
        if (camera.IsDestroyed || !camera.Enabled || camera.Slot is not { IsActive: true })
            return false;
        // On the desktop window it is being watched whether or not its screen is in view.
        if (OnWindow)
            return true;
        var world = camera.World;
        if (world == null || world.Focus == World.WorldFocus.Background)
            return false;
        // The wearer's own camera and anything in their private space film whatever they point it at.
        if (camera.RenderPrivateUI.Value || world.IsUserspace || camera.Slot.IsUnderLocalUser)
            return true;
        return _viewers.InView(asset);
    }

    public void RequestRender()
    {
        if (!_renderEnabled) return;
        Callable.From(ApplyRequestRender).CallDeferred();
    }

    private void ApplyRequestRender()
    {
        if (!_renderEnabled || _viewport == null || !GodotObject.IsInstanceValid(_viewport)) return;
        // UpdateMode.Once renders a single frame then reverts to no-updates on its own, keeping that frame on
        // the texture until the next request. -xlinka
        ApplyUiEnvironment();
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    // A photo: the next frame the viewport draws, read back and encoded. An idle viewport is asked for one frame;
    // the read waits until that frame has been drawn. -xlinka
    public System.Threading.Tasks.Task<byte[]?> CaptureAsync(ImageEncoding encoding, float quality)
    {
        var done = new System.Threading.Tasks.TaskCompletionSource<byte[]?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() => BeginCapture(done, encoding, quality)).CallDeferred();
        return done.Task;
    }

    public System.Threading.Tasks.Task<PixelFrame?> CaptureRawAsync()
    {
        var done = new System.Threading.Tasks.TaskCompletionSource<PixelFrame?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() => AfterNextFrame(done, ReadRaw)).CallDeferred();
        return done.Task;
    }

    private void BeginCapture(System.Threading.Tasks.TaskCompletionSource<byte[]?> done, ImageEncoding encoding, float quality)
        => AfterNextFrame(done, () => ReadImage(encoding, quality));

    private void AfterNextFrame<T>(System.Threading.Tasks.TaskCompletionSource<T?> done, System.Func<T?> read) where T : class
    {
        if (_viewport == null || !GodotObject.IsInstanceValid(_viewport))
        {
            done.TrySetResult(null);
            return;
        }
        if (!_everFilmed && _portalLeft == null)
        {
            _everFilmed = true;
            ApplySize();
        }
        if (_viewport.RenderTargetUpdateMode != SubViewport.UpdateMode.Always)
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        // A still of the wearer's own body needs it in the lights (ShadowBudget keeps it out while nothing
        // films it).
        if (_camera != null && GodotObject.IsInstanceValid(_camera)
            && (_camera.CullMask & (uint)Helpers.RenderHelper.LOCAL_HEAD_LAYER) != 0)
            ShadowBudget.FilmLocalHead();
        int drawn = 0;
        void Drawn()
        {
            if (++drawn < 2)
                return;
            RenderingServer.FramePostDraw -= Drawn;
            done.TrySetResult(read());
        }
        RenderingServer.FramePostDraw += Drawn;
        // With no window drawing (headless, minimised) the frame never comes, and the photo would hold its
        // camera open for good waiting on it.
        var tree = _viewport.GetTree();
        if (tree != null)
        {
            tree.CreateTimer(DrawPatience, processAlways: true, processInPhysics: false, ignoreTimeScale: true).Timeout += () =>
            {
                if (done.Task.IsCompleted)
                    return;
                RenderingServer.FramePostDraw -= Drawn;
                done.TrySetResult(null);
            };
        }
    }

    private const double DrawPatience = 5.0;

    private PixelFrame? ReadRaw()
    {
        try
        {
            if (_viewport == null || !GodotObject.IsInstanceValid(_viewport))
                return null;
            var image = _viewport.GetTexture()?.GetImage();
            if (image == null || image.IsEmpty())
                return null;
            if (image.GetFormat() != Image.Format.Rgba8)
                image.Convert(Image.Format.Rgba8);
            return new PixelFrame(image.GetWidth(), image.GetHeight(), image.GetData());
        }
        catch (System.Exception ex)
        {
            LumoraLogger.Warn($"RenderTextureHook: photo read failed: {ex.Message}");
            return null;
        }
    }

    // Upload copies retain all four channels at their original dimensions. -xlinka
    public static byte[]? EncodeLosslessWebp(byte[] rgba, int width, int height)
    {
        using var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
        var data = image.SaveWebpToBuffer(false);
        return data is { Length: > 0 } ? data : null;
    }

    // Pixels from anywhere written as a picture file, for photos put together from several frames.
    public static byte[]? Encode(byte[] rgba, int width, int height, ImageEncoding encoding, float quality)
    {
        var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
        if (encoding != ImageEncoding.Png)
            image.Convert(Image.Format.Rgb8);
        float q = System.Math.Clamp(quality, 0.05f, 1f);
        byte[] data = encoding switch
        {
            ImageEncoding.Png => image.SavePngToBuffer(),
            ImageEncoding.Jpg => image.SaveJpgToBuffer(q),
            _ => image.SaveWebpToBuffer(true, q),
        };
        return data is { Length: > 0 } ? data : null;
    }

    // The frame comes back in the renderer's own format, half floats on Forward+; the encoders take 8 bits.
    private byte[]? ReadImage(ImageEncoding encoding, float quality)
    {
        try
        {
            if (_viewport == null || !GodotObject.IsInstanceValid(_viewport))
                return null;
            var image = _viewport.GetTexture()?.GetImage();
            if (image == null || image.IsEmpty())
                return null;
            var format = encoding == ImageEncoding.Png ? Image.Format.Rgba8 : Image.Format.Rgb8;
            if (image.GetFormat() != format)
                image.Convert(format);
            float q = System.Math.Clamp(quality, 0.05f, 1f);
            byte[] data = encoding switch
            {
                ImageEncoding.Png => image.SavePngToBuffer(),
                ImageEncoding.Jpg => image.SaveJpgToBuffer(q),
                _ => image.SaveWebpToBuffer(true, q),
            };
            return data is { Length: > 0 } ? data : null;
        }
        catch (System.Exception ex)
        {
            LumoraLogger.Warn($"RenderTextureHook: photo read failed: {ex.Message}");
            return null;
        }
    }

    public void UploadData(byte[] pixels, int width, int height, bool hasMipmaps) { }

    // Render textures build their viewport synchronously in Configure; nothing to wait on. -xlinka
    public System.Threading.Tasks.Task WaitForUploadAsync() => System.Threading.Tasks.Task.CompletedTask;

    public void SetWrapMode(TextureWrapMode wrapU, TextureWrapMode wrapV) { }

    public override void Unload()
    {
        Filming(false);
        _firstFrames.Remove(this);
        _firstFrameIn = 0;
        _filmingCamera = null;
        _viewers.Forget();
        if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
        {
            lock (_liveViewportLock)
                _liveViewports.Remove(_viewport);
            _viewport.QueueFree();
        }
        _viewport = null!;
        _camera = null!;
        _cameraEnvironment = null;
        _uiEnvironment = null;
        _studioSky = null;
        _studioMaterial = null;
    }
}

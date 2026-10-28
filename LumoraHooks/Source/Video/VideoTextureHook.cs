// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Media;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks.Video;

// A video is a texture. Everything downstream binds it through IGodotTexture exactly like a PNG,
// and the only thing that makes it different is that the pixels arrive from a decoder instead of a
// file. The hook owns the texture, picks the decoder, and is the ONE place on the main thread where
// a decoded frame becomes a GPU upload.
//
// Threading: the core may call Load/UpdateState/SetVolume from the world thread, so everything that
// touches Godot is deferred onto the main thread and the per-frame work happens in a process node's
// _Process. Decoder threads never get near a Godot object; they publish through the backend's Tick,
// which only ever runs here. -xlinka
[ImplementableHook(typeof(VideoTexture))]
public sealed partial class VideoTextureHook : AssetHook, IVideoTextureAssetHook, IGodotTexture, IVideoFrameTarget
{
    // A stream that has produced nothing at all by now is not buffering, it is broken, and a player
    // staring at a blank panel deserves to be told which.
    private const double FirstFrameTimeoutSeconds = 20.0;

    private readonly object _requestSync = new();

    private ImageTexture? _imageTexture;
    private Image? _image;

    // A FRAME NOBODY CAN SEE STAYS ON THE DECODER.
    //
    // Every decoded frame went up to the video card, a 1080p frame at a time. A world with a screen in one room
    // paid for it everywhere else in the building. A frame goes up only while a renderer showing the video is in
    // front of the viewer, and a screen that is a small part of the view gets fewer of them: every decoded frame
    // from FullRateShare up, then fewer in proportion to its size down to MinFramesPerSecond. The sound plays on
    // regardless, and the next frame goes up the moment the screen is seen. The first frame always goes, so the
    // texture is valid from the start. -xlinka
    private readonly TextureViewers _viewers = new();
    private const float FullRateShare = 0.5f;
    private const double FullRateFramesPerSecond = 60.0;
    private const double MinFramesPerSecond = 15.0;
    private Texture2D? _presented;

    private IVideoDecoderBackend? _backend;
    private VideoProcessNode? _processNode;

    private LoadRequest? _pendingRequest;
    private double _loadStartSeconds;
    private bool _sawFirstFrame;
    private bool _sawReady;
    private bool _timedOut;
    private volatile bool _unloaded;

    private bool _wantPlay = true;
    private bool _wantLoop;
    private double _wantPosition;
    private float _wantSpeed = 1f;
    private float _volume = 1f;
    private int? _audioTrack;
    private IVideoAudioSink? _sink;

    // Kept so a material hook can ask how this video wants to be sampled. The ImageTexture itself
    // has no wrap or filter state; sampling is decided by the material that binds it. -xlinka
    public VideoFilterMode Filter { get; private set; } = VideoFilterMode.Bilinear;
    public int AnisotropicLevel { get; private set; }
    public TextureWrapMode WrapU { get; private set; } = TextureWrapMode.Clamp;
    public TextureWrapMode WrapV { get; private set; } = TextureWrapMode.Clamp;

    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    public Texture2D? GodotTexture2D => _presented != null && GodotObject.IsInstanceValid(_presented) ? _presented : null;

    public bool IsValid => GodotTexture2D != null;

    public float CurrentClockError => _backend?.CurrentClockError ?? 0f;
    public VideoStats Stats => _backend?.Stats ?? default;

    public event Action<VideoReadyInfo>? Ready;
    public event Action? TextureChanged;
    public event Action<string>? Failed;

    private sealed record LoadRequest(string Source, string? AudioSource, string? ForceEngine, string? Mime, int? AudioTrackIndex, bool IsStream, bool IsLive, int MaxWidth, int MaxHeight);

    public void Load(string source, string? audioSource, string? forceEngine, string? mime, int? audioTrackIndex, bool isStream, bool isLive, int maxWidth, int maxHeight)
    {
        lock (_requestSync)
        {
            _unloaded = false;
            _pendingRequest = new LoadRequest(source ?? string.Empty, audioSource, forceEngine, mime, audioTrackIndex, isStream, isLive, maxWidth, maxHeight);
        }
        _audioTrack = audioTrackIndex;
        Callable.From(StartPendingLoad).CallDeferred();
    }

    public void UpdateState(bool play, bool loop, double position, float speed)
    {
        _wantPlay = play;
        _wantLoop = loop;
        _wantPosition = position;
        _wantSpeed = speed;
        _backend?.UpdateState(play, loop, position, speed);
    }

    public void SetVolume(float volume)
    {
        _volume = volume;
        _backend?.SetVolume(volume);
    }

    public void SetAudioTrack(int? index)
    {
        _audioTrack = index;
        _backend?.SetAudioTrack(index);
    }

    public void SetAudioSink(IVideoAudioSink? sink)
    {
        _sink = sink;
        _backend?.SetAudioSink(sink);
    }

    public void SetTextureProperties(VideoFilterMode filter, int anisotropicLevel, TextureWrapMode wrapU, TextureWrapMode wrapV)
    {
        Filter = filter;
        AnisotropicLevel = anisotropicLevel;
        WrapU = wrapU;
        WrapV = wrapV;
    }

    // ITextureAssetHook: a provider may push a poster or a placeholder into the same texture the
    // decoder writes to, which is how a video panel can show something before the first frame lands.
    public void UploadData(byte[] pixels, int width, int height, bool hasMipmaps)
    {
        if (pixels == null || pixels.Length == 0) return;
        Callable.From(() => BuildFromUpload(pixels, width, height)).CallDeferred();
    }

    // Nothing to wait on: the first frame of a video is asynchronous by nature and the asset is
    // usable (as an empty texture) the moment it exists.
    public System.Threading.Tasks.Task WaitForUploadAsync() => System.Threading.Tasks.Task.CompletedTask;

    public void SetWrapMode(TextureWrapMode wrapU, TextureWrapMode wrapV)
    {
        WrapU = wrapU;
        WrapV = wrapV;
    }

    public override void Unload()
    {
        lock (_requestSync)
        {
            _unloaded = true;
            _pendingRequest = null;
        }

        var backend = _backend;
        var node = _processNode;
        var texture = _imageTexture;
        var converter = _converter;
        var planesView = _planesView;
        var planesTexture = _planesTexture;
        var planesImageTexture = _planesImageTexture;
        _converter = null;
        _converterRect = null;
        _converterMaterial = null;
        _boundPlanes = null;
        _planesView = null;
        _planesTexture = default;
        _planesUpdatable = false;
        _planesImage = null;
        _planesImageTexture = null;
        _backend = null;
        _processNode = null;
        _imageTexture = null;
        _image = null;
        _presented = null;

        Callable.From(() =>
        {
            backend?.Stop();
            backend?.Dispose();
            if (node != null && GodotObject.IsInstanceValid(node))
                node.QueueFree();
            texture?.Dispose();
            FreePlanes(converter, planesView, planesTexture, planesImageTexture);
        }).CallDeferred();
    }

    // MAIN THREAD

    private void StartPendingLoad()
    {
        LoadRequest? request;
        lock (_requestSync)
        {
            // Unload can land between the request and this deferred call; starting a decoder for an
            // asset that is already gone would leak the whole pipeline. -xlinka
            request = _unloaded ? null : _pendingRequest;
            _pendingRequest = null;
        }
        if (request == null) return;

        var previous = _backend;
        _backend = null;
        if (previous != null)
        {
            previous.Ready -= OnBackendReady;
            previous.Failed -= OnBackendFailed;
            previous.Stop();
            previous.Dispose();
        }

        _sawFirstFrame = false;
        _sawReady = false;
        _timedOut = false;
        _loadStartSeconds = global::Godot.Time.GetTicksMsec() / 1000.0;

        var host = EnsureProcessNode();
        if (host == null)
        {
            OnBackendFailed("no scene tree to host the decoder");
            return;
        }

        var backend = CreateBackend(request, host, out var source);
        if (backend == null)
            return;

        backend.Ready += OnBackendReady;
        backend.Failed += OnBackendFailed;
        _backend = backend;

        backend.SetAudioSink(_sink);
        backend.SetVolume(_volume);
        backend.UpdateState(_wantPlay, _wantLoop, _wantPosition, _wantSpeed);
        backend.Open(source, request.AudioSource, _audioTrack, request.IsStream, request.IsLive, request.MaxWidth, request.MaxHeight);
    }

    // Engine choice, in one place so the rule is readable:
    //   .ogv/.ogg      -> the built-in Theora decoder, whatever was asked for
    //   anything else  -> libVLC
    //   libVLC missing -> Theora for an ogg, a named failure for everything else
    // A forced "libVLC" beats the extension, because someone forcing it wants the reason when it is
    // not there rather than a silent downgrade. -xlinka
    //
    // A video from the asset database (a local:// source) is sealed on disk and only ever opened through a
    // decrypting stream. libVLC reads that stream; the built-in Theora decoder can only open a path, so a
    // sealed one goes to libVLC whatever its container. Where there is no libVLC the store keeps Theora plain
    // (LocalDB.KeepsTheoraPlain) and Theora is given its file.
    private IVideoDecoderBackend? CreateBackend(LoadRequest request, Node host, out string source)
    {
        source = request.Source;
        bool stored = request.Source.StartsWith("local://", StringComparison.Ordinal);
        bool isOgg = HasOggExtension(request.Source) || string.Equals(request.Mime, "video/ogg", StringComparison.OrdinalIgnoreCase);
        bool wantsLibVlc = request.ForceEngine == MediaServices.EngineLibVlc || !isOgg || stored;

        if (!wantsLibVlc)
            return new TheoraBackend(host);

#if GODOT_PC
        if (LibVlcRuntime.IsAvailable)
            return new LibVlcBackend();
        string reason = LibVlcRuntime.UnavailableReason ?? "not initialised";
#else
        const string reason = "this platform has no libVLC build";
#endif
        if (isOgg && !stored)
            return new TheoraBackend(host);
        if (isOgg && stored)
        {
            var path = Lumora.Core.Engine.Current?.LocalDB?.GetFilePath(request.Source);
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path) && !Lumora.Nexus.Storage.SealedFile.IsSealed(path))
            {
                source = path!;
                return new TheoraBackend(host);
            }
            OnBackendFailed($"a stored video plays through a decrypting stream, and the built-in decoder here only opens files ({reason})");
            return null;
        }

        OnBackendFailed($"libVLC unavailable: {reason}");
        return null;
    }

    private static bool HasOggExtension(string source)
    {
        if (string.IsNullOrEmpty(source)) return false;
        string path = source;
        int query = path.IndexOfAny(new[] { '?', '#' });
        if (query >= 0)
            path = path.Substring(0, query);
        return path.EndsWith(".ogv", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    private Node? EnsureProcessNode()
    {
        if (_processNode != null && GodotObject.IsInstanceValid(_processNode))
            return _processNode;

        if (global::Godot.Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            return null;

        // Parented to the scene tree root rather than to a world: an asset outlives any one world and
        // a decoder that stops with a backgrounded world would desync the moment you looked away.
        _processNode = new VideoProcessNode(this)
        {
            Name = "VideoDecoder",
            ProcessMode = Node.ProcessModeEnum.Always,
        };
        tree.Root.AddChild(_processNode);
        return _processNode;
    }

    internal void Tick()
    {
        var backend = _backend;
        if (backend == null) return;

        try
        {
            backend.Tick(this);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"VideoTextureHook: decoder tick failed: {ex.Message}");
        }

        if (_timedOut || _sawFirstFrame || _sawReady) return;
        if (global::Godot.Time.GetTicksMsec() / 1000.0 - _loadStartSeconds < FirstFrameTimeoutSeconds) return;

        _timedOut = true;
        OnBackendFailed($"no video after {FirstFrameTimeoutSeconds:0} s");
    }

    double IVideoFrameTarget.FrameInterval
    {
        get
        {
            if (!_sawFirstFrame)
                return 0.0;
            if (!_viewers.InView(asset))
                return -1.0;
            float share = _viewers.LargestShare;
            if (share >= FullRateShare)
                return 0.0;
            return 1.0 / System.Math.Max(MinFramesPerSecond, FullRateFramesPerSecond * share / FullRateShare);
        }
    }

    // COLOUR IS PUT BACK TOGETHER ON THE CARD.
    //
    // A decoder that hands over planes (LibVlcBackend) sends a byte and a half a pixel instead of four, as one
    // one-byte texture: brightness rows, then colour rows with U on the left half and V on the right. A small
    // viewport draws it into the colour picture every material samples, once for each new frame. Colour samples
    // are clamped inside their own half so filtering never mixes U into V or brightness into colour. The matrix
    // is BT.709 for pictures 720 rows and up and BT.601 below, the usual rule for video that does not say which;
    // both studio range. The picture the viewport draws is read through an sRGB view like any colour texture, so
    // materials see the same values as from an image. -xlinka
    private const string PlaneShaderCode = @"shader_type canvas_item;
uniform sampler2D planes : filter_linear;
uniform vec2 picture = vec2(1.0);
uniform float luma_lines = 1.0;
uniform bool bt709 = true;
void fragment() {
	vec2 size = vec2(textureSize(planes, 0));
	vec2 p = UV * picture;
	vec2 luma = vec2(p.x, min(p.y, picture.y - 0.5));
	vec2 c = clamp(p * 0.5, vec2(0.5), ceil(picture * 0.5) - 0.5);
	vec2 cu = vec2(c.x, luma_lines + c.y);
	float y = (texture(planes, luma / size).r - 16.0 / 255.0) * (255.0 / 219.0);
	float u = (texture(planes, cu / size).r - 0.5) * (255.0 / 224.0);
	float v = (texture(planes, (cu + vec2(size.x * 0.5, 0.0)) / size).r - 0.5) * (255.0 / 224.0);
	vec3 rgb = bt709
		? vec3(y + 1.5748 * v, y - 0.1873 * u - 0.4681 * v, y + 1.8556 * u)
		: vec3(y + 1.402 * v, y - 0.344136 * u - 0.714136 * v, y + 1.772 * u);
	COLOR = vec4(clamp(rgb, 0.0, 1.0), 1.0);
}
";
    private static readonly StringName PlanesParam = "planes";
    private static readonly StringName PictureParam = "picture";
    private static readonly StringName LumaLinesParam = "luma_lines";
    private static readonly StringName Bt709Param = "bt709";
    // Passed instead of null: GodotSharp would make a temporary array for the call that nothing keeps alive.
    private static readonly global::Godot.Collections.Array<byte[]> NoLayers = new();
    private static Shader? s_planeShader;

    private SubViewport? _converter;
    private ColorRect? _converterRect;
    private ShaderMaterial? _converterMaterial;
    private Texture2D? _boundPlanes;
    private Texture2Drd? _planesView;
    // The planes being shown. A backend's texture is ours to free once a newer one replaces it; one made here
    // for main-thread uploads is updated in place.
    private Rid _planesTexture;
    private bool _planesUpdatable;
    private int _planesWidth;
    private int _planesHeight;
    private Image? _planesImage;
    private ImageTexture? _planesImageTexture;
    private int _layoutWidth;
    private int _layoutHeight;
    private int _layoutPitch;
    private int _layoutLumaLines;

    void IVideoFrameTarget.PresentPlanes(Rid texture, int width, int height, int pitch, int lumaLines, int chromaLines)
    {
        var device = RenderingServer.GetRenderingDevice();
        if (device == null || !texture.IsValid)
            return;
        if (width <= 0 || height <= 0 || pitch <= 0 || lumaLines <= 0 || chromaLines <= 0
            || !EnsureConverter(width, height, pitch, lumaLines))
        {
            device.FreeRid(texture);
            return;
        }

        var previous = _planesTexture;
        _planesTexture = texture;
        _planesUpdatable = false;
        _planesView ??= new Texture2Drd();
        _planesView.TextureRdRid = texture;
        BindPlanes(_planesView);
        if (previous.IsValid && previous != texture)
            device.FreeRid(previous);
        ShowPlanes(width, height);
    }

    void IVideoFrameTarget.SubmitPlanes(ReadOnlySpan<byte> planes, int width, int height, int pitch, int lumaLines, int chromaLines)
    {
        if (width <= 0 || height <= 0 || pitch <= 0 || lumaLines <= 0 || chromaLines <= 0)
            return;
        int rows = lumaLines + chromaLines;
        if (planes.Length < pitch * rows)
            return;
        // A backend pacing itself already asked; this is for one that hands every frame over.
        if (_sawFirstFrame && ((IVideoFrameTarget)this).FrameInterval < 0.0)
            return;

        try
        {
            if (!EnsureConverter(width, height, pitch, lumaLines))
                return;
            UploadPlanes(planes.Slice(0, pitch * rows), pitch, rows);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"VideoTextureHook: plane upload failed: {ex.Message}");
            return;
        }
        ShowPlanes(width, height);
    }

    private void ShowPlanes(int width, int height)
    {
        _converter!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        VideoWidth = width;
        VideoHeight = height;
        _sawFirstFrame = true;
        Present(_converter.GetTexture());
    }

    private void BindPlanes(Texture2D texture)
    {
        if (ReferenceEquals(_boundPlanes, texture))
            return;
        _boundPlanes = texture;
        _converterMaterial!.SetShaderParameter(PlanesParam, texture);
    }

    // The uniforms only change with the picture's size, not every frame.
    private bool EnsureConverter(int width, int height, int pitch, int lumaLines)
    {
        if (_converter == null || !GodotObject.IsInstanceValid(_converter))
        {
            var host = EnsureProcessNode();
            if (host == null)
                return false;
            s_planeShader ??= new Shader { Code = PlaneShaderCode };
            _converterMaterial = new ShaderMaterial { Shader = s_planeShader };
            _converterRect = new ColorRect { Material = _converterMaterial };
            _converter = new SubViewport
            {
                Name = "VideoPlanes",
                Disable3D = true,
                TransparentBg = false,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            };
            _converter.AddChild(_converterRect);
            host.AddChild(_converter);
            _boundPlanes = null;
            _layoutWidth = 0;
        }
        if (_layoutWidth == width && _layoutHeight == height && _layoutPitch == pitch && _layoutLumaLines == lumaLines)
            return true;

        _layoutWidth = width;
        _layoutHeight = height;
        _layoutPitch = pitch;
        _layoutLumaLines = lumaLines;
        var size = new Vector2I(width, height);
        if (_converter.Size != size)
            _converter.Size = size;
        _converterRect!.Size = new Vector2(width, height);
        _converterMaterial!.SetShaderParameter(PictureParam, new Vector2(width, height));
        _converterMaterial.SetShaderParameter(LumaLinesParam, (float)lumaLines);
        _converterMaterial.SetShaderParameter(Bt709Param, height >= 720);
        return true;
    }

    // Main thread: the planes go into a texture of our own, in place.
    private void UploadPlanes(ReadOnlySpan<byte> planes, int pitch, int rows)
    {
        var device = RenderingServer.GetRenderingDevice();
        if (device != null)
        {
            if (_planesUpdatable && _planesTexture.IsValid && _planesWidth == pitch && _planesHeight == rows)
            {
                device.TextureUpdate(_planesTexture, 0, planes);
                return;
            }
            var format = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R8Unorm,
                Width = (uint)pitch,
                Height = (uint)rows,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
            };
            var view = new RDTextureView();
            var created = device.TextureCreate(format, view, NoLayers);
            GC.KeepAlive(format);
            GC.KeepAlive(view);
            GC.KeepAlive(NoLayers);
            if (!created.IsValid)
                throw new InvalidOperationException("no device texture for the planes");
            device.TextureUpdate(created, 0, planes);
            var previous = _planesTexture;
            _planesTexture = created;
            _planesUpdatable = true;
            _planesWidth = pitch;
            _planesHeight = rows;
            _planesView ??= new Texture2Drd();
            _planesView.TextureRdRid = created;
            BindPlanes(_planesView);
            if (previous.IsValid)
                device.FreeRid(previous);
            return;
        }

        if (_planesImage != null && _planesImageTexture != null && _planesWidth == pitch && _planesHeight == rows)
        {
            _planesImage.SetData(pitch, rows, false, Image.Format.L8, planes);
            _planesImageTexture.Update(_planesImage);
            return;
        }
        _planesImage = Image.CreateFromData(pitch, rows, false, Image.Format.L8, planes);
        _planesImageTexture = ImageTexture.CreateFromImage(_planesImage);
        _planesWidth = pitch;
        _planesHeight = rows;
        BindPlanes(_planesImageTexture);
    }

    private static void FreePlanes(SubViewport? converter, Texture2Drd? view, Rid texture, ImageTexture? imageTexture)
    {
        if (converter != null && GodotObject.IsInstanceValid(converter))
            converter.QueueFree();
        if (view != null && GodotObject.IsInstanceValid(view))
        {
            view.TextureRdRid = new Rid();
            view.Dispose();
        }
        if (texture.IsValid)
            RenderingServer.GetRenderingDevice()?.FreeRid(texture);
        imageTexture?.Dispose();
    }

    void IVideoFrameTarget.PresentTexture(Texture2D? texture, int width, int height)
    {
        if (texture == null || !GodotObject.IsInstanceValid(texture)) return;
        VideoWidth = width;
        VideoHeight = height;
        _sawFirstFrame = true;
        Present(texture);
    }

    // TextureChanged means "bind a different object", not "new pixels". Pixels go into the same
    // ImageTexture in place, so a material that bound it once keeps seeing every frame. What a
    // material cannot see on its own is the first frame turning a not-yet-valid texture into a valid
    // one: Ready fires as soon as the decoder knows the size, before anything was decoded, and a
    // material that bound at that moment is holding its white fallback until told to look again. -xlinka
    private void Present(Texture2D? texture)
    {
        bool changed = !ReferenceEquals(_presented, texture);
        _presented = texture;
        if (changed)
            TextureChanged?.Invoke();
    }

    private void BuildFromUpload(byte[] pixels, int width, int height)
    {
        try
        {
            Image image;
            if (width > 0 && height > 0 && pixels.Length == width * height * 4)
            {
                image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);
            }
            else
            {
                image = new Image();
                if (image.LoadPngFromBuffer(pixels) != Error.Ok && image.LoadJpgFromBuffer(pixels) != Error.Ok)
                {
                    LumoraLogger.Warn("VideoTextureHook: UploadData could not decode the supplied image");
                    return;
                }
            }

            _image = image;
            Adopt(image);
            VideoWidth = image.GetWidth();
            VideoHeight = image.GetHeight();
            Present(_imageTexture);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"VideoTextureHook: UploadData failed: {ex.Message}");
        }
    }

    private void Adopt(Image image)
    {
        if (_imageTexture == null || !GodotObject.IsInstanceValid(_imageTexture))
            _imageTexture = ImageTexture.CreateFromImage(image);
        else
            _imageTexture.SetImage(image);
    }

    private void OnBackendReady(VideoReadyInfo info)
    {
        _sawReady = true;
        if (info.Width > 0) VideoWidth = info.Width;
        if (info.Height > 0) VideoHeight = info.Height;
        Ready?.Invoke(info);
    }

    private void OnBackendFailed(string reason) => Failed?.Invoke(reason);

    private sealed partial class VideoProcessNode : Node
    {
        private readonly VideoTextureHook _hook;
        public VideoProcessNode(VideoTextureHook hook) => _hook = hook;
        public VideoProcessNode() => _hook = null!;

        public override void _Process(double delta) => _hook?.Tick();
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Threading;
using Lumora.Audio;
using Lumora.Core.Assets;
using Lumora.Core.Components.Audio;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Media;
using Lumora.Core.Media;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Assets;

// A video as a texture provider. Everything downstream - a material, a UI image, a skybox - sees an
// ordinary texture and never learns there is a decoder behind it.
//
// The CLOCK is the interesting part. Playback is a SyncPlayback, so the playhead is an anchor plus a
// rate that every peer already agrees on, and this component never streams a position: each frame it
// hands the decoder the position the shared clock says it should be at and the decoder chases that.
// Two people looking at the same video are in step because they compute the same number, not because
// one of them is sending frames of timecode.
//
// The OUTPUT fields (title, engine, error, clock error) are what the LOCAL decoder found, so they are
// written silently: they never cross the wire. They cannot, honestly - each peer resolves its own
// stream URL, picks its own engine and has its own drift, and replicating one peer's answer would
// overwrite three other peers' truth with it.
//
// Nothing is downloaded first. An http URL plays as a stream, which is why a two hour video starts in
// a second instead of after a two gigabyte fetch. -xlinka
[ComponentCategory("Assets/Video")]
public sealed class VideoTextureProvider : DynamicAssetProvider<VideoTexture>, IPlayable, IWorldAudioDataSource, IVideoAudioSink, IUrlAssetProvider
{
    public readonly Sync<Uri> URL;

    // Play a local file as a stream rather than seeking around a decoded clip. http(s) is always a
    // stream regardless.
    public readonly Sync<bool> Stream;

    // MediaServices.EngineNative / EngineLibVlc, or empty for automatic.
    public readonly Sync<string> ForcePlaybackEngine;

    // Run a URL through the service resolver even when its host is not one of the known services.
    public readonly Sync<bool> ForceVideoStreamingServiceParsing;

    public readonly Sync<VideoFilterMode> FilterMode;
    public readonly Sync<int> AnisotropicLevel;
    public readonly Sync<TextureWrapMode> WrapModeU;
    public readonly Sync<TextureWrapMode> WrapModeV;

    // -1 picks whatever the container calls the default track. There is no nullable int coder, so the
    // sentinel is the honest shape here rather than a member that would silently never replicate.
    public readonly Sync<int> AudioTrackIndex;

    // Ask the resolver for a sound-only format. A radio stream or a music link has no frames to show.
    public readonly Sync<bool> PreferAudioOnly;

    // Caps handed to the service resolver, 0 meaning no cap of our own. Height falls back to the
    // engine setting so one place decides what a 4k link costs.
    public readonly Sync<int> MaxWidth;
    public readonly Sync<int> MaxHeight;

    [NonPersistent]
    public readonly Sync<string> VideoTitle;
    [NonPersistent]
    public readonly Sync<string> CurrentPlaybackEngine;
    [NonPersistent]
    public readonly Sync<string> LoadError;
    [NonPersistent]
    public readonly Sync<float> CurrentClockError;

    public readonly SyncPlayback Playback;

    private VideoTexture? _hooked;
    private CancellationTokenSource? _resolve;

    // What the last Load was issued for. A change pass that moves none of these must not restart the
    // decoder: a reload drops the frame, the audio and the position.
    private string? _loadedSource;
    private bool _loadedStream;
    private bool _loadedLive;
    private string? _loadedEngine;
    private int _loadedMaxWidth;
    private int _loadedMaxHeight;
    private bool _loadedAudioOnly;
    private string? _loadedAudioSource;
    private int _loadedAudioTrack = int.MinValue;

    // The service request the current resolve was issued for, so a change pass does not spawn a second
    // yt-dlp process for a link that is already being resolved.
    private string? _serviceKey;

    private bool _isLive;
    private bool _autoPlayed;

    // Below this the correction is not worth a change event, and the decoder reports a new number every
    // single frame.
    private const float ClockErrorEpsilon = 0.01f;

    public VideoTextureProvider()
    {
        URL = new Sync<Uri>(this, null!);
        Stream = new Sync<bool>(this, false);
        ForcePlaybackEngine = new Sync<string>(this, string.Empty);
        ForceVideoStreamingServiceParsing = new Sync<bool>(this, false);
        FilterMode = new Sync<VideoFilterMode>(this, VideoFilterMode.Bilinear);
        AnisotropicLevel = new Sync<int>(this, 8);
        WrapModeU = new Sync<TextureWrapMode>(this, TextureWrapMode.Repeat);
        WrapModeV = new Sync<TextureWrapMode>(this, TextureWrapMode.Repeat);
        AudioTrackIndex = new Sync<int>(this, -1);
        PreferAudioOnly = new Sync<bool>(this, false);
        MaxWidth = new Sync<int>(this, 0);
        MaxHeight = new Sync<int>(this, 0);
        VideoTitle = new Sync<string>(this, string.Empty);
        CurrentPlaybackEngine = new Sync<string>(this, string.Empty);
        LoadError = new Sync<string>(this, string.Empty);
        CurrentClockError = new Sync<float>(this, 0f);
        Playback = new SyncPlayback();
    }

    // TRANSPORT

    public bool IsLive => _isLive;

    public float Position
    {
        get => Playback.Position;
        set => Playback.Seek(value);
    }

    public float Speed
    {
        get => Playback.Speed;
        set => Playback.Speed = value;
    }

    public bool Loop
    {
        get => Playback.LoopMode != PlaybackLoopMode.Once;
        set => Playback.LoopMode = value ? PlaybackLoopMode.Loop : PlaybackLoopMode.Once;
    }

    // Infinite for a live stream, -1 while the decoder has not probed it yet. SyncPlayback refuses a
    // non-finite length (it stores unknown instead), so live is tracked here rather than in the member.
    public double ClipLength => Playback.HasLength
        ? Playback.Length
        : (_isLive ? double.PositiveInfinity : -1.0);

    public bool IsStreaming => double.IsPositiveInfinity(ClipLength);

    public bool IsPlaying => Playback.IsPlaying;

    public bool IsFinished => Playback.IsFinished;

    public float NormalizedPosition
    {
        get => IsStreaming || !Playback.HasLength ? -1f : Playback.NormalizedPosition;
        set
        {
            if (!IsStreaming)
                Playback.NormalizedPosition = value;
        }
    }

    [SyncMethod]
    [ScriptMethod]
    public void Play() => Playback.Play();

    [SyncMethod]
    [ScriptMethod]
    public void Stop() => Playback.Stop();

    [SyncMethod]
    [ScriptMethod]
    public void Pause() => Playback.Pause();

    [SyncMethod]
    [ScriptMethod]
    public void Resume() => Playback.Resume();

    [SyncMethod]
    [ScriptMethod]
    public void TogglePlayback() => Playback.TogglePlayback();

    [SyncMethod]
    public void UseAutomaticEngine() => ForcePlaybackEngine.Value = string.Empty;

    [SyncMethod]
    public void UseNativeEngine() => ForcePlaybackEngine.Value = MediaServices.EngineNative;

    [SyncMethod]
    public void UseLibVlcEngine() => ForcePlaybackEngine.Value = MediaServices.EngineLibVlc;

    // AUDIO
    //
    // One ring buffer with two threads on it. The DECODER side (Configure/Push/Flush) runs on whatever
    // thread the backend decodes on and writes; the MIXER side (Read) runs on the audio thread and
    // reads. The lock covers both because the reader's position is bookkeeping the writer invalidates.
    //
    // Nothing here touches a sync member. Playback is read once a frame on the world thread into a plain
    // bool, because reading a replicated field from the audio thread is how you get a torn read in the
    // one place that cannot afford one. -xlinka

    private const double AudioStartDelaySeconds = 0.1;
    // The decoder side paces delivery to its own clock now, so the reader should never sit far behind
    // the writer; if it does (the mixer skipped this source for a while, a stall released a burst),
    // the backlog is stale sound and is dropped rather than played late.
    private const double AudioMaxLagSeconds = 0.5;
    private const int AudioRingSeconds = 2;
    public int AudioDrops { get; private set; }

    private readonly object _audioLock = new object();
    private CircularAudioBuffer<StereoSample>? _audioBuffer;
    private StereoSample[] _pushScratch = new StereoSample[2048];
    private readonly StereoSample[] _resampleScratch = new StereoSample[512];
    private int _incomingChannels = 2;
    private int _incomingRate;
    private int _mixerRate;
    private long _audioReadPosition;
    private bool _audioReadingStarted;
    private double _resampleCursor;
    private StereoSample _resampleLast;
    private bool _audioActive;

    // Frames the mixer asked for and the buffer could not supply. Diagnostics only: plain, unsynced and
    // unsaved, because an underrun count is this machine's experience of this stream.
    public int AudioUnderruns { get; private set; }
    // Frames the mixer asked for while the start delay was rebuilding after a flush, and how many
    // flushes there were: the two silent paths an underrun count never shows.
    public int AudioWaits { get; private set; }
    public int AudioFlushes { get; private set; }

    public int ChannelCount => 2;

    public bool IsActive => _audioActive;

    // IVideoAudioSink - decoder threads

    public void Configure(int channels, int sampleRate)
    {
        lock (_audioLock)
        {
            _incomingChannels = channels < 1 ? 1 : channels;
            _incomingRate = sampleRate > 0 ? sampleRate : MixerSampleRate();
            EnsureBufferLocked();
            _audioBuffer?.Clear();
            ResetReaderLocked();
        }
    }

    public double LatencySeconds
        => AudioStartDelaySeconds + (Engine.Current?.AudioSystem?.OutputLatencySeconds ?? 0.0);

    public VideoStats DecoderStats => Asset?.Stats ?? default;

    public void Push(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length == 0)
            return;

        lock (_audioLock)
        {
            var buffer = EnsureBufferLocked();
            if (buffer == null)
                return;

            int channels = _incomingChannels;
            int frames = interleaved.Length / channels;
            if (frames <= 0)
                return;

            if (_pushScratch.Length < frames)
                _pushScratch = new StereoSample[frames];

            for (int i = 0; i < frames; i++)
            {
                int at = i * channels;
                float left = interleaved[at];
                // Mono plays out of both ears; anything wider than stereo keeps its front pair rather
                // than being folded down, which is a mix decision and not this seam's to make.
                float right = channels > 1 ? interleaved[at + 1] : left;
                _pushScratch[i] = new StereoSample(left, right);
            }

            var source = new ReadOnlySpan<StereoSample>(_pushScratch, 0, frames);
            if (_incomingRate == _mixerRate)
                buffer.Put(source);
            else
                PutResampledLocked(buffer, source);
        }
    }

    public void Flush()
    {
        lock (_audioLock)
        {
            AudioFlushes++;
            _audioBuffer?.Clear();
            ResetReaderLocked();
        }
    }

    // The mixer owns volume now: it is the AudioOutput pointing at this provider that carries a gain, a
    // group and a distance. A second gain here would be a second thing to explain when a video is quiet.
    public void SetVolume(float volume)
    {
    }

    // IAudioDataSource - audio thread

    public void Read(Span<StereoSample> buffer, AudioSimulator simulator)
    {
        lock (_audioLock)
        {
            var ring = _audioBuffer;
            if (ring == null)
            {
                buffer.Clear();
                return;
            }

            // Reading the instant the first sample lands means reading faster than the decoder delivers
            // for the rest of the stream. The delay is rebuilt after every flush, because a seek is a new
            // stream as far as the buffer is concerned.
            long startDelay = (long)(_mixerRate * AudioStartDelaySeconds);
            long lag = ring.GlobalIndex - _audioReadPosition;
            if (lag > (long)(_mixerRate * AudioMaxLagSeconds))
            {
                _audioReadPosition = ring.GlobalIndex - startDelay;
                AudioDrops++;
                lag = startDelay;
            }
            if (!_audioReadingStarted)
            {
                if (lag < startDelay)
                {
                    AudioWaits++;
                    buffer.Clear();
                    return;
                }
                _audioReadingStarted = true;
            }

            int read = ring.Read(buffer, ref _audioReadPosition);
            if (read < buffer.Length)
            {
                // Ran dry: silence out the rest and wait for the delay to rebuild before reading again,
                // otherwise every late block becomes a crackle of one-sample gaps.
                buffer.Slice(read).Clear();
                AudioUnderruns++;
                _audioReadingStarted = false;
            }
        }
    }

    private static int MixerSampleRate()
        => Engine.Current?.AudioSystem?.Simulator.SampleRate ?? AudioSimulator.DefaultSampleRate;

    private CircularAudioBuffer<StereoSample>? EnsureBufferLocked()
    {
        if (_mixerRate <= 0)
            _mixerRate = MixerSampleRate();
        if (_incomingRate <= 0)
            _incomingRate = _mixerRate;
        // Two seconds of slack so a paced writer can never lap the reader; a seek flushes it anyway.
        return _audioBuffer ??= new CircularAudioBuffer<StereoSample>(_mixerRate * AudioRingSeconds);
    }

    private void ResetReaderLocked()
    {
        _audioReadPosition = 0;
        _audioReadingStarted = false;
        _resampleCursor = 0.0;
        _resampleLast = default;
    }

    // Linear interpolation as the samples arrive, with the cursor and the previous sample carried across
    // calls so the seam between two pushes interpolates like the middle of one.
    private void PutResampledLocked(CircularAudioBuffer<StereoSample> buffer, ReadOnlySpan<StereoSample> source)
    {
        double rate = (double)_incomingRate / _mixerRate;
        double position = _resampleCursor;
        int written = 0;

        while (true)
        {
            int index = (int)System.Math.Floor(position);
            if (index + 1 >= source.Length)
                break;

            var previous = index < 0 ? _resampleLast : source[index];
            var next = source[index + 1];
            _resampleScratch[written++] = previous.LerpTo(next, (float)(position - index));
            if (written == _resampleScratch.Length)
            {
                buffer.Put(_resampleScratch);
                written = 0;
            }
            position += rate;
        }

        if (written > 0)
            buffer.Put(new ReadOnlySpan<StereoSample>(_resampleScratch, 0, written));

        _resampleLast = source[source.Length - 1];
        _resampleCursor = position - source.Length;
    }

    // LOAD STATE

    public Uri? SourceUrl => URL.Value;

    public string? LoadFailure => URL.Value == null || string.IsNullOrEmpty(LoadError.Value) ? null : LoadError.Value;

    public bool IsLoadPending
    {
        get
        {
            if (URL.Value == null)
                return false;
            if (!string.IsNullOrEmpty(LoadError.Value))
                return false;
            var asset = Asset;
            return asset == null || !asset.IsReady;
        }
    }

    // ASSET

    protected override void OnAssetCreated(VideoTexture asset)
    {
        DetachEvents();
        _hooked = asset;
        asset.Ready += HandleReady;
        asset.Failed += HandleFailed;
        // Not a per-frame event: pixels land in the same texture object in place. It fires when the
        // object to bind changes, above all on the first frame, when every material bound at Ready is
        // still showing its fallback because nothing had been decoded yet. -xlinka
        asset.FrameChanged += HandleFrameChanged;
    }

    protected override void OnAssetCleared()
    {
        CancelResolve();
        DetachEvents();
        // Nothing is decoding into the buffer any more, so the mixer must stop pulling from it before
        // it plays out the last second of a video that no longer exists.
        _audioActive = false;
        Flush();
        _loadedSource = null;
        _serviceKey = null;
        _isLive = false;
        _autoPlayed = false;
    }

    public override void OnDestroy()
    {
        CancelResolve();
        DetachEvents();
        _audioActive = false;
        base.OnDestroy();
    }

    private void DetachEvents()
    {
        if (_hooked == null)
            return;
        _hooked.Ready -= HandleReady;
        _hooked.FrameChanged -= HandleFrameChanged;
        _hooked.Failed -= HandleFailed;
        _hooked = null;
    }

    protected override void UpdateAsset(VideoTexture asset)
    {
        asset.SetTextureProperties(FilterMode.Value, AnisotropicLevel.Value, WrapModeU.Value, WrapModeV.Value);
        asset.SetAudioSink(this);

        var url = URL.Value;
        if (url == null || string.IsNullOrEmpty(url.OriginalString))
        {
            CancelResolve();
            _loadedSource = null;
            _serviceKey = null;
            ClearError();
            return;
        }

        string? engine = string.IsNullOrEmpty(ForcePlaybackEngine.Value) ? null : ForcePlaybackEngine.Value;

        if (AssetHelper.IsStreamingProtocol(url))
        {
            CancelResolve();
            _serviceKey = null;
            IssueLoad(asset, url.OriginalString, engine, null, isStream: true, isLive: true);
            return;
        }

        if (AssetHelper.IsVideoStreamingService(url) || ForceVideoStreamingServiceParsing.Value)
        {
            ResolveService(url, engine);
            return;
        }

        // A local:// video the store holds plays from the store, by its address: the decoder reads it through
        // a decrypting stream, since the file itself is sealed. One it does not hold is somebody else's
        // import, and is fetched from the session into the store first: this branch used to stop at "this
        // machine does not hold" it, so a dropped video only ever played for the person who dropped it.
        // -xlinka
        if (url.IsAbsoluteUri && string.Equals(url.Scheme, "local", StringComparison.OrdinalIgnoreCase))
        {
            var db = Engine.Current?.LocalDB;
            if (db != null && db.Exists(url.OriginalString))
            {
                CancelResolve();
                _serviceKey = null;
                IssueLoad(asset, url.OriginalString, engine, MimeFor(db.GetExtension(url.OriginalString)), Stream.Value);
                return;
            }
            GatherLocal(url, engine);
            return;
        }

        CancelResolve();
        _serviceKey = null;

        if (url.IsAbsoluteUri
            && (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            // Always a stream: the decoder pulls as it plays, so a long video starts now instead of
            // after a full download that nothing would ever delete.
            IssueLoad(asset, url.OriginalString, engine, MimeFor(url.AbsolutePath), isStream: true);
            return;
        }

        string path = url.IsAbsoluteUri && string.Equals(url.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            ? url.LocalPath
            : url.OriginalString;

        if (!Path.IsPathRooted(path) || !File.Exists(path))
        {
            SetError($"file not found: {path}");
            return;
        }
        IssueLoad(asset, path, engine, MimeFor(path), Stream.Value);
    }

    private void IssueLoad(VideoTexture asset, string source, string? engine, string? mime, bool isStream, bool isLive = false, string? audioSource = null)
    {
        int track = AudioTrackIndex.Value;
        int maxWidth = EffectiveMaxWidth ?? 0;
        int maxHeight = EffectiveMaxHeight ?? 0;

        if (_loadedSource == source
            && _loadedStream == isStream
            && _loadedLive == isLive
            && _loadedEngine == engine
            && _loadedMaxWidth == maxWidth
            && _loadedMaxHeight == maxHeight
            && _loadedAudioOnly == PreferAudioOnly.Value
            && _loadedAudioSource == audioSource)
        {
            // A track change is a decoder switch, not a reload: the frame and the position stay.
            if (_loadedAudioTrack != track)
            {
                _loadedAudioTrack = track;
                asset.SetAudioTrack(track < 0 ? null : track);
            }
            return;
        }

        _loadedSource = source;
        _loadedStream = isStream;
        _loadedLive = isLive;
        _loadedEngine = engine;
        _loadedMaxWidth = maxWidth;
        _loadedMaxHeight = maxHeight;
        _loadedAudioOnly = PreferAudioOnly.Value;
        _loadedAudioSource = audioSource;
        _loadedAudioTrack = track;
        _isLive = false;
        _autoPlayed = false;

        ClearError();
        asset.Load(source, audioSource, engine, mime, track < 0 ? null : track, isStream, isLive, maxWidth, maxHeight);
        asset.SetAudioSink(this);
    }

    // SERVICE RESOLVE

    private int? EffectiveMaxWidth => MaxWidth.Value > 0 ? MaxWidth.Value : null;

    private int? EffectiveMaxHeight
    {
        get
        {
            if (MaxHeight.Value > 0)
                return MaxHeight.Value;
            int setting = EngineSettings.VideoMaxHeight;
            return setting > 0 ? setting : null;
        }
    }

    // Fetch a local:// video from the session, then play the file it lands in. Keyed like a service
    // resolve, so the many updates a provider sees while the fetch runs neither restart it nor ask again.
    private void GatherLocal(Uri url, string? engine)
    {
        string key = $"local|{url.OriginalString}|{engine}";
        if (key == _serviceKey)
            return;
        _serviceKey = key;

        CancelResolve();
        var cancellation = new CancellationTokenSource();
        _resolve = cancellation;

        StartTask(async () =>
        {
            bool held = await Lumora.Core.Networking.AssetFetcher.EnsureLocalAsync(url.OriginalString);

            await WorldContext.ToWorld();

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_resolve, cancellation))
                return;

            if (!held)
            {
                SetError($"could not fetch {url.OriginalString} from the session");
                return;
            }

            var asset = Asset;
            if (asset == null)
                return;
            IssueLoad(asset, url.OriginalString, engine, MimeFor(Engine.Current?.LocalDB?.GetExtension(url.OriginalString) ?? string.Empty),
                Stream.Value);
        });
    }

    private void ResolveService(Uri url, string? engine)
    {
        var cookies = EngineSettings.VideoCookiesBrowser;
        string key = $"{url}|{EffectiveMaxWidth}|{EffectiveMaxHeight}|{PreferAudioOnly.Value}|{engine}|{cookies}";
        if (key == _serviceKey)
            return;
        _serviceKey = key;

        CancelResolve();

        var resolver = MediaServices.VideoServiceResolver;
        if (resolver == null)
        {
            SetError("video services need the platform resolver");
            LumoraLogger.Warn($"VideoTextureProvider: no video service resolver is registered; '{url}' cannot be played.");
            return;
        }

        var request = new VideoServiceRequest(EffectiveMaxWidth, EffectiveMaxHeight, PreferAudioOnly.Value, cookies);
        var cancellation = new CancellationTokenSource();
        _resolve = cancellation;

        StartTask(async () =>
        {
            await WorldContext.ToBackground();

            VideoServiceResolution? resolution = null;
            string? failure = null;
            try
            {
                resolution = await resolver.ResolveAsync(url, request, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            // Hopping back cancels the task outright if this component died while the resolver ran, so
            // nothing below has to check for a destroyed world.
            await WorldContext.ToWorld();

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_resolve, cancellation))
                return;

            if (resolution == null)
            {
                SetError(string.IsNullOrEmpty(failure) ? $"could not resolve {url}" : failure!);
                LumoraLogger.Warn($"VideoTextureProvider: could not resolve '{url}'{(string.IsNullOrEmpty(failure) ? string.Empty : $": {failure}")}");
                return;
            }

            var asset = Asset;
            if (asset == null)
                return;

            VideoTitle.SetValueSilently(resolution.Title ?? string.Empty);
            string chosen = engine ?? PickEngine(resolution);
            string source = resolution.StreamUrl.IsFile ? resolution.StreamUrl.LocalPath : resolution.StreamUrl.OriginalString;
            IssueLoad(asset, source, chosen, null, isStream: true, isLive: resolution.IsLive,
                audioSource: resolution.AudioStreamUrl?.OriginalString);
        });
    }

    // The native decoder only handles what the platform's own demuxer handles, which in practice is
    // h264 video in an mp4 container with aac audio. Anything else - vp9, opus, a webm wrapper - goes
    // to the general decoder, which is slower but plays it.
    private static string PickEngine(VideoServiceResolution resolution)
    {
        string video = resolution.Vcodec ?? string.Empty;
        string audio = resolution.Acodec ?? string.Empty;
        bool nativeVideo = video.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
            || video.Contains("h264", StringComparison.OrdinalIgnoreCase);
        bool nativeAudio = audio.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase);
        return nativeVideo && nativeAudio ? MediaServices.EngineNative : MediaServices.EngineLibVlc;
    }

    private void CancelResolve()
    {
        var running = _resolve;
        _resolve = null;
        if (running == null)
            return;
        try
        {
            running.Cancel();
            running.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // FRAME

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        var asset = Asset;
        if (asset == null || !asset.IsReady)
        {
            _audioActive = false;
            return;
        }

        // A world nobody is looking at keeps its playhead running (the clock is shared, so it has to)
        // but stops decoding frames. Decoding for a room you are not in is the whole cost of the
        // feature paid for nothing.
        var world = World;
        bool background = world != null && world.Focus == Lumora.Core.World.WorldFocus.Background;
        bool play = Enabled.Value && Playback.IsPlaying && !background;

        // The one place the audio thread's view of "is this playing" is written.
        _audioActive = play;

        asset.UpdateState(play, Loop, IsStreaming ? 0.0 : Playback.Position, Playback.Speed);

        // Readable, never an event: a change here re-ran the whole asset update, which re-bound the
        // audio sink and flushed its buffer every frame the decoder clock moved. -xlinka
        float error = asset.CurrentClockError;
        if (System.Math.Abs(error - CurrentClockError.Value) > ClockErrorEpsilon)
            CurrentClockError.SetValueSilently(error, change: false);
    }

    // EVENTS
    // Both arrive on a decoder thread, so every line of state they touch is run back on the world.

    private void HandleReady(VideoTexture asset)
    {
        RunSynchronously(() =>
        {
            if (IsDestroyed || !ReferenceEquals(asset, Asset))
                return;

            _isLive = asset.IsLive;
            Playback.Length = asset.IsLive ? -1f : (float)asset.Length;
            LumoraLogger.Debug($"VideoTextureProvider: ready {asset.VideoWidth}x{asset.VideoHeight} live={asset.IsLive}, notifying {AssetReferenceCount} references");
            CurrentPlaybackEngine.SetValueSilently(asset.PlaybackEngine ?? string.Empty);
            ClearError();
            asset.SetAudioSink(this);
            AssetUpdated();

            // A live stream has no frame 0 to pause on; "opened" means "watching". Files still come up
            // paused so a dropped clip does not start blaring. One peer starts it (the transport is
            // synced, everyone else follows), and only once per load: the Ready that upgrades an
            // audio-only verdict to a picture must not restart what is already playing. -xlinka
            if (asset.IsLive && !_autoPlayed && World?.IsAuthority == true && !Playback.IsPlaying)
            {
                _autoPlayed = true;
                Playback.Play();
            }
        });
    }

    private void HandleFrameChanged(VideoTexture asset)
    {
        RunSynchronously(() =>
        {
            if (IsDestroyed || !ReferenceEquals(asset, Asset))
                return;
            LumoraLogger.Debug($"VideoTextureProvider: texture bound {asset.VideoWidth}x{asset.VideoHeight}, rebinding {AssetReferenceCount} references");
            AssetUpdated();
        });
    }

    private void HandleFailed(VideoTexture asset, string reason)
    {
        RunSynchronously(() =>
        {
            if (IsDestroyed)
                return;
            SetError(reason);
            LumoraLogger.Warn($"VideoTextureProvider: playback failed for '{URL.Value}': {reason}");
        });
    }

    private void SetError(string reason) => LoadError.SetValueSilently(reason ?? string.Empty);

    private void ClearError() => LoadError.SetValueSilently(string.Empty);

    private static string? MimeFor(string path)
        => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4"
            : path.EndsWith(".ogv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "video/ogg"
            : null;
}

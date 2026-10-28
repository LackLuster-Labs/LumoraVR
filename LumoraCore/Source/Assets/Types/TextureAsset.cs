// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

// Asset containing texture/image data (RGBA8 with optional mipmaps). URL instances gather and
// decode their image in LoadSelf; procedural instances (atlases, render targets,
// generated textures) are created via InitializeDynamic and fed data through
// SetImageData.
//
// A URL instance resolves a VARIANT before it gathers anything: the descriptor's resolution cap
// plus the source's real dimensions name a generated blob, and if that blob exists locally, on a
// peer or with the content service it is loaded instead of the source file. The base URL is never
// rewritten, so a save that references local://.../hash keeps working whether or not any variant
// was ever generated. -xlinka
//
// The load is progressive. TextureLoadChain turns the one requested variant into an ordered list of
// acceptable answers, smallest first, and each one that lands is put on screen straight away; the
// exact one supersedes it when it arrives. A swap is guarded by rank, so a small rung that comes in
// late can never overwrite a better one that already landed.
public class TextureAsset : ImplementableAsset<ITextureAssetHook>
{
    private byte[] _pixelData = null!;
    private byte[][] _mipLevels = Array.Empty<byte[]>();
    // The bytes behind an address that does not name its content, as a token: part of the GPU cache key.
    private string? _sourceToken;
    private int _width;
    private int _height;
    private bool _hasMipmaps;

    // Longest edge, in pixels, of the best rung already handed to the renderer. -1 means nothing has
    // been delivered yet. The whole guard rail against a progressive load going BACKWARDS is this
    // number plus the check in TryClaimRank.
    private int _deliveredRank = -1;
    private readonly object _deliveryLock = new();

    // Set when the address is a lumora one: the hash its variants are registered and filed under.
    private string? _cloudHash;

    // Cancelled when this asset unloads, taking with it any ladder it queued that has not been made yet: nothing
    // shows this picture any more, and its next load asks again.
    private System.Threading.CancellationTokenSource? _ladderCancel;

    private System.Threading.CancellationToken LadderToken =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _ladderCancel, () => new System.Threading.CancellationTokenSource()).Token;

    // Where a read decodes the pixels again once the CPU copy is gone: the delivered rung's address, or
    // the source when the original was delivered, with what that decode must give back.
    private sealed record ReadbackSource(string? BaseUri, string? VariantUri, TextureVariantId? Variant, bool Hdr,
        int Width, int Height, int Levels, int Version, int Cap);

    private ReadbackSource? _readback;
    private TexturePixels? _heldSnapshot;

    public int Width => _width;

    // For a frame sheet (FrameSheet): how many frames it holds, how many sit side by side in a row, how long
    // each shows, and the average rate for whatever plays it at one speed. One frame and no rate for an
    // ordinary picture.
    public int AnimationFrames { get; private set; } = 1;
    public int AnimationColumns { get; private set; } = 1;
    public float AnimationFps { get; private set; }
    public int[]? AnimationDurationsMs { get; private set; }

    // The size of one frame in the file, whatever this machine's cap made of the sheet: the picture's shape, the
    // same on every peer. Width and Height for a still.
    public int FrameWidth => AnimationFrames > 1 && _frameWidth > 0 ? _frameWidth : _width;
    public int FrameHeight => AnimationFrames > 1 && _frameHeight > 0 ? _frameHeight : _height;
    private int _frameWidth;
    private int _frameHeight;

    // The frame that shows this many seconds into a loop counted from zero.
    public int AnimationFrameAt(double seconds) => AnimationFrames > 1 ? FrameSheet.FrameAt(AnimationDurationsMs, seconds) : 0;

    // Where a frame sits on the sheet, in UV; the whole picture for a still.
    public Math.Rect AnimationFrameRect(int frame) => FrameSheet.FrameRect(frame, AnimationFrames, AnimationColumns);

    public int Height => _height;

    public TextureProfile Profile => (TargetVariant as TextureVariantDescriptor)?.Profile ?? TextureProfile.Slot;

    // The filtering and wrapping a material slot gives this picture in place of its own. Null wrap leaves it
    // to the slot.
    public TextureFilter Filter => (TargetVariant as TextureVariantDescriptor)?.Filter ?? TextureFilter.Slot;
    public (TextureWrapMode U, TextureWrapMode V)? SlotWrap => TargetVariant is TextureVariantDescriptor { UseWrapModes: true } d
        ? (d.WrapU, d.WrapV) : null;

    // The asset still holds its decoded pixels: a procedural texture always, a loaded picture only until
    // the renderer has its copy. Readers go through TryGetPixels either way.
    public bool HoldsPixels => _mipLevels.Length > 0 && _mipLevels[0] != null;

    // A read can give pixels: they are held, or the loaded picture can be decoded again from its file.
    public bool CanReadPixels => HoldsPixels || _readback != null;

    // Mip levels a read gives. One means base level only; the renderer may still generate mips from it.
    public int ReadableLevels => HoldsPixels ? _mipLevels.Length : _readback?.Levels ?? 0;

    // The pixels now, or null while they are decoded again. A null starts that decode, so a later call
    // has them; null for good when the texture has none to read (a camera's or a video's picture).
    public TexturePixels? TryGetPixels() => TexturePixelCache.TryGet(this);

    // The pixels once they are back, or null when the texture has none to read.
    public Task<TexturePixels?> GetPixelsAsync() => TexturePixelCache.GetAsync(this);

    internal bool CanDecodeAgain => _readback != null;

    internal TexturePixels? HeldPixels()
    {
        var levels = _mipLevels;
        if (levels.Length == 0 || levels[0] == null)
            return null;
        var snapshot = _heldSnapshot;
        if (snapshot != null && ReferenceEquals(snapshot.Levels, levels))
            return snapshot;
        snapshot = new TexturePixels(levels, _width, _height, PixelFormat, Version);
        _heldSnapshot = snapshot;
        return snapshot;
    }

    public bool HasMipmaps => _hasMipmaps;

    // Layout of the pixels a read gives. RGBA8 for everything except a floating-point source, which is
    // held as RGBA half floats. Anything that reads the pixels as bytes-per-channel must check this
    // first. -xlinka
    public TextureFormatKind PixelFormat { get; private set; } = TextureFormatKind.RGBA8;

    // The pixels the asset holds right now, mips included: nothing once a loaded picture let them go.
    public long MemorySize
    {
        get
        {
            if (_mipLevels.Length > 1)
            {
                long total = 0;
                foreach (var level in _mipLevels)
                    total += level?.LongLength ?? 0;
                return total;
            }
            return _pixelData?.LongLength ?? 0;
        }
    }

    // Null for a procedural texture that has not been analyzed.
    public TextureMetadata? Metadata { get; private set; }

    // Null when it loaded the source directly.
    public TextureVariantId? LoadedVariant { get; private set; }

    public bool IsVariant => LoadedVariant is { IsOriginal: false };

    // Set by the font atlas builder.
    public bool IsMSDF { get; set; }

    // Matches the font's msdf_pixel_range. Only meaningful when IsMSDF.
    public int MsdfPixelRange { get; set; } = 8;

    // URL (static) instances only; procedural instances set data through SetImageData.
    // WHERE A TEXTURE LOAD ACTUALLY ENDS.
    //
    // This method has several ways out and all but one are silent, so a texture that never reaches
    // the GPU looks exactly like one nobody asked for. Every exit names itself; an "enter" with no
    // matching exit is a load that HUNG, and that is the one shape no amount of after-the-fact
    // inspection can tell you about. Capped so a long session is not drowned. -xlinka
    private static int _traceCount;

    private long _traceStart;

    private void TraceLoad(string stage)
    {
        if (!Logger.EnableDebug)
            return;
        // Elapsed since this texture's own enter, so a slow stage names itself instead of being
        // inferred from how many lines never showed up.
        if (_traceStart == 0)
            _traceStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _traceStart) * 1000 / System.Diagnostics.Stopwatch.Frequency;

        // The cap is STATIC and the engine loads a few hundred UI textures before anything is
        // imported, so a low one is spent long before the interesting textures arrive - which is
        // exactly how a trace that was working produced not one line. -xlinka
        if (System.Threading.Interlocked.Increment(ref _traceCount) > 2500)
            return;
        var url = AssetURL?.OriginalString ?? "?";
        Logger.Debug($"TextureAsset[{(url.Length > 14 ? url.Substring(url.Length - 14) : url)}] +{ms}ms {stage}");
    }

    protected override async Task LoadSelf()
    {
        Loading(1);
        try
        {
            await LoadPictureAsync().ConfigureAwait(false);
        }
        finally
        {
            Loading(-1);
        }
    }

    private async Task LoadPictureAsync()
    {
        TraceLoad("enter");
        TextureLoadTrace.Mark(this, "enter", AssetURL?.OriginalString);
        var descriptor = TargetVariant as TextureVariantDescriptor ?? TextureVariantDescriptor.Default;
        int cap = HardCap(descriptor);
        if (cap != descriptor.MaxSize)
            descriptor = descriptor with { MaxSize = cap };

        var db = Engine?.LocalDB;
        // A cloud texture is worked on under the local address of its hash whether or not this machine
        // holds the original: the sidecar and rungs its uploader registered are filed there, so the
        // ladder below reads them like ones made here. Without the original there is nothing to
        // generate from, and every rung comes over the network. -xlinka
        _cloudHash = AssetURL?.Scheme == Persistence.CloudAssetPacker.CloudScheme ? CloudVariants.HashOf(AssetURL) : null;
        var baseUri = AssetURL?.Scheme == "local" ? AssetURL.OriginalString
            : _cloudHash != null ? CloudVariants.BaseUri(db, _cloudHash) : null;
        bool cloudOnly = _cloudHash != null && (baseUri == null || !db!.Exists(baseUri));
        TextureMetadata? sourceMeta = null;

        if (db != null && baseUri != null)
        {
            // The one await before the source gather, and the only step meshes do not have. If the
            // trace stops between these two lines, this is where it stops.
            TraceLoad("asking for metadata");
            if (_cloudHash != null)
                await CloudVariants.EnsureMetadataAsync(db, baseUri, _cloudHash).ConfigureAwait(false);
            sourceMeta = await TextureVariantStore.TryLoadMetadataAsync(db, baseUri, AssetManager).ConfigureAwait(false);
            if (Logger.EnableDebug)
                TraceLoad($"metadata {(sourceMeta == null ? "none" : sourceMeta.Width + "x" + sourceMeta.Height)}");
            if (sourceMeta != null && TextureVariantStore.MayBeAnimated(db, baseUri, sourceMeta))
            {
                // Recorded before frames were counted, on a kind of file that animates: read the file this once,
                // and the decode records the count.
                sourceMeta = null;
            }
            else if (sourceMeta == null || sourceMeta.Width <= 0)
            {
                // Never analyzed. Generating on the spot would put a full decode plus a stack of mip
                // chains in front of the user, so queue it instead and load the source this time: the
                // cap and the progressive chain start applying from the next load. This is what gives
                // textures that never went through the image importer (a model's embedded maps,
                // anything restored from a save) their variants.
                if (!cloudOnly)
                    TextureVariantStore.EnsureGeneratedInBackground(db, baseUri, descriptor.IsNormalMap, descriptor.GenerateMipmaps, LadderToken);
                sourceMeta = null;
            }
        }

        Task<byte[]>? prefetch = null;

        // The ladder is RGBA8 stills. An HDR or animated source has no rung in it and loads from its own bytes only.
        if (db != null && baseUri != null && sourceMeta != null && !sourceMeta.IsHdr && !sourceMeta.IsAnimated)
        {
            var chain = TextureLoadChain.Build(sourceMeta.Width, sourceMeta.Height, descriptor);
            // A rung of a cloud texture this machine lacks is asked for by name, so ask once which ones
            // its uploader registered.
            if (_cloudHash != null && !chain.TrueForAll(id => id.IsOriginal || db.Exists(TextureVariantStore.GetVariantUri(baseUri, id.Storage)!)))
                await CloudVariants.ListAsync(_cloudHash).ConfigureAwait(false);
            for (int i = 0; i < chain.Count; i++)
            {
                var id = chain[i];
                if (id.IsOriginal || db.Exists(TextureVariantStore.GetVariantUri(baseUri, id.Storage)!))
                    continue;
                if (_cloudHash != null)
                {
                    chain[i] = CloudVariants.ReadableVariant(_cloudHash, id);
                    if (CloudVariants.IsRegistered(_cloudHash, chain[i])) continue;
                }
                var legacy = new TextureVariantId(id.MaxSize, id.Mipmaps, id.Compression, version: 1);
                if (db.Exists(TextureVariantStore.GetVariantUri(baseUri, legacy.Storage)!))
                    chain[i] = legacy;
            }
            var target = chain[chain.Count - 1];
            var targetUri = ResolveReachableVariantUri(db, baseUri, target);
            bool cached = SkipPreviewRungs(db, baseUri, target, sourceMeta, descriptor);

            // The exact rung already compressed on this machine goes from that copy straight to the GPU.
            // Decoding the picture first only for the renderer to swap in its cached copy was the whole
            // cost of a second visit. A rung needs its own file here, for a later read of its pixels.
            if (cached && (target.IsOriginal || (targetUri != null && db.Exists(targetUri)))
                && await InTurn(release => TryDeliverCachedAsync(db, baseUri, target, targetUri, sourceMeta, descriptor, release)).ConfigureAwait(false))
            {
                TraceLoad("exit: delivered from the GPU cache");
                return;
            }

            bool previews = chain.Count > 1 && !cached;

            // Peer-owned, or a cloud texture whose original is not here: strictly one gather at a time,
            // small rung first. The whole point of the preview on a remote asset is to get pixels up
            // before the big transfer finishes, and running both transfers at once just makes them
            // share the same pipe. Locally owned: the "gather" is a disk read, nothing to contend for,
            // so start the exact one now and let it run underneath the previews. -xlinka
            bool remote = !TextureVariantStore.IsOwnedLocally(db, baseUri) || cloudOnly;
            if (previews && !remote)
                prefetch = targetUri != null ? GatherVariant(db, targetUri, target) : GatherSource(db, baseUri);

            if (previews)
            {
                bool delivered = false;
                for (int i = 0; i < chain.Count - 1; i++)
                    delivered |= await TryDeliverVariantAsync(db, baseUri, chain[i], sourceMeta, descriptor, null, final: false)
                        .ConfigureAwait(false);

                // Every rung was missing on an asset we own, which is what an import from before the
                // cheap rung existed looks like. Queue a fill so the next load has something to show
                // while it waits. Generation is deduplicated internally, so spamming this is free.
                if (!delivered && !remote)
                    TextureVariantStore.EnsureGeneratedInBackground(db, baseUri, descriptor.IsNormalMap, descriptor.GenerateMipmaps, LadderToken);
            }

            if (Logger.EnableDebug)
                TraceLoad($"variant path previews={previews} targetUri={targetUri != null}");
            if (targetUri != null
                && await TryDeliverVariantAsync(db, baseUri, target, sourceMeta, descriptor, prefetch, final: true)
                    .ConfigureAwait(false))
            {
                TraceLoad("exit: delivered variant");
                return;
            }

            // The exact variant turned out to be unreadable, so the source below is the answer. Any
            // prefetch we started was aimed at the variant address and is not it; drop it and gather
            // the source instead. When targetUri was null all along the prefetch already IS the
            // source gather, so leave that one alone.
            if (targetUri != null)
                prefetch = null;
        }

        TraceLoad("gathering source");
        var bytes = await (prefetch ?? GatherSource(db, baseUri)).ConfigureAwait(false);
        if (Logger.EnableDebug)
            TraceLoad($"gathered {(bytes?.Length ?? -1)} bytes");
        TextureLoadTrace.Mark(this, "gathered", "source " + (bytes?.Length ?? -1));
        if (bytes == null || bytes.Length == 0)
        {
            // A delivered preview is a usable texture. Failing the whole asset here would tear that
            // back off screen to replace it with nothing, which is strictly worse than staying on the
            // rung we already have.
            if (_deliveredRank >= 0)
            {
                Logger.Warn($"TextureAsset: no source data for {AssetURL}; staying on the {_deliveredRank}px rung");
                return;
            }
            FailLoad($"No image data gathered for {AssetURL}");
            return;
        }

        TextureLoadTrace.Mark(this, "turn-wait");
        await InTurn(release => DeliverSourceAsync(bytes, descriptor, sourceMeta, baseUri, release)).ConfigureAwait(false);
    }

    private async Task DeliverSourceAsync(byte[] bytes, TextureVariantDescriptor descriptor, TextureMetadata? sourceMeta, string? baseUri,
        Action release)
    {
        TextureLoadTrace.Mark(this, "turn", "source");
        bool normalMap = descriptor.IsNormalMap || (sourceMeta?.IsNormalMap ?? false);

        if (TextureMetadata.DetectHdr(bytes))
        {
            await LoadHdrSourceAsync(bytes, descriptor, normalMap, baseUri).ConfigureAwait(false);
            return;
        }

        int width, height;
        FrameSheet? sheet;
        bool reduced = false;
        byte[]? rgba;
        try
        {
            // Decoding a full-resolution source image is real CPU work, and this line runs on whichever
            // thread completed the gather. Say where it goes instead of taking whatever the completer
            // was standing on. -xlinka
            var decoded = await DecodeOffThread(() => DecodePicture(bytes, descriptor.MaxSize)).ConfigureAwait(false);
            reduced = decoded.Reduced;
            rgba = decoded.Pixels;
            width = decoded.Width;
            height = decoded.Height;
            sheet = decoded.Sheet;
        }
        catch (Exception ex)
        {
            if (_deliveredRank >= 0)
            {
                Logger.Warn($"TextureAsset: cannot decode {AssetURL} ({ex.Message}); staying on the {_deliveredRank}px rung");
                return;
            }
            FailLoad($"Failed to decode image {AssetURL}: {ex.Message}");
            return;
        }

        TextureLoadTrace.Mark(this, "decoded", width + "x" + height);
        if (rgba == null || width <= 0 || height <= 0)
        {
            if (_deliveredRank >= 0)
                return;
            FailLoad($"Failed to decode image {AssetURL}: no pixels");
            return;
        }

        int mipCount = descriptor.GenerateMipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
        var metadata = TextureMetadata.Analyze(
            rgba, width, height, mipCount,
            bytes.LongLength,
            descriptor.GenerateMipmaps
                ? TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA8, width, height, mipCount)
                : (long)width * height * 4,
            TextureMetadata.DetectSRgb(bytes),
            normalMap);
        if (sheet != null)
            metadata = metadata.WithFrames(TextureVariantStore.SourceFrames(bytes));

        // Never analyzed before: what this load just measured is filed for the next one. An animated source is
        // filed at its frame size.
        var db = Engine?.LocalDB;
        if (sourceMeta == null && db != null && baseUri != null && TextureVariantStore.IsOwnedLocally(db, baseUri))
        {
            int sourceWidth = width, sourceHeight = height;
            if (sheet != null)
                (sourceWidth, sourceHeight) = (sheet.FrameWidth, sheet.FrameHeight);
            if (sheet != null || !reduced || ImageHeader.TryProbe(bytes, out _, out sourceWidth, out sourceHeight))
                _ = TextureVariantStore.RecordAnalysisAsync(db, baseUri, metadata, sourceWidth, sourceHeight);
        }

        // Nothing on screen yet: a small copy of what was just decoded goes up first, so the picture shows
        // while the full one is filtered and compressed.
        var preview = _deliveredRank < 0 && sheet == null ? SubmitPreview(rgba, width, height, metadata) : null;

        // Reduced under the cap, it is the rung of that size, decoded from the source.
        var original = new TextureVariantId(reduced ? descriptor.MaxSize : 0, descriptor.GenerateMipmaps, descriptor.Compression);
        if (!TryClaimRank(System.Math.Max(width, height)))
            return;

        _sourceToken = TextureGpuCache.NamesItsContent(AssetURL?.OriginalString) ? null : TextureGpuCache.SourceToken(bytes);
        Metadata = metadata;
        LoadedVariant = original;

        SetAnimation(sheet);
        var prepared = NextPrepared();
        SetImageData(rgba, width, height, descriptor.GenerateMipmaps);
        int version = Version;
        _readback = new ReadbackSource(baseUri, null, null, false, width, height, 1, version, descriptor.MaxSize);
        Hook?.SetWrapMode(descriptor.WrapU, descriptor.WrapV);

        // Don't let LoadSelf return (-> FullyLoaded) until the GPU texture is actually built. SetImageData ->
        // Hook.UploadData only QUEUES a deferred main-thread build; if we reported loaded now, the asset's single
        // load-complete notification would fire while Hook.IsValid is still false, the consuming material would bind
        // a null albedo, and nothing re-binds it (white body). Awaiting the upload makes that notification fire when
        // the texture is genuinely usable: the asset isn't marked loaded until the GPU upload completes. -xlinka
        if (Logger.EnableDebug)
            TraceLoad($"decoded {width}x{height}, awaiting GPU upload (hook={Hook != null})");
        if (Hook != null)
        {
            var landed = Hook.WaitForUploadAsync();
            if (preview != null)
            {
                await Task.WhenAny(preview, landed).ConfigureAwait(false);
                if (preview.IsCompleted && !landed.IsCompleted)
                    ReportPartiallyLoaded();
            }
            await LetGoAsync(prepared, landed, version, release).ConfigureAwait(false);
            await landed.ConfigureAwait(false);
        }
        TextureLoadTrace.Mark(this, "uploaded", "source " + width + "x" + height);
        ReleaseCpuCopy(version);
        TraceLoad("exit: uploaded");
    }

    // Nothing comes out with an edge above cap (see CappedImageDecoder). An animated source becomes a FrameSheet
    // under the same cap: fewer pixels a frame, never fewer frames. One that holds a single picture is a still,
    // decoded the way every still is. Sheet is null for a still.
    private static (byte[]? Pixels, int Width, int Height, FrameSheet? Sheet, bool Reduced) DecodePicture(byte[] bytes, int cap)
    {
        FrameSheet? sheet = null;
        var animated = TextureVariantStore.AnimatedDecoder;
        if (GifDecoder.IsGif(bytes) && GifDecoder.CountFrames(bytes, 2) > 1)
            sheet = GifDecoder.DecodeSheet(bytes, cap);
        else if (animated != null && WebpStill.CountFrames(bytes, 2) > 1)
            sheet = animated(bytes, cap);
        if (sheet != null && sheet.Frames > 1)
            return (sheet.Pixels, sheet.Width, sheet.Height, sheet, sheet.Reduced);
        var pixels = CappedImageDecoder.DecodeRgba(bytes, cap, out int w, out int h, out int sourceWidth, out int sourceHeight);
        return (pixels, w, h, null, w != sourceWidth || h != sourceHeight);
    }

    private void SetAnimation(FrameSheet? sheet)
    {
        bool animated = sheet != null && sheet.Frames > 1;
        _frameWidth = animated ? sheet!.FrameWidth : 0;
        _frameHeight = animated ? sheet!.FrameHeight : 0;
        AnimationColumns = animated ? sheet!.Columns : 1;
        AnimationDurationsMs = animated ? sheet!.DurationsMs : null;
        AnimationFps = animated ? sheet!.Fps : 0f;
        AnimationFrames = animated ? sheet!.Frames : 1;
    }

    // The provider's cap, and never above this machine's: the setting is a memory ceiling, so nothing
    // that names its own size gets past it.
    private static int HardCap(TextureVariantDescriptor descriptor)
    {
        int ceiling = EngineSettings.MaxTextureSize;
        int asked = descriptor.MaxSize;
        return ceiling > 0 && (asked <= 0 || asked > ceiling) ? ceiling : asked;
    }

    // Floating-point sources: decode to half, keep the full range all the way to the renderer. No
    // ladder, no preview rung, and the resolution cap does not apply, because every one of those
    // is an RGBA8 mechanism and an RGBA8 copy of a radiance map is a different, clipped picture.
    // Decode failure here fails the load outright rather than falling back to the 8-bit decoder,
    // which would put that clipped picture on screen and call it done. -xlinka
    private async Task LoadHdrSourceAsync(byte[] bytes, TextureVariantDescriptor descriptor, bool normalMap, string? baseUri)
    {
        int width, height;
        bool reduced;
        byte[]? half;
        try
        {
            var decoded = await DecodeOffThread(() =>
            {
                var pixels = CappedImageDecoder.DecodeRgbaHalf(bytes, descriptor.MaxSize, out int w, out int h, out int sw, out int sh);
                return (Pixels: pixels, Width: w, Height: h, Reduced: w != sw || h != sh);
            }).ConfigureAwait(false);
            half = decoded.Pixels;
            width = decoded.Width;
            height = decoded.Height;
            reduced = decoded.Reduced;
        }
        catch (Exception ex)
        {
            FailLoad($"Failed to decode HDR image {AssetURL}: {ex.Message}");
            return;
        }

        if (half == null || width <= 0 || height <= 0)
        {
            FailLoad($"Failed to decode HDR image {AssetURL}: no HDR decoder for this format on this build");
            return;
        }

        int mipCount = descriptor.GenerateMipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
        var metadata = TextureMetadata.AnalyzeHalf(
            half, width, height, mipCount,
            bytes.LongLength,
            TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA16F, width, height, mipCount),
            normalMap);

        if (!TryClaimRank(System.Math.Max(width, height)))
            return;

        _sourceToken = TextureGpuCache.NamesItsContent(AssetURL?.OriginalString) ? null : TextureGpuCache.SourceToken(bytes);
        Metadata = metadata;
        LoadedVariant = new TextureVariantId(reduced ? descriptor.MaxSize : 0, descriptor.GenerateMipmaps, descriptor.Compression);

        SetHdrImageData(half, width, height, descriptor.GenerateMipmaps);
        int version = Version;
        _readback = new ReadbackSource(baseUri, null, null, true, width, height, 1, version, descriptor.MaxSize);
        Hook?.SetWrapMode(descriptor.WrapU, descriptor.WrapV);

        if (Hook != null)
            await Hook.WaitForUploadAsync().ConfigureAwait(false);
        ReleaseCpuCopy(version);
    }

    private Task<byte[]> StartGather(Uri url) => AssetManager.RequestGather(url);

    // The original. A cloud address names no machine to gather from, so it is this machine's copy when
    // there is one, and otherwise fetched from the service and kept.
    private Task<byte[]> GatherSource(LocalDB? db, string? baseUri)
    {
        if (_cloudHash == null)
            return StartGather(AssetURL!);
        if (db != null && baseUri != null && db.Exists(baseUri))
            return StartGather(new Uri(baseUri));
        return FetchCloudOriginal(db, _cloudHash);
    }

    // A rung this machine does not hold comes from the service for a cloud texture, and from its owner
    // for anything else.
    private Task<byte[]> GatherVariant(LocalDB db, string variantUri, TextureVariantId id) =>
        _cloudHash != null && !db.Exists(variantUri) ? FetchCloudVariant(_cloudHash, id) : StartGather(new Uri(variantUri));

    private static async Task<byte[]> FetchCloudOriginal(LocalDB? db, string hash) =>
        (await CloudVariants.FetchOriginalAsync(db, hash).ConfigureAwait(false))!;

    private static async Task<byte[]> FetchCloudVariant(string hash, TextureVariantId id) =>
        (await CloudVariants.FetchVariantAsync(hash, id).ConfigureAwait(false))!;

    // The address of a variant we can actually get hold of, or null if asking would be a wasted trip.
    //
    // Held locally: straight read. Owned by a peer: worth asking for, because a variant is exactly
    // what we want crossing the wire instead of a full-resolution original, and a peer that hasn't
    // generated it answers "not available" and we fall through. Owned by US and missing: nobody else
    // has it either. The original is never a "variant" address - that is the base URI. -xlinka
    private string? ResolveReachableVariantUri(LocalDB db, string baseUri, TextureVariantId id)
    {
        if (id.IsOriginal)
            return null;

        var uri = TextureVariantStore.GetVariantUri(baseUri, id.Storage);
        if (uri == null)
            return null;

        if (db.Exists(uri))
            return uri;

        // A cloud texture's rung is worth a trip when its uploader registered it.
        if (_cloudHash != null)
            return CloudVariants.IsRegistered(_cloudHash, id) ? uri : null;

        if (TextureVariantStore.IsOwnedLocally(db, baseUri) || Networking.Session.SessionAssetTransferer.ForLocalAsset(baseUri) == null)
            return null;

        return uri;
    }

    // Skip the cheap rungs entirely when the exact one is already compressed and sitting in the GPU
    // cache. On a revisit that path is a file read plus one upload, so previews would only buy a
    // flicker of blur and an extra upload nobody asked for. -xlinka
    private bool SkipPreviewRungs(LocalDB db, string baseUri, TextureVariantId target, TextureMetadata sourceMeta, TextureVariantDescriptor descriptor)
    {
        if (descriptor.Compression != TextureCompressionKind.Block)
            return false;

        var (width, height) = target.ResolveSize(sourceMeta.Width, sourceMeta.Height);
        int mipCount = descriptor.GenerateMipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
        string intent = TextureGpuCache.Intent(
            sourceMeta.HasAlpha, descriptor.IsNormalMap || sourceMeta.IsNormalMap, sourceMeta.IsHdr);
        // Keyed the way Upload stores it, by the address this texture was asked for. An address that does not
        // name its content cannot vouch for a cached blob before its bytes are read.
        string uri = AssetURL?.OriginalString ?? baseUri;
        if (!TextureGpuCache.NamesItsContent(uri) && _sourceToken == null)
            return false;
        return TextureGpuCache.IsCached(db, CacheIdentity(uri), target, width, height, mipCount, intent);
    }

    private string CacheIdentity(string uri) => _sourceToken == null ? uri : uri + "#" + _sourceToken;

    // Fetch one rung and put it on screen. gather is a transfer already in flight for this rung's
    // address, or null to start one here.
    private async Task<bool> TryDeliverVariantAsync(
        LocalDB db,
        string baseUri,
        TextureVariantId id,
        TextureMetadata sourceMeta,
        TextureVariantDescriptor descriptor,
        Task<byte[]>? gather,
        bool final)
    {
        var variantUri = gather != null ? TextureVariantStore.GetVariantUri(baseUri, id.Storage) : ResolveReachableVariantUri(db, baseUri, id);
        if (variantUri == null)
            return false;

        // Nothing to gain from a rung that is no better than what is already up.
        int rank = TextureLoadChain.Rank(id, System.Math.Max(sourceMeta.Width, sourceMeta.Height));
        if (rank <= _deliveredRank)
            return false;

        bool local = db.Exists(variantUri);
        byte[]? blob;
        try
        {
            blob = await (gather ?? GatherVariant(db, variantUri, id)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"TextureAsset: variant fetch failed for {variantUri}: {ex.Message}");
            return false;
        }

        TextureLoadTrace.Mark(this, "gathered", id.Identifier + " " + (blob?.Length ?? -1));
        TextureLoadTrace.Mark(this, "turn-wait");
        return await InTurn(release => DeliverVariantAsync(db, baseUri, id, sourceMeta, descriptor, blob, variantUri, local, rank, final, release))
            .ConfigureAwait(false);
    }

    private async Task<bool> DeliverVariantAsync(LocalDB db, string baseUri, TextureVariantId id, TextureMetadata sourceMeta,
        TextureVariantDescriptor descriptor, byte[]? blob, string variantUri, bool local, int rank, bool final, Action release)
    {
        TextureLoadTrace.Mark(this, "turn", id.Identifier);
        var data = await DecodeOffThread(() => TextureVariantStore.Decode(blob)).ConfigureAwait(false);
        TextureLoadTrace.Mark(this, "decoded", data == null ? "none" : data.Width + "x" + data.Height);
        if (data == null)
        {
            if (blob != null && final)
                Logger.Warn($"TextureAsset: variant {id.Identifier} of {baseUri} is unreadable; using the source");
            return false;
        }

        // A peer-supplied variant is written into our cache under the SAME derived address, so the
        // next load resolves it locally. Without this the generic receive path would re-key it by
        // content hash and every session would pull it again. -xlinka
        if (!local && blob != null)
        {
            await db.SaveDerivedAssetAsync(baseUri, id.Storage.Identifier, blob, TextureVariantStore.VariantExtension, received: true)
                .ConfigureAwait(false);
        }

        if (!TryClaimRank(rank))
            return false;

        Metadata = new TextureMetadata
        {
            Width = data.Width,
            Height = data.Height,
            ContentFormat = data.ContentFormat,
            HasAlpha = data.HasAlpha,
            MipCount = data.MipCount,
            SourceBytes = blob?.LongLength ?? 0,
            DecodedBytes = data.DecodedBytes,
            SRgb = sourceMeta.SRgb,
            IsNormalMap = descriptor.IsNormalMap || sourceMeta.IsNormalMap,
            VariantId = id.Identifier,
        };
        LoadedVariant = id;

        var prepared = NextPrepared();
        SetMipData(data.Levels, data.Width, data.Height);
        int version = Version;
        _readback = new ReadbackSource(baseUri, variantUri, id, false, data.Width, data.Height, data.Levels.Length, version, 0);
        Hook?.SetWrapMode(descriptor.WrapU, descriptor.WrapV);

        if (Hook != null)
        {
            var landed = Hook.WaitForUploadAsync();
            await LetGoAsync(prepared, landed, version, release).ConfigureAwait(false);
            await landed.ConfigureAwait(false);
        }
        TextureLoadTrace.Mark(this, "uploaded", (final ? "final " : "rung ") + data.Width + "x" + data.Height);
        ReleaseCpuCopy(version);

        // Only after the GPU texture exists, for the same reason the final rung waits: a requester
        // told to re-bind while the hook is still invalid binds nothing and never comes back.
        if (!final)
            ReportPartiallyLoaded();

        return true;
    }

    // A cold load's first picture: the source cut to the preview rung's size, uncompressed and with its mips,
    // which is a few kilobytes and well under a millisecond on the main thread. Null when the source is small
    // enough to go up whole as quickly. The renderer applies uploads in the order they were asked for, so
    // this can never land over the full picture.
    private Task? SubmitPreview(byte[] rgba, int width, int height, TextureMetadata metadata)
    {
        var hook = Hook;
        if (hook == null || System.Math.Max(width, height) <= TextureVariantId.PreviewSize * 2)
            return null;
        var (previewWidth, previewHeight) = BoxReducer.Fit(width, height, TextureVariantId.PreviewSize);
        var small = BoxReducer.Reduce(rgba, width, height, previewWidth, previewHeight);
        if (!TryClaimRank(System.Math.Max(previewWidth, previewHeight)))
            return null;
        hook.UploadTexture(new TextureUploadRequest
        {
            MipLevels = new[] { small },
            Width = previewWidth,
            Height = previewHeight,
            GenerateMipmaps = true,
            AllowBlockCompression = false,
            HasAlpha = metadata.HasAlpha,
            IsNormalMap = metadata.IsNormalMap,
            SRgb = metadata.SRgb,
        });
        TextureLoadTrace.Mark(this, "preview", previewWidth + "x" + previewHeight);
        return hook.WaitForUploadAsync();
    }

    // The target rung from the renderer's compressed copy, no pixels decoded. False, with nothing changed,
    // when that copy turned out to be missing or unreadable; the caller then delivers pixels as usual.
    private async Task<bool> TryDeliverCachedAsync(LocalDB db, string baseUri, TextureVariantId target, string? targetUri,
        TextureMetadata sourceMeta, TextureVariantDescriptor descriptor, Action release)
    {
        var hook = Hook;
        int rank = TextureLoadChain.Rank(target, System.Math.Max(sourceMeta.Width, sourceMeta.Height));
        if (hook == null || rank <= _deliveredRank)
            return false;

        var (width, height) = target.ResolveSize(sourceMeta.Width, sourceMeta.Height);
        int mipCount = descriptor.GenerateMipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
        bool normalMap = descriptor.IsNormalMap || sourceMeta.IsNormalMap;
        var metadata = new TextureMetadata
        {
            Width = width,
            Height = height,
            ContentFormat = sourceMeta.ContentFormat,
            HasAlpha = sourceMeta.HasAlpha,
            MipCount = mipCount,
            SourceBytes = sourceMeta.SourceBytes,
            DecodedBytes = TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA8, width, height, mipCount),
            SRgb = sourceMeta.SRgb,
            IsNormalMap = normalMap,
            VariantId = target.IsOriginal ? null : target.Identifier,
        };
        var request = new TextureUploadRequest
        {
            Width = width,
            Height = height,
            GenerateMipmaps = descriptor.GenerateMipmaps,
            AllowBlockCompression = true,
            HasAlpha = metadata.HasAlpha,
            IsNormalMap = normalMap,
            SRgb = metadata.SRgb,
            CacheKey = TextureGpuCache.BuildKey(CacheIdentity(AssetURL?.OriginalString ?? baseUri), target, width, height, mipCount,
                TextureGpuCache.Intent(metadata.HasAlpha, normalMap, false)),
            CacheDirectory = db.GetGpuCachePath(),
            Report = ReportUploaded,
        };

        // Set before the upload is queued: the renderer reports the format it built into these.
        var hadMetadata = Metadata;
        var hadVariant = LoadedVariant;
        Metadata = metadata;
        LoadedVariant = target;
        TextureLoadTrace.Mark(this, "turn", "cached");
        if (!await hook.UploadCachedAsync(request).ConfigureAwait(false))
        {
            Metadata = hadMetadata;
            LoadedVariant = hadVariant;
            return false;
        }

        TryClaimRank(rank);
        int version;
        lock (_deliveryLock)
        {
            _width = width;
            _height = height;
            _hasMipmaps = mipCount > 1;
            PixelFormat = TextureFormatKind.RGBA8;
            version = ++Version;
        }
        _readback = target.IsOriginal
            ? new ReadbackSource(baseUri, null, null, false, width, height, 1, version, descriptor.MaxSize)
            : new ReadbackSource(baseUri, targetUri, target, false, width, height, mipCount, version, 0);
        hook.SetWrapMode(descriptor.WrapU, descriptor.WrapV);
        var landed = hook.WaitForUploadAsync();
        release();
        await landed.ConfigureAwait(false);
        TextureLoadTrace.Mark(this, "uploaded", "cached " + width + "x" + height);
        return true;
    }

    // DECODED PICTURES IN FLIGHT.
    //
    // A world asks for all of its textures at once, and a decoded picture is held until its upload lands.
    // Without a limit the decodes ran far ahead of the uploads, so for the whole of a load the process held
    // nearly every texture of the world decoded at the same time, gigabytes for a big world, on top of the
    // renderer's copies being prepared. A delivery takes a turn from its decode until the renderer holds its
    // own copy (compressed, if it compresses) and the decoded one is let go. The gather before it takes none,
    // so a slow transfer never holds a turn. -xlinka
    private static System.Threading.SemaphoreSlim? _deliveryTurns;

    private static System.Threading.SemaphoreSlim DeliveryTurns => System.Threading.LazyInitializer.EnsureInitialized(ref _deliveryTurns,
        () => new System.Threading.SemaphoreSlim(Lumora.Core.Engine.UseConstrainedResources ? 2 : System.Math.Clamp(Environment.ProcessorCount / 2, 2, 6)));

    // The wait for the main thread to put a picture on the GPU holds no turn: a delivery hands its turn back
    // through release before that. Held through it, six pictures stuck behind an import's mesh builds kept
    // every other picture from even decoding, for minutes. -xlinka
    private static async Task InTurn(Func<Action, Task> delivery)
    {
        var turns = DeliveryTurns;
        await turns.WaitAsync().ConfigureAwait(false);
        int released = 0;
        void Release()
        {
            if (System.Threading.Interlocked.Exchange(ref released, 1) == 0)
                turns.Release();
        }
        try
        {
            await delivery(Release).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    private static async Task<T> InTurn<T>(Func<Action, Task<T>> delivery)
    {
        var turns = DeliveryTurns;
        await turns.WaitAsync().ConfigureAwait(false);
        int released = 0;
        void Release()
        {
            if (System.Threading.Interlocked.Exchange(ref released, 1) == 0)
                turns.Release();
        }
        try
        {
            return await delivery(Release).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    // The renderer's signal for the next upload that it holds its own copy and the CPU side is done.
    private Action? _prepared;

    private Task NextPrepared()
    {
        var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prepared = () => prepared.TrySetResult(true);
        return prepared.Task;
    }

    // A renderer that never signals is covered by the landing, which implies it.
    private async Task LetGoAsync(Task prepared, Task landed, int version, Action release)
    {
        await Task.WhenAny(prepared, landed).ConfigureAwait(false);
        ReleaseCpuCopy(version);
        release();
    }

    // Completes whenever no picture is loading. Background work that only helps a later load (the variant
    // ladder) waits for it, so it never takes a core from a picture someone is waiting to see.
    internal static Task LoadsIdle
    {
        get
        {
            lock (_idleLock)
                return _idle.Task;
        }
    }

    private static readonly object _idleLock = new();
    private static int _loading;
    private static TaskCompletionSource<bool> _idle = Idle();

    private static TaskCompletionSource<bool> Idle()
    {
        var idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult(true);
        return idle;
    }

    private static void Loading(int change)
    {
        lock (_idleLock)
        {
            int was = _loading;
            _loading += change;
            if (was == 0 && _loading > 0)
                _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            else if (was > 0 && _loading == 0)
                _idle.TrySetResult(true);
        }
    }

    // The renderer has its copy now, or there is no renderer. Keeping the decoded picture as well cost a
    // world of three hundred textures gigabytes that nothing read; a reader gets it back through
    // TryGetPixels, decoded again from the file it came from. Only a loaded picture lets go: a
    // procedural texture has no file to decode from. The version check leaves a newer delivery that
    // landed during the upload alone. -xlinka
    private void ReleaseCpuCopy(int version)
    {
        if (AssetType != AssetType.Static || _readback == null)
            return;
        lock (_deliveryLock)
        {
            if (Version != version)
                return;
            _pixelData = null!;
            _mipLevels = Array.Empty<byte[]>();
            _heldSnapshot = null;
        }
    }

    // Gathered and decoded the way the delivered picture was, off the world thread, the decode in a
    // delivery turn so a script reading a hundred textures at once does not decode them all at once.
    // Null when the file is gone or no longer decodes to the size that was delivered.
    internal async Task<TexturePixels?> DecodeAgainAsync()
    {
        var source = _readback;
        if (source == null)
            return null;
        var db = Engine?.LocalDB;
        var gather = source.Variant is { } id && source.VariantUri != null
            ? db != null ? GatherVariant(db, source.VariantUri, id) : StartGather(new Uri(source.VariantUri))
            : GatherSource(db, source.BaseUri);
        var bytes = await gather.ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
            return null;
        return await InTurn(_ => DecodeOffThread(() => DecodeForRead(bytes, source))).ConfigureAwait(false);
    }

    private static TexturePixels? DecodeForRead(byte[] bytes, ReadbackSource source)
    {
        byte[][]? levels;
        int width, height;
        var format = TextureFormatKind.RGBA8;
        if (source.Variant != null)
        {
            var data = TextureVariantStore.Decode(bytes);
            levels = data?.Levels;
            width = data?.Width ?? 0;
            height = data?.Height ?? 0;
        }
        else if (source.Hdr)
        {
            var half = CappedImageDecoder.DecodeRgbaHalf(bytes, source.Cap, out width, out height, out _, out _);
            levels = half == null ? null : new[] { half };
            format = TextureFormatKind.RGBA16F;
        }
        else
        {
            var decoded = DecodePicture(bytes, source.Cap);
            levels = decoded.Pixels == null ? null : new[] { decoded.Pixels };
            width = decoded.Width;
            height = decoded.Height;
        }

        if (levels == null || levels.Length == 0 || width != source.Width || height != source.Height)
        {
            Logger.Warn($"TexturePixels: the file behind a {source.Width}x{source.Height} texture no longer decodes to it");
            return null;
        }
        return new TexturePixels(levels, width, height, format, source.Version);
    }

    // Claim the right to replace the contents at this quality. Losing the claim means something
    // better already landed - a late preview must never overwrite it, so the caller drops its
    // payload on the floor and leaves the good one alone.
    private bool TryClaimRank(int rank)
    {
        lock (_deliveryLock)
        {
            if (rank <= _deliveredRank)
                return false;
            _deliveredRank = rank;
            return true;
        }
    }

    // Expects RGBA8, 4 bytes per pixel.
    public void SetImageData(byte[] pixels, int width, int height, bool mipmaps = false)
    {
        if (pixels == null)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        int expectedSize = width * height * 4; // RGBA8
        if (pixels.Length < expectedSize)
        {
            throw new ArgumentException($"Pixel data too small. Expected at least {expectedSize} bytes, got {pixels.Length}");
        }

        _pixelData = pixels;
        _mipLevels = new[] { pixels };
        _width = width;
        _height = height;
        _hasMipmaps = mipmaps;
        PixelFormat = TextureFormatKind.RGBA8;
        Version++;

        Upload(generateMipmaps: mipmaps);
    }

    // RGBA half floats, 8 bytes per pixel, rows top-down like everything else.
    public void SetHdrImageData(byte[] rgbaHalf, int width, int height, bool mipmaps = false)
    {
        if (rgbaHalf == null)
            throw new ArgumentNullException(nameof(rgbaHalf));

        long expectedSize = (long)width * height * 8;
        if (rgbaHalf.LongLength < expectedSize)
            throw new ArgumentException($"Pixel data too small. Expected at least {expectedSize} bytes, got {rgbaHalf.LongLength}");

        _pixelData = rgbaHalf;
        _mipLevels = new[] { rgbaHalf };
        _width = width;
        _height = height;
        _hasMipmaps = mipmaps;
        PixelFormat = TextureFormatKind.RGBA16F;
        Version++;

        Upload(generateMipmaps: mipmaps);
    }

    // Used by the variant path, where the chain was filtered once at generation time and every machine that
    // loads it gets the same levels instead of re-deriving them.
    public void SetMipData(byte[][] levels, int width, int height)
    {
        if (levels == null || levels.Length == 0)
            throw new ArgumentException("At least the base mip level is required", nameof(levels));

        _mipLevels = levels;
        _pixelData = levels[0];
        _width = width;
        _height = height;
        _hasMipmaps = levels.Length > 1;
        PixelFormat = TextureFormatKind.RGBA8;
        Version++;

        Upload(generateMipmaps: false);
    }

    private void Upload(bool generateMipmaps)
    {
        var hook = Hook;
        if (hook == null)
            return;

        var metadata = Metadata;
        string? cacheKey = null;
        string? cacheDirectory = null;

        // Only a URL-backed texture gets a compressed-cache slot: its bytes are addressed by a
        // stable URI, so the same key always means the same pixels. A procedural texture is
        // regenerated by whatever built it and has no such identity to key on.
        if (AssetURL != null && metadata != null)
        {
            var db = Engine?.LocalDB;
            if (db != null)
            {
                cacheKey = TextureGpuCache.BuildKey(
                    CacheIdentity(AssetURL.OriginalString), LoadedVariant,
                    metadata.Width, metadata.Height, metadata.MipCount,
                    TextureGpuCache.Intent(metadata.HasAlpha, metadata.IsNormalMap, metadata.IsHdr));
                cacheDirectory = db.GetGpuCachePath();
            }
        }

        hook.UploadTexture(new TextureUploadRequest
        {
            MipLevels = _mipLevels,
            Width = _width,
            Height = _height,
            GenerateMipmaps = generateMipmaps,
            AllowBlockCompression = LoadedVariant?.Compression == TextureCompressionKind.Block,
            HasAlpha = metadata?.HasAlpha ?? true,
            IsNormalMap = metadata?.IsNormalMap ?? false,
            SRgb = metadata?.SRgb,
            IsHdr = PixelFormat == TextureFormatKind.RGBA16F,
            CacheKey = cacheKey,
            CacheDirectory = cacheDirectory,
            Report = ReportUploaded,
            Prepared = System.Threading.Interlocked.Exchange(ref _prepared, null),
        });
    }

    // This is the only way GpuFormat is ever set, which is why the VRAM figures downstream are measurements and
    // not estimates.
    public void ReportUploaded(TextureFormatKind gpuFormat, int mipCount, long gpuBytes)
    {
        var metadata = Metadata;
        if (metadata == null)
            return;
        metadata.GpuFormat = gpuFormat;
        _hasMipmaps = mipCount > 1;
    }

    public static TextureAsset FromRgba(byte[] rgbaData, int width, int height, bool mipmaps = false)
    {
        var texture = new TextureAsset();
        texture.InitializeDynamic();
        texture.SetImageData(rgbaData, width, height, mipmaps);
        return texture;
    }

    public override void Unload()
    {
        SetAnimation(null);
        _pixelData = null!;
        _mipLevels = Array.Empty<byte[]>();
        _width = 0;
        _height = 0;
        _hasMipmaps = false;
        PixelFormat = TextureFormatKind.RGBA8;
        Metadata = null;
        _sourceToken = null;
        LoadedVariant = null;
        _readback = null;
        _heldSnapshot = null;
        TexturePixelCache.Forget(this);
        System.Threading.Interlocked.Exchange(ref _ladderCancel, null)?.Cancel();
        lock (_deliveryLock)
            _deliveredRank = -1;
        base.Unload();
    }
}

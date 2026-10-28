// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(TextureAsset))]
public class TextureAssetHook : AssetHook, ITextureAssetHook, IGodotTexture
{
    private ImageTexture _godotTexture = null!;
    private TextureWrapMode _wrapU = TextureWrapMode.Repeat;
    private TextureWrapMode _wrapV = TextureWrapMode.Repeat;

    // Completed at the END of the deferred BuildTexture (when _godotTexture actually exists). TextureAsset.LoadSelf
    // awaits this before reporting the asset loaded, so a material can't bind this texture while it's still null. -xlinka
    private System.Threading.Tasks.TaskCompletionSource<bool> _uploadTcs = null!;

    // bumping this invalidates every cached blob
    // 2: compressed caches written from bottom-up rows (before the decode flip was removed) are stale.
    // 3: the format is now chosen per texture (alpha, normal map, HDR) and per device capability;
    //    a version-2 blob is BC7 for everything and would be handed back under the new policy.
    private const int GpuCacheVersion = 3;

    public ImageTexture GodotTexture => _godotTexture;

    public Texture2D GodotTexture2D => _godotTexture;

    public bool IsValid => _godotTexture != null;

    public void UploadData(byte[] pixels, int width, int height, bool hasMipmaps)
    {
        // Godot RenderingServer resource creation must run on the main thread, but this is invoked INLINE from the
        // off-main asset-load thread (TextureAsset.SetImageData). Touching the renderer off-thread is an intermittent
        // stall/corruption bug; defer the actual build to the main thread (same pattern as RenderTextureHook). The
        // TCS completes when that deferred build has run, so LoadSelf can wait for the GPU texture to truly exist
        // before reporting the asset loaded (else a material binds a null albedo in the gap = white body). -xlinka
        var tcs = NewUploadTcs();
        System.Threading.Interlocked.Increment(ref _uploadsQueued);
        MainThreadWork.PostPicture($"texture build {width}x{height}", () =>
        {
            try { BuildTexture(pixels, width, height, hasMipmaps); }
            finally
            {
                System.Threading.Interlocked.Increment(ref _uploadsBuilt);
                tcs.TrySetResult(true);
            }
        }, pixels.LongLength);

        // QUEUED VERSUS BUILT, because the whole texture pipeline hangs off this one deferred call.
        // LoadSelf waits on that TCS before it reports the asset loaded, so if the deferred build is
        // not running, every texture in the session sits at "still loading" forever and every material
        // that wanted one binds a null - and nothing anywhere says so. Two counters make the gap
        // visible instead of leaving it to be inferred from a white model. -xlinka
        int queued = System.Threading.Volatile.Read(ref _uploadsQueued);
        if (queued <= 3 || queued % 25 == 0)
        {
            LumoraLogger.Debug($"TextureAssetHook: GPU uploads queued={queued} built={System.Threading.Volatile.Read(ref _uploadsBuilt)}");
        }
    }

    // Compression runs OFF the main thread. BPTC on a 2048-square chain is hundreds of milliseconds
    // of pure CPU; doing it inline in the deferred main-thread build would drop frames on every
    // texture load. Image is a plain CPU-side resource until it is handed to ImageTexture, so the
    // expensive part is safe to do on a worker and only the resource creation is deferred. The
    // compressed result is cached to disk keyed by the variant, so each variant pays that cost once
    // per machine and every later load is a file read. -xlinka
    public void UploadTexture(TextureUploadRequest request)
    {
        var levels = request.MipLevels;
        if (levels == null || levels.Length == 0 || request.Width <= 0 || request.Height <= 0)
        {
            // Returns WITHOUT queuing a TCS, so WaitForUploadAsync then completes instantly over a
            // hook that has no texture: the asset reports itself loaded and every material binds a
            // null albedo. Silent until now.
            LumoraLogger.Warn($"TextureAssetHook.UploadTexture: refused - levels={(levels?.Length ?? -1)} "
                            + $"size={request.Width}x{request.Height}; no GPU texture will exist.");
            return;
        }

        var tcs = NewUploadTcs();
        int serial = System.Threading.Interlocked.Increment(ref _uploadSerial);
        System.Threading.Interlocked.Increment(ref _uploadsQueued);
        var traced = asset;
        System.Threading.Tasks.Task.Run(() =>
        {
            Image image = null!;
            try
            {
                image = PrepareImage(request, traced);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"TextureAssetHook: failed to prepare {request.Width}x{request.Height} texture: {ex.Message}");
            }
            request.Prepared?.Invoke();
            Deliver(request, image, tcs, serial, traced);
        });
    }

    // The renderer's compressed copy of exactly this request, read on a worker and queued like any upload.
    public System.Threading.Tasks.Task<bool> UploadCachedAsync(TextureUploadRequest request)
    {
        string cachePath = ForceCpuEncoder ? null! : GetCachePath(request);
        if (cachePath == null)
            return System.Threading.Tasks.Task.FromResult(false);
        var traced = asset;
        return System.Threading.Tasks.Task.Run(() =>
        {
            if (!TryReadCache(request, cachePath, out var image))
                return false;
            TextureLoadTrace.Mark(traced, "cache-hit", "direct");
            var tcs = NewUploadTcs();
            int serial = System.Threading.Interlocked.Increment(ref _uploadSerial);
            System.Threading.Interlocked.Increment(ref _uploadsQueued);
            Deliver(request, image, tcs, serial, traced);
            return true;
        });
    }

    // Uploads are applied in the order they were asked for. A cold load sends a small preview and then the
    // full picture; the preview is far quicker to prepare and lands first, but an older upload that does
    // arrive late is dropped rather than put over a newer one.
    private int _uploadSerial;
    private int _appliedSerial;

    private void Deliver(TextureUploadRequest request, Image? image, System.Threading.Tasks.TaskCompletionSource<bool> tcs, int serial, object? traced)
    {
        long uploadBytes = image?.GetDataSize() ?? 0;
        Rid built = image != null && uploadBytes >= OffMainBytes ? BuildOffMain(image) : default;
        string uploadLabel = image == null ? $"texture upload {request.Width}x{request.Height}"
            : $"texture upload {image.GetWidth()}x{image.GetHeight()} {image.GetFormat()} {uploadBytes >> 10} KB{(built.IsValid ? " (built off the main thread)" : "")}";
        TextureLoadTrace.Mark(traced, "posted", uploadLabel);
        MainThreadWork.PostPicture(uploadLabel, () =>
        {
            try
            {
                if (image != null && serial < _appliedSerial)
                {
                    if (built.IsValid)
                        RenderingServer.FreeRid(built);
                    image.Dispose();
                }
                else if (image != null && built.IsValid)
                {
                    _appliedSerial = serial;
                    AdoptBuilt(built, image);
                    ReportUpload(request, image);
                    image.Dispose();
                    asset?.NotifyBackingReady();
                    TextureLoadTrace.Mark(traced, "built");
                }
                else if (image != null && TryUpdateInPlace(image))
                {
                    // Same picture shape as the one already on the GPU: its contents change under the same
                    // texture, which every material already holds, so nothing has to be told about it.
                    _appliedSerial = serial;
                    request.Report?.Invoke(MapFormat(image.GetFormat()), image.HasMipmaps() ? image.GetMipmapCount() + 1 : 1, image.GetDataSize());
                    image.Dispose();
                    TextureLoadTrace.Mark(traced, "updated");
                }
                else if (image != null)
                {
                    _appliedSerial = serial;
                    Adopt(image);
                    ReportUpload(request, image);
                    // The GPU texture has its own copy now. Waiting for the finalizer kept every
                    // decoded picture's native buffer alive until the next collection, tens of
                    // megabytes each for a 4k map.
                    image.Dispose();

                    // THE GPU TEXTURE EXISTS ONLY AS OF THIS LINE. Every material that asked for it
                    // before now was handed a null and wrote that null into its Godot material, and
                    // the load-complete notification it was waiting on had already fired, back when
                    // the DATA finished decoding. Nothing else is coming, so a prop kept a white
                    // albedo for the session while the log insisted the texture was loaded. It was.
                    // It just was not HERE yet. Tell the references now that it is. -xlinka
                    asset?.NotifyBackingReady();
                    TextureLoadTrace.Mark(traced, "built");
                }
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"TextureAssetHook: failed to build texture: {ex.Message}");
            }
            finally
            {
                int done = System.Threading.Interlocked.Increment(ref _uploadsBuilt);
                int queued = System.Threading.Volatile.Read(ref _uploadsQueued);
                if (done <= 3 || done % 25 == 0 || queued - done > 20)
                    LumoraLogger.Debug($"TextureAssetHook: GPU uploads queued={queued} built={done} valid={IsValid}");
                tcs.TrySetResult(true);
            }
        }, built.IsValid ? 0 : uploadBytes);
    }

    // A BIG PICTURE BECOMES A GPU TEXTURE OFF THE MAIN THREAD.
    //
    // Handing the renderer a picture copies it to the GPU in small tiles on the thread that asked, and a 4k map
    // with its mips (21 MB as BC3) held the frame for 226 ms as a guest's avatar arrived. The renderer makes a
    // texture on whatever thread asks for one (its own threaded loads do exactly this), so a big picture is made
    // into one here, on the worker that prepared it, and the main thread only swaps it in behind the handle the
    // materials hold, which is a move, not a copy. One at a time, so an avatar's maps never all copy at once.
    // A renderer without a rendering device makes its resources on its own thread only, and keeps the old way.
    // -xlinka
    private const long OffMainBytes = 4L << 20;
    private static readonly object _offMainTurn = new();
    private static int _offMain;

    private static void LearnOffMain()
    {
        if (System.Threading.Volatile.Read(ref _offMain) == 0)
            System.Threading.Volatile.Write(ref _offMain, RenderingServer.GetRenderingDevice() != null ? 1 : -1);
    }

    private static Rid BuildOffMain(Image image)
    {
        if (System.Threading.Volatile.Read(ref _offMain) != 1)
            return default;
        lock (_offMainTurn)
        {
            try
            {
                return RenderingServer.Texture2DCreate(image);
            }
            catch (Exception ex)
            {
                System.Threading.Volatile.Write(ref _offMain, -1);
                LumoraLogger.Warn($"TextureAssetHook: making a texture off the main thread failed ({ex.Message}); every texture is made on the main thread from now on");
                return default;
            }
        }
    }

    private void AdoptBuilt(Rid built, Image image)
    {
        if (_godotTexture == null)
        {
            using var stub = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            _godotTexture = ImageTexture.CreateFromImage(stub);
        }
        RenderingServer.TextureReplace(_godotTexture.GetRid(), built);
        _godotTexture.SetSizeOverride(new Vector2I(image.GetWidth(), image.GetHeight()));
        _godotTexture.EmitChanged();
        // The handle's own record of its format and mips is the stub's or the last picture's now, so the next
        // change goes through SetImage, never an in-place update.
        _shape = default;
    }

    // completes once the deferred BuildTexture has run (the GPU texture exists) -xlinka
    private static int _uploadsQueued;
    private static int _uploadsBuilt;

    public System.Threading.Tasks.Task WaitForUploadAsync()
    {
        var tcs = _uploadTcs;
        if (tcs != null)
            return tcs.Task;

        // No upload was ever queued, so this returns "done" over a hook that has no texture. The
        // asset then reports itself loaded, the material binds a null albedo, and the object is white
        // for the rest of the session - the exact failure the await here exists to prevent.
        if (!_warnedNoUpload)
        {
            _warnedNoUpload = true;
            LumoraLogger.Warn("TextureAssetHook.WaitForUploadAsync: nothing was ever queued for this "
                            + "texture, so the wait returns immediately over a hook with no GPU texture.");
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private bool _warnedNoUpload;

    private System.Threading.Tasks.TaskCompletionSource<bool> NewUploadTcs()
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        _uploadTcs = tcs;
        return tcs;
    }

    // OFF-THREAD IMAGE PREPARATION

    private static Image PrepareImage(TextureUploadRequest request, object? traced = null)
    {
        var levels = request.MipLevels;
        int width = request.Width;
        int height = request.Height;

        // A cached compressed blob short-circuits everything: same key means the same pixels and the
        // same compression intent, so there is nothing to recompute. A run forced onto the CPU encoder
        // neither reads nor writes the cache, so it measures the encoder and leaves no blobs behind.
        string cachePath = ForceCpuEncoder ? null! : GetCachePath(request);
        if (cachePath != null && TryReadCache(request, cachePath, out var cached))
        {
            TextureLoadTrace.Mark(traced, "cache-hit");
            return cached;
        }
        TextureLoadTrace.Mark(traced, "cache-miss", request.AllowBlockCompression ? "compress" : "plain");

        Image image = BuildSourceImage(levels, width, height, request.GenerateMipmaps,
            request.IsHdr ? Image.Format.Rgbah : Image.Format.Rgba8);

        if (request.AllowBlockCompression)
        {
            bool compressed = UsePlatformEncoder && TryCompress(image, request);
            if (!compressed && CpuEncoderFits(request))
            {
                var encoded = TryCpuEncode(image, request);
                if (encoded != null)
                {
                    image.Dispose();
                    image = encoded;
                    compressed = true;
                }
            }
            TextureLoadTrace.Mark(traced, "encoded", image.GetFormat().ToString());
            if (compressed && cachePath != null)
                TryWriteCache(cachePath, image);
        }

        return image;
    }

    // ENCODERS THIS BUILD DOES NOT HAVE, AND THE ONE THAT IS ALWAYS HERE.
    //
    // The platform's block encoders are editor-only: in an exported game every compress call answers
    // "unavailable" and leaves the picture alone, so every texture a player ever loaded went to the GPU
    // as RGBA8 with mips, 5.3 bytes a pixel. A world of three hundred textures asked for six gigabytes
    // that way. The first "unavailable" per mode is remembered, so the engine stops printing its error
    // for every texture, and BlockCompressor does BC1 (opaque colour and normal maps) or BC3 (colour with
    // alpha) on the worker instead. A headset or phone gets Etc2Compressor the same way: ETC2 RGB for opaque
    // colour and normal maps, ETC2 RGBA8 for alpha. HDR keeps its half floats and a device with neither
    // family keeps RGBA8.
    //
    // The editor build does have the platform encoders, and they froze the frame: a guest receiving one
    // avatar saw frames of 0.5 to 10.6 s while its BC7 maps went through them, against nothing over 40 ms
    // on BlockCompressor. So every build encodes on the CPU, and editor runs behave like the exported game.
    // LUMORA_TEXTURE_ENCODER=platform asks for the platform encoders again; =cpu also skips the disk
    // cache, for timing the encoder. -xlinka
    private static int _missingEncoders;
    private static int _cpuEncodeCount;
    private static long _cpuEncodeTicks;
    private static bool _cpuEncoderAnnounced;

    private static readonly bool ForceCpuEncoder =
        string.Equals(System.Environment.GetEnvironmentVariable("LUMORA_TEXTURE_ENCODER"), "cpu", StringComparison.OrdinalIgnoreCase);

    private static readonly bool UsePlatformEncoder =
        string.Equals(System.Environment.GetEnvironmentVariable("LUMORA_TEXTURE_ENCODER"), "platform", StringComparison.OrdinalIgnoreCase);

    // Godot's own mip layout for a block format, asked of the platform once per size: offsets of each
    // level, then the total.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, bool, Image.Format), long[]> _blockLayouts = new();

    private static bool CpuBcFamily => Device.S3tc && !Device.PreferMobileFamily;

    private static bool CpuEncoderFits(TextureUploadRequest request)
        => !request.IsHdr && (CpuBcFamily || Device.Etc2);

    private static Image? TryCpuEncode(Image image, TextureUploadRequest request)
    {
        try
        {
            int width = image.GetWidth();
            int height = image.GetHeight();
            if (width < 4 || height < 4)
                return null;
            if (image.IsCompressed())
                return null;
            if (image.GetFormat() != Image.Format.Rgba8)
                image.Convert(Image.Format.Rgba8);

            // Block formats want the top level a whole number of blocks; the platform's own encoder
            // stretches to the next multiple of four the same way.
            bool mipmaps = image.HasMipmaps();
            if ((width & 3) != 0 || (height & 3) != 0)
            {
                width = (width + 3) & ~3;
                height = (height + 3) & ~3;
                image.Resize(width, height, Image.Interpolation.Bilinear);
                if (mipmaps && !image.HasMipmaps())
                    image.GenerateMipmaps();
            }

            bool etc = !CpuBcFamily;
            bool alpha = request.HasAlpha && !request.IsNormalMap;
            var format = alpha ? BlockCompressor.Format.Bc3 : BlockCompressor.Format.Bc1;
            var etcFormat = alpha ? Etc2Compressor.Format.Etc2Rgba : Etc2Compressor.Format.Etc2Rgb;
            var target = etc
                ? alpha ? Image.Format.Etc2Rgba8 : Image.Format.Etc2Rgb8
                : alpha ? Image.Format.Dxt5 : Image.Format.Dxt1;
            var layout = BlockLayout(width, height, mipmaps, target, alpha ? 16 : 8);
            if (layout == null)
                return null;
            int levels = layout.Length - 1;
            int sourceLevels = mipmaps ? image.GetMipmapCount() + 1 : 1;
            if (levels > sourceLevels)
                return null;

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            byte[] source = image.GetData();
            var destination = new byte[layout[levels]];
            for (int level = 0; level < levels; level++)
            {
                int levelWidth = System.Math.Max(1, width >> level);
                int levelHeight = System.Math.Max(1, height >> level);
                int sourceOffset = level == 0 ? 0 : (int)image.GetMipmapOffset(level);
                if (etc)
                    Etc2Compressor.EncodeLevel(source, sourceOffset, levelWidth, levelHeight, etcFormat, destination, (int)layout[level]);
                else
                    BlockCompressor.EncodeLevel(source, sourceOffset, levelWidth, levelHeight, format,
                        perceptual: !request.IsNormalMap, destination, (int)layout[level]);
            }

            var encoded = Image.CreateFromData(width, height, mipmaps, target, destination);
            if (encoded == null || encoded.IsEmpty() || encoded.GetFormat() != target)
            {
                encoded?.Dispose();
                return null;
            }

            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            int count = System.Threading.Interlocked.Increment(ref _cpuEncodeCount);
            long ticks = System.Threading.Interlocked.Add(ref _cpuEncodeTicks, elapsed);
            if (!_cpuEncoderAnnounced)
            {
                _cpuEncoderAnnounced = true;
                LumoraLogger.Log($"TextureAssetHook: {(UsePlatformEncoder ? "no platform block encoder in this build" : "block encoder")}{(ForceCpuEncoder ? " (forced, no disk cache)" : string.Empty)}; "
                               + (etc ? "compressing on the CPU, ETC2 RGB for opaque colour and normal maps, ETC2 RGBA8 for alpha"
                                      : "compressing on the CPU, BC1 for opaque colour and normal maps, BC3 for alpha"));
            }
            if (count <= 3 || count % 25 == 0)
            {
                LumoraLogger.Debug($"TextureAssetHook: CPU block encode #{count} {width}x{height} {target} in "
                                 + $"{elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms "
                                 + $"(all so far {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms)");
            }
            return encoded;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"TextureAssetHook: CPU block encode failed, keeping {image.GetFormat()}: {ex.Message}");
            return null;
        }
    }

    private static long[]? BlockLayout(int width, int height, bool mipmaps, Image.Format target, int blockBytes)
    {
        var key = (width, height, mipmaps, target);
        if (_blockLayouts.TryGetValue(key, out var known))
            return known;

        using var probe = Image.CreateEmpty(width, height, mipmaps, target);
        if (probe == null || probe.IsEmpty() || probe.GetFormat() != target)
            return null;
        int levels = mipmaps ? probe.GetMipmapCount() + 1 : 1;
        var layout = new long[levels + 1];
        for (int level = 1; level < levels; level++)
            layout[level] = probe.GetMipmapOffset(level);
        layout[levels] = probe.GetDataSize();
        for (int level = 0; level < levels; level++)
        {
            long expected = (long)BlockCompressor.BlocksAcross(System.Math.Max(1, width >> level))
                * BlockCompressor.BlocksAcross(System.Math.Max(1, height >> level)) * blockBytes;
            if (layout[level + 1] - layout[level] != expected)
                return null;
        }

        _blockLayouts[key] = layout;
        return layout;
    }

    private static Image BuildSourceImage(byte[][] levels, int width, int height, bool generateMipmaps, Image.Format format)
    {
        // A supplied chain is only usable as-is when it runs all the way to 1x1, which is the layout
        // the renderer expects behind a single buffer. A partial chain is not an error, it just means
        // we hand over the base level and let the renderer fill the rest in.
        int fullLevels = FullMipCount(width, height);
        if (levels.Length > 1 && levels.Length == fullLevels)
        {
            long total = 0;
            foreach (var level in levels)
                total += level?.LongLength ?? 0;

            var packed = new byte[total];
            int offset = 0;
            foreach (var level in levels)
            {
                if (level == null)
                    continue;
                Buffer.BlockCopy(level, 0, packed, offset, level.Length);
                offset += level.Length;
            }
            return Image.CreateFromData(width, height, true, format, packed);
        }

        var image = Image.CreateFromData(width, height, false, format, levels[0]);
        if (generateMipmaps || levels.Length > 1)
            image.GenerateMipmaps();
        return image;
    }

    // WHAT THIS DEVICE CAN SAMPLE.
    //
    // Asked once, on the main thread, before any texture is built. RenderingServer.HasOsFeature is
    // the renderer's own answer for "s3tc", "bptc", "etc2" and "astc": on the RenderingDevice
    // backends it is TextureIsFormatSupportedForUsage against each family's representative block
    // format, on the GL backend it is the extension list. Hardcoding by OS name got this wrong in
    // both directions: an ARM laptop or a software renderer has no BC family, and a desktop-class
    // Vulkan driver often has ETC2 as well.
    //
    // The query says nothing about whether the ENCODER for a format is compiled into this binary,
    // so the compress call still checks its own result and steps down the plan. The query decides
    // what is worth trying; compress-and-see covers what this build can produce. Both are honest
    // about different things. -xlinka
    public sealed class DeviceFormats
    {
        public bool S3tc;
        public bool Bptc;
        public bool Etc2;
        public bool Astc;
        public bool PreferMobileFamily;
        public bool Queried;

        public override string ToString()
        {
            var parts = new System.Collections.Generic.List<string>(4);
            if (S3tc) parts.Add("s3tc");
            if (Bptc) parts.Add("bptc");
            if (Etc2) parts.Add("etc2");
            if (Astc) parts.Add("astc");
            return parts.Count == 0 ? "none" : string.Join("-", parts);
        }
    }

    private static readonly object _deviceLock = new();
    private static DeviceFormats _device = new();

    public static DeviceFormats Device
    {
        get
        {
            var device = _device;
            if (device.Queried)
                return device;
            lock (_deviceLock)
            {
                if (!_device.Queried)
                    QueryDeviceFormats();
                return _device;
            }
        }
    }

    public static DeviceFormats QueryDeviceFormats()
    {
        var device = new DeviceFormats { Queried = true };
        try
        {
            device.S3tc = RenderingServer.HasOsFeature("s3tc");
            device.Bptc = RenderingServer.HasOsFeature("bptc");
            device.Etc2 = RenderingServer.HasOsFeature("etc2");
            device.Astc = RenderingServer.HasOsFeature("astc");
        }
        catch (Exception ex)
        {
            // No renderer to ask (headless, or a test without a rendering server). Every flag
            // stays false, which means every texture stays uncompressed, which is the only answer
            // that cannot be wrong.
            LumoraLogger.Warn($"TextureAssetHook: could not query texture format support ({ex.Message}); uploading uncompressed");
        }
        device.PreferMobileFamily = OS.GetName() is "Android" or "iOS";
        lock (_deviceLock)
            _device = device;
        return device;
    }

    // Part of the GPU cache key. A blob baked in one format must never be handed to a device
    // expecting another, so the tag names the format families this device offers plus a policy
    // number: bump the number whenever the choice below changes for the same inputs, or a blob
    // baked under the old policy is handed back under the new one. -xlinka
    private const int FormatPolicyVersion = 3;

    public static string CompressionTag() => $"p{FormatPolicyVersion}-{Device}";

    // One thing to try, in the order the plan lists them.
    private readonly record struct CompressAttempt(Image.CompressMode Mode, Image.UsedChannels Channels);

    // THE FORMAT DECISION, from five inputs: device, HDR, alpha, normal map, and colour profile
    // (which only picks the compress-source hint here, since the runtime encoders do not branch on
    // it).
    //
    // Desktop: BC1 for opaque colour (4 bpp), BC7 where alpha or a normal map needs the extra
    // channel (8 bpp, the same bytes as BC3 and BC3nm at strictly higher quality), BC6H for HDR.
    // Normal maps keep three channels rather than taking Godot's RG-only route because our material
    // shaders sample normal.rgb; an RG block would hand them a zero blue.
    //
    // Mobile: ETC2 RGB8 for opaque colour (4 bpp, the floor both GLES3 and Vulkan guarantee), ASTC
    // 4x4 where alpha or a normal map needs it (8 bpp, the same bytes as ETC2 RGBA8 at better
    // quality) with ETC2 RGBA8 behind it, and HDR stays uncompressed half float. ASTC 8x8 is
    // deliberately not used: it is 2 bpp and below the ETC2 floor in quality, and Godot exposes no
    // block size between the two.
    //
    // HDR never enters an LDR block format. If BC6H is not on offer the texture stays RGBAH and
    // pays full price rather than lose its range. -xlinka
    private static System.Collections.Generic.List<CompressAttempt> PlanCompression(TextureUploadRequest request, DeviceFormats device)
    {
        var plan = new System.Collections.Generic.List<CompressAttempt>(3);
        bool needsFourth = request.HasAlpha || request.IsNormalMap;
        var channels = request.HasAlpha && !request.IsNormalMap ? Image.UsedChannels.Rgba : Image.UsedChannels.Rgb;

        if (request.IsHdr)
        {
            if (device.Bptc)
                plan.Add(new CompressAttempt(Image.CompressMode.Bptc, Image.UsedChannels.Rgb));
            return plan;
        }

        void DesktopFamily()
        {
            if (needsFourth)
            {
                if (device.Bptc) plan.Add(new CompressAttempt(Image.CompressMode.Bptc, channels));
                if (device.S3tc) plan.Add(new CompressAttempt(Image.CompressMode.S3Tc, channels));
            }
            else
            {
                if (device.S3tc) plan.Add(new CompressAttempt(Image.CompressMode.S3Tc, channels));
                if (device.Bptc) plan.Add(new CompressAttempt(Image.CompressMode.Bptc, channels));
            }
        }

        void MobileFamily()
        {
            if (needsFourth)
            {
                if (device.Astc) plan.Add(new CompressAttempt(Image.CompressMode.Astc, channels));
                if (device.Etc2) plan.Add(new CompressAttempt(Image.CompressMode.Etc2, channels));
            }
            else
            {
                if (device.Etc2) plan.Add(new CompressAttempt(Image.CompressMode.Etc2, channels));
                if (device.Astc) plan.Add(new CompressAttempt(Image.CompressMode.Astc, channels));
            }
        }

        if (device.PreferMobileFamily)
        {
            MobileFamily();
            DesktopFamily();
        }
        else
        {
            DesktopFamily();
            MobileFamily();
        }
        return plan;
    }

    // Block-compress in place. Returns true only when the format actually changed, so a device that
    // cannot compress silently keeps the uncompressed image instead of failing the load.
    private static bool TryCompress(Image image, TextureUploadRequest request)
    {
        // Below one full block there is nothing to compress and Godot rejects it outright.
        if (image.GetWidth() < 4 || image.GetHeight() < 4)
            return false;

        var plan = PlanCompression(request, Device);
        if (plan.Count == 0)
            return false;

        try
        {
            var before = image.GetFormat();
            bool tried = false;
            foreach (var attempt in plan)
            {
                int bit = 1 << (int)attempt.Mode;
                if ((System.Threading.Volatile.Read(ref _missingEncoders) & bit) != 0)
                    continue;
                tried = true;
                // The channel set comes from the sidecar's pixel scan rather than Godot's own
                // DetectUsedChannels, which would walk every texel of the full-resolution image
                // again to learn a fact the engine already measured once. -xlinka
                // The format check is the real verdict: an encoder that is not in this build returns
                // an error and leaves the image alone, and either sign on its own is enough to move
                // to the next attempt.
                var error = image.CompressFromChannels(attempt.Mode, attempt.Channels, Image.AstcFormat.Format4X4);
                if (image.GetFormat() != before)
                    return true;
                if (error == Error.Unavailable
                    && (System.Threading.Interlocked.Or(ref _missingEncoders, bit) & bit) == 0)
                    LumoraLogger.Log($"TextureAssetHook: this build has no {attempt.Mode} encoder; not asking again");
            }

            if (tried)
                LumoraLogger.Debug($"TextureAssetHook: no encoder in this build for {plan[0].Mode} on {image.GetWidth()}x{image.GetHeight()}; keeping {before}");
            return false;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"TextureAssetHook: compression failed, keeping {image.GetFormat()}: {ex.Message}");
            return false;
        }
    }

    private static int FullMipCount(int width, int height)
    {
        int levels = 1;
        int w = System.Math.Max(1, width), h = System.Math.Max(1, height);
        while (w > 1 || h > 1)
        {
            w = System.Math.Max(1, w >> 1);
            h = System.Math.Max(1, h >> 1);
            levels++;
        }
        return levels;
    }

    // MACHINE-LOCAL COMPRESSED CACHE

    private static string GetCachePath(TextureUploadRequest request)
    {
        if (!request.AllowBlockCompression
            || string.IsNullOrEmpty(request.CacheKey)
            || string.IsNullOrEmpty(request.CacheDirectory))
            return null!;
        // Name shared with the engine side: it probes this same path to find out whether a rung is already
        // compressed, and a private copy of the naming here would silently make every probe miss. -xlinka
        return TextureGpuCache.FileFor(request.CacheDirectory!, request.CacheKey!);
    }

    // The sealed blob, or one from before sealing under its plain name (the background pass seals those).
    private static bool TryReadCache(TextureUploadRequest request, string path, out Image image)
    {
        image = null!;
        try
        {
            if (!File.Exists(path))
            {
                var legacy = TextureGpuCache.LegacyFileFor(request.CacheDirectory!, request.CacheKey!);
                if (!File.Exists(legacy))
                    return false;
                path = legacy;
            }

            using var stream = Lumora.Nexus.Storage.SealedFile.OpenAny(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != GpuCacheVersion)
                return false;

            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            var format = (Image.Format)reader.ReadInt32();
            bool mipmaps = reader.ReadBoolean();
            int length = reader.ReadInt32();
            if (width <= 0 || height <= 0 || length <= 0 || length > stream.Length - stream.Position)
                return false;

            var data = reader.ReadBytes(length);
            image = Image.CreateFromData(width, height, mipmaps, format, data);
            return image != null;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"TextureAssetHook: unreadable compressed cache '{path}': {ex.Message}");
            return false;
        }
    }

    private static void TryWriteCache(string path, Image image)
    {
        var data = image.GetData();
        if (data == null || data.Length == 0)
            return;
        TryWriteCache(path, image.GetWidth(), image.GetHeight(), image.GetFormat(), image.HasMipmaps(), data);
    }

    // A block chain that arrived already compressed, filed exactly where an upload of the same request would
    // file its own, so the load that asks for it takes it straight from the cache. Refused unless this device
    // takes the format and the chain is the layout the platform holds behind one buffer. -xlinka
    public static bool WriteBlockCache(string path, int width, int height, TextureFormatKind format, bool mipmaps, byte[] blocks)
    {
        if (ForceCpuEncoder || !CpuBcFamily || blocks == null || width < 4 || height < 4 || (width & 3) != 0 || (height & 3) != 0)
            return false;
        Image.Format target;
        int blockBytes;
        switch (format)
        {
            case TextureFormatKind.BC1_RGB:
                target = Image.Format.Dxt1;
                blockBytes = 8;
                break;
            case TextureFormatKind.BC3_RGBA:
                target = Image.Format.Dxt5;
                blockBytes = 16;
                break;
            default:
                return false;
        }
        var layout = BlockLayout(width, height, mipmaps, target, blockBytes);
        if (layout == null || layout[layout.Length - 1] != blocks.LongLength)
            return false;
        return TryWriteCache(path, width, height, target, mipmaps, blocks);
    }

    private static bool TryWriteCache(string path, int width, int height, Image.Format format, bool mipmaps, byte[] data)
    {
        string? temp = null;
        try
        {
            // Write beside the target then move into place, so a crash mid-write can never leave a
            // truncated blob that a later run would happily load as a texture. The beside file is this
            // write's own: two loads of one texture (a picture and its spawned copy, one map under two
            // descriptors) finish their encodes together, and one shared name made the second fail with
            // the file in use. Same key, same bytes, so whichever lands last is as good. -xlinka
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = Lumora.Nexus.Storage.SealedFile.Create(temp))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(GpuCacheVersion);
                writer.Write(width);
                writer.Write(height);
                writer.Write((int)format);
                writer.Write(mipmaps);
                writer.Write(data.Length);
                writer.Write(data);
            }
            File.Move(temp, path, true);
            temp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
        {
            // The other write of this key landed first and a load has it open: it is these same bytes.
            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"TextureAssetHook: could not cache compressed texture '{path}': {ex.Message}");
            return false;
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    // MAIN-THREAD RESOURCE CREATION

    // Keep the SAME texture resource for the life of the asset and push new contents through it.
    //
    // This is what makes a progressive load look like a texture getting sharper instead of a texture
    // disappearing and coming back. Every material that bound this texture holds a reference to the
    // resource object, and swapping the object out from under them leaves each one pointing at a
    // disposed handle until something re-binds it. SetImage does not have that problem: internally it
    // builds the new texture and swaps it in behind the same handle, so a size change (128 -> 2048)
    // and a format change (RGBA8 -> BPTC) both land without anyone downstream noticing. Do not
    // "optimize" this back into dispose-and-recreate. -xlinka
    private void Adopt(Image image)
    {
        LearnOffMain();
        if (_godotTexture == null)
            _godotTexture = ImageTexture.CreateFromImage(image);
        else
            _godotTexture.SetImage(image);
        _shape = (image.GetWidth(), image.GetHeight(), image.GetFormat(), image.HasMipmaps());
    }

    // What the texture on the GPU was last built from; the platform texture does not say whether it has mips.
    private (int Width, int Height, Image.Format Format, bool Mips) _shape;

    // A PROCEDURAL PICTURE THAT CHANGES IS NOT A NEW TEXTURE.
    //
    // A generated texture with something driving it (a noise whose offset scrolls, a gradient that fades) is
    // made again on every change, and each one went up as a whole new texture swapped in behind the handle,
    // then announced to every material as if it had just arrived: a world's scrolling noise card did that 1.6
    // times a second for a whole evening, eighteen thousand textures. When the new picture has the shape of
    // the one already there (size, format, mip chain, uncompressed), its pixels are written into the existing
    // texture instead. -xlinka
    private static int _updatedInPlace;

    private bool TryUpdateInPlace(Image image)
    {
        var texture = _godotTexture;
        if (texture == null || image.IsCompressed() || _shape != (image.GetWidth(), image.GetHeight(), image.GetFormat(), image.HasMipmaps()))
            return false;
        texture.Update(image);
        System.Threading.Interlocked.Increment(ref _updatedInPlace);
        return true;
    }

    // What has gone to the GPU, by format, since start. A picture of a megabyte or more that went up
    // uncompressed says why, so a VRAM bill can be read off the log rather than guessed. -xlinka
    private static readonly System.Collections.Generic.Dictionary<Image.Format, (int Count, long Bytes)> _uploadedByFormat = new();
    private static int _uploadsTallied;

    private static void Tally(TextureUploadRequest request, Image image, long bytes)
    {
        var format = image.GetFormat();
        int tallied;
        string totals = string.Empty;
        lock (_uploadedByFormat)
        {
            _uploadedByFormat.TryGetValue(format, out var had);
            _uploadedByFormat[format] = (had.Count + 1, had.Bytes + bytes);
            tallied = ++_uploadsTallied;
            if (tallied % 25 == 0)
                totals = string.Join(", ", System.Linq.Enumerable.Select(_uploadedByFormat, kv => $"{kv.Key} {kv.Value.Count} = {kv.Value.Bytes >> 20} MB"));
        }
        if (!image.IsCompressed() && bytes >= 1L << 20)
            LumoraLogger.Debug($"TextureAssetHook: {image.GetWidth()}x{image.GetHeight()} went to the GPU as {format}, {bytes >> 20} MB: "
                            + $"compression allowed {request.AllowBlockCompression}, hdr {request.IsHdr}, alpha {request.HasAlpha}, "
                            + $"normal map {request.IsNormalMap}, CPU encoder fits {CpuEncoderFits(request)}, forced CPU {ForceCpuEncoder}");
        if (totals.Length > 0)
            LumoraLogger.Log($"TextureAssetHook: {tallied} uploads so far: {totals}; {System.Threading.Volatile.Read(ref _updatedInPlace)} generated pictures refreshed in place");
    }

    private void ReportUpload(TextureUploadRequest request, Image image)
    {
        // The size, not a copy of the data to measure: GetData marshalled the whole chain into a managed
        // array on the main thread just to read its length.
        long bytes = image.GetDataSize();
        Tally(request, image, bytes);
        if (request.Report == null)
            return;
        int mipCount = image.HasMipmaps() ? image.GetMipmapCount() + 1 : 1;
        request.Report(MapFormat(image.GetFormat()), mipCount, bytes);
    }

    private static TextureFormatKind MapFormat(Image.Format format) => format switch
    {
        Image.Format.Rgba8 => TextureFormatKind.RGBA8,
        Image.Format.Rgb8 => TextureFormatKind.RGB8,
        Image.Format.Rg8 => TextureFormatKind.RG8,
        Image.Format.R8 => TextureFormatKind.R8,
        Image.Format.Dxt1 => TextureFormatKind.BC1_RGB,
        Image.Format.Dxt5 => TextureFormatKind.BC3_RGBA,
        Image.Format.RgtcR => TextureFormatKind.BC4_R,
        Image.Format.RgtcRg => TextureFormatKind.BC5_RG,
        Image.Format.BptcRgba => TextureFormatKind.BC7_RGBA,
        Image.Format.BptcRgbf or Image.Format.BptcRgbfu => TextureFormatKind.BC6H_RGB,
        Image.Format.Etc2Rgb8 => TextureFormatKind.ETC2_RGB8,
        Image.Format.Etc2Rgba8 => TextureFormatKind.ETC2_RGBA8,
        Image.Format.Astc4X4 => TextureFormatKind.ASTC_4x4,
        Image.Format.Rgbah => TextureFormatKind.RGBA16F,
        _ => TextureFormatKind.Unknown,
    };

    private void BuildTexture(byte[] pixels, int width, int height, bool hasMipmaps)
    {
        // Check if this is raw RGBA data or encoded image data
        int expectedRgbaSize = width * height * 4;

        Image image;

        if (width > 0 && height > 0 && pixels.Length == expectedRgbaSize)
        {
            // Raw RGBA8 pixel data - create without mipmaps first (we only have base level data)
            image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);

            // KTX and DDS arrive already block-compressed, and the renderer refuses to generate mipmaps
            // on a compressed format - it prints its own error and carries on, so this is pure noise in
            // the log rather than a failure. Skip it for those, they ship their own chain.
            if (hasMipmaps && !image.IsCompressed())
            {
                image.GenerateMipmaps();
            }
        }
        else
        {
            // Encoded image data (PNG, JPEG, etc.) - let Godot decode it
            image = new Image();
            var error = Lumora.Godot.Helpers.GodotImageLoader.Load(image, pixels);

            if (error != Error.Ok)
            {
                image.Dispose();
                GD.PrintErr($"TextureAssetHook: Failed to decode image data ({error})");
                return;
            }

            if (hasMipmaps && !image.IsCompressed())
            {
                image.GenerateMipmaps();
            }
        }

        Adopt(image);
        image.Dispose();
    }

    // In Godot 4, wrap mode is set on the material/sampler, not the texture itself; we just store
    // the values for reference.
    public void SetWrapMode(TextureWrapMode wrapU, TextureWrapMode wrapV)
    {
        _wrapU = wrapU;
        _wrapV = wrapV;
    }

    public TextureWrapMode WrapModeU => _wrapU;

    public TextureWrapMode WrapModeV => _wrapV;

    public override void Unload()
    {
        if (_godotTexture != null)
        {
            _godotTexture.Dispose();
            _godotTexture = null!;
        }
    }
}

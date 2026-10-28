// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Assets;

public interface IAssetHook
{
    void Initialize(IAsset asset);

    void Unload();
}

public interface ITextureAssetHook : IAssetHook
{
    void UploadData(byte[] pixels, int width, int height, bool hasMipmaps);

    void SetWrapMode(TextureWrapMode wrapU, TextureWrapMode wrapV);

    bool IsValid { get; }

    // Completes once the most recent UploadData has actually built the GPU texture - UploadData
    // itself only QUEUES a deferred (main-thread) build. The asset awaits this before reporting FullyLoaded so a
    // consuming material never binds the texture while IsValid is still false (the white-body race).
    System.Threading.Tasks.Task WaitForUploadAsync();

    // Upload a texture the renderer is allowed to make decisions about: a pre-built mip chain
    // instead of a single level, and permission to block-compress if the device supports it.
    //
    // The split between this and UploadData is deliberate. The engine cannot know
    // which compressed formats the running GPU accepts, so it never picks one; it states intent
    // (compressible or not, normal map or not, sRGB or not) and the renderer decides. Whatever the
    // renderer ends up building it reports back through Report,
    // so the format and VRAM figures the inspector shows are observed rather than assumed.
    //
    // Default implementation uploads the base level the old way, which keeps every renderer that
    // has not implemented this compiling and correct, just without compression. -xlinka
    void UploadTexture(TextureUploadRequest request)
    {
        var levels = request.MipLevels;
        if (levels == null || levels.Length == 0)
            return;
        // UploadData is an RGBA8 contract. Feeding it half floats would put a picture of noise on
        // screen, which is worse than no picture.
        if (request.IsHdr)
            return;
        UploadData(levels[0], request.Width, request.Height, request.GenerateMipmaps || levels.Length > 1);
    }

    // The copy the renderer compressed and kept under the request's CacheKey, put on the GPU with no pixels at
    // all. True once it is queued, after which WaitForUploadAsync answers for it. False, with nothing queued,
    // when there is no such copy or this renderer keeps none; the caller then hands over pixels as usual.
    System.Threading.Tasks.Task<bool> UploadCachedAsync(TextureUploadRequest request) => System.Threading.Tasks.Task.FromResult(false);
}

public sealed class TextureUploadRequest
{
    public byte[][] MipLevels { get; init; } = System.Array.Empty<byte[]>();

    public int Width { get; init; }
    public int Height { get; init; }

    public bool GenerateMipmaps { get; init; }

    public bool AllowBlockCompression { get; init; }

    // Measured from the pixel scan, so the renderer can pick a format without alpha.
    public bool HasAlpha { get; init; }

    // Set only when the importer or user marked it; drives normal-map-aware compression.
    public bool IsNormalMap { get; init; }

    public bool? SRgb { get; init; }

    // True when MipLevels hold RGBA half floats (8 bytes per texel) instead of RGBA8. Range above
    // 1.0 is the whole point of such a texture, so the renderer may only pick a format that keeps
    // it (BC6H) or none at all. -xlinka
    public bool IsHdr { get; init; }

    // Same key means same pixels and same compression intent. Null skips the cache.
    public string? CacheKey { get; init; }

    // Null disables the cache.
    public string? CacheDirectory { get; init; }

    // The renderer reports the format it actually built, the mip count it holds and the resident
    // bytes. Never a guess.
    public System.Action<TextureFormatKind, int, long>? Report { get; init; }

    // Called once the renderer holds its own copy and the CPU side is done (compression included), before
    // the upload waits for the main thread. The caller's pixels are free from then on.
    public System.Action? Prepared { get; init; }
}

public interface IRenderTextureAssetHook : ITextureAssetHook
{
    void Configure(
        int width,
        int height,
        int cullMask,
        Math.color clearColor,
        Math.float3 cameraPosition,
        Math.floatQ cameraRotation,
        float orthographicSize);

    void SetRenderEnabled(bool enabled);

    // Render exactly one frame now, then go idle again (keeping the last frame). Used for render-on-change:
    // the UI viewport only re-renders when its captured content actually changed, instead of every frame.
    void RequestRender();

    // Hand the viewport over to a Camera component, or null to give it back to the provider's own fields.
    //
    // A provider-driven render texture is a fixed orthographic capture that only redraws when something
    // it captured changed - that is what the UI canvases use. A camera-driven one is filming a live
    // scene from a slot that can move, so it redraws continuously. The two cannot share one set of
    // fields, so the camera's parameters ride separately and take priority while they are set. -xlinka
    void SetCameraOverride(RenderCameraParameters? parameters);

    // The next frame it films, read back and written as an image: a photo. Null when there was nothing to read.
    System.Threading.Tasks.Task<byte[]?> CaptureAsync(ImageEncoding encoding, float quality);

    // The same frame as raw pixels, four bytes each, row 0 at the top.
    System.Threading.Tasks.Task<PixelFrame?> CaptureRawAsync();
}

public sealed record PixelFrame(int Width, int Height, byte[] Rgba);

// Everything a Camera needs the renderer to know, in engine terms. Position and rotation are GLOBAL:
// the viewport that renders this does not sit under the camera's slot, so there is no parent transform
// on the other side to compose with. -xlinka
public readonly struct RenderCameraParameters
{
    public bool Perspective { get; init; }
    public float FieldOfView { get; init; }
    public float OrthographicSize { get; init; }
    public float NearClip { get; init; }
    public float FarClip { get; init; }

    public Math.float3 Position { get; init; }
    public Math.floatQ Rotation { get; init; }

    public int CullMask { get; init; }

    public Components.ClearMode Clear { get; init; }
    public Math.color BackgroundColor { get; init; }

    public bool RenderShadows { get; init; }
    public bool OcclusionCulling { get; init; }
    public bool Msaa { get; init; }
    public bool PostProcessing { get; init; }

    public bool WearerView { get; init; }
    public bool StudioLight { get; init; }
}

public interface IMeshAssetHook : IAssetHook
{
    void UploadMesh(Phos.PhosMesh mesh);

    bool IsValid { get; }
}

public interface IMaterialAssetHook : IAssetHook
{
    // True while a texture that IS assigned has not reached the GPU yet, so the material is currently
    // showing a null in its place and needs to be re-applied once it has.
    bool HasTexturesStillArriving { get; }

    // The property names behind that bool, comma separated, for the warning when the wait runs out.
    // Knowing a material gave up is half an answer; knowing it gave up on AlbedoTexture is the whole one.
    string TexturesStillArriving { get; }

    void SetMaterialType(MaterialType type);

    void SetBlendMode(BlendMode mode);

    void SetCulling(Culling culling);

    void SetFloat(string property, float value);

    void SetInt(string property, int value);

    void SetBool(string property, bool value);

    void SetColor(string property, Math.colorHDR value);

    void SetFloat2(string property, Math.float2 value);

    // Set a float2 property AND flush just that one to the live material immediately (no full ApplyChanges,
    // which re-pushes every property). For per-frame hot paths like scroll clip_offset. -xlinka
    void ApplyFloat2Now(string property, Math.float2 value);

    // Push one property set since the last ApplyChanges to the live material now, leaving everything else as it is.
    void ApplyStagedNow(string property);

    void SetFloat3(string property, Math.float3 value);

    void SetFloat4(string property, Math.float4 value);

    void SetTexture(string property, TextureAsset texture);

    void SetCustomShader(string shaderPath);

    void SetCustomShaderSource(string shaderSource);

    // The chained pass's own source (an outline hull); empty drops the pass.
    void SetNextPassShaderSource(string shaderSource);

    void SetCubemap(string property, CubemapAsset cubemap);

    // Called before UpdateMaterial.
    void Clear();

    void ApplyChanges(Action callback);

    object GodotMaterial { get; }

    // The copy identical materials share, for renderers that only draw with it.
    object SharedGodotMaterial { get; }

    // -1 = default queue.
    int RenderQueue { get; }

    bool IsValid { get; }
}

public interface IFontAssetHook : IAssetHook
{
    bool IsValid { get; }

    // engine-side texture asset wrapping the font's glyph atlas - xlinka
    TextureAsset? AtlasTexture { get; }

    // load a font from a file path. format detected from extension (.ttf/.otf/.woff). - xlinka
    void LoadFromFile(string path);

    // load a font from its bytes, for one the asset database keeps sealed. name keys its glyph cache. - xlinka
    void LoadFromData(byte[] data, string name);

    // return false if the codepoint isn't rasterized at this size (caller may request and retry) - xlinka
    bool TryGetGlyph(int codepoint, float size, out GlyphMetrics metrics, out Math.Rect uvRect);

    // ensure the given codepoint is in the atlas at the given size. async-friendly. - xlinka
    void RequestGlyph(int codepoint, float size);

    float GetLineHeight(float size);
    float GetAscent(float size);
    float GetDescent(float size);
    float GetKerning(int leftCodepoint, int rightCodepoint, float size);

    // MSDF reconstruction parameter - pixels of distance encoded around each glyph edge.
    // The shader uses this to scale signed distance into screen-space pixel units. - xlinka
    int PixelRange { get; }

    // Bumped whenever a glyph entry is added to the atlas. The atlas never repacks, so a
    // shaped run stays valid until this changes - text shaping caches against it. - xlinka
    int CacheGeneration { get; }
}

public interface IMaterialPropertyBlockAssetHook : IAssetHook
{
    void SetFloat(string property, float value);
    void SetInt(string property, int value);
    void SetBool(string property, bool value);
    void SetColor(string property, Math.colorHDR value);
    void SetFloat2(string property, Math.float2 value);
    void SetFloat3(string property, Math.float3 value);
    void SetFloat4(string property, Math.float4 value);
    void SetTexture(string property, TextureAsset texture);
    void Clear();
    void ApplyChanges(Action callback);
    object ApplyToMaterial(object baseMaterial, MaterialType materialType);
    bool IsValid { get; }
}

public enum TextureWrapMode
{
    Repeat,
    Clamp,
    Mirror,
    ClampToBorder,
    // Mirrored once about each edge, clamped past that.
    MirrorOnce
}

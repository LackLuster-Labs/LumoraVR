// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// This is the order the renderer expects its six layers in and the order every producer writes, so it is
// the one place the convention is stated.
public enum CubemapFace
{
    PositiveX = 0,
    NegativeX = 1,
    PositiveY = 2,
    NegativeY = 3,
    PositiveZ = 4,
    NegativeZ = 5,
}

// A face size of 0 means "pick from the source", which every auto request shares.
public sealed record CubemapVariantDescriptor(
    int FaceSize,
    bool GenerateMipmaps) : IAssetVariantDescriptor
{
    public static readonly CubemapVariantDescriptor Default = new(FaceSize: 0, GenerateMipmaps: true);
}

public sealed class CubemapUploadRequest
{
    // Six faces in CubemapFace order, FaceSize squared texels each: RGBA8, or half-float RGBA (eight
    // bytes a texel, linear light) when Hdr is set.
    public byte[][] Faces { get; init; } = Array.Empty<byte[]>();

    public bool Hdr { get; init; }

    // Faces are always square and always equal.
    public int FaceSize { get; init; }

    public bool GenerateMipmaps { get; init; }

    public Action<long>? Report { get; init; }
}

public interface ICubemapAssetHook : IAssetHook
{
    bool IsValid { get; }

    void UploadCubemap(CubemapUploadRequest request);

    // Completes once the most recent UploadCubemap has actually built the GPU resource.
    // Uploads only queue a deferred main-thread build, so a URL instance awaits this before reporting
    // itself loaded - otherwise a sky or probe binds a null cubemap in the gap and nothing rebinds it.
    Task WaitForUploadAsync();
}

// Six cube faces, RGBA8, or half-float RGBA from an HDR panorama. URL instances decode an
// equirectangular panorama in LoadSelf and project it onto the faces; procedural instances (gradient,
// checker, noise) and the six-image assembler push their faces straight in through SetFaces.
//
// Faces are the only form kept. A panorama is an import format, not a runtime one: keeping both
// would double the resident bytes of every skybox, and the cube is what a sky shader, a probe
// fallback and a material all want to sample anyway. -xlinka
public class CubemapAsset : ImplementableAsset<ICubemapAssetHook>
{
    public const int MinFaceSize = 8;

    // Cap on a single face. Six faces at this size is 384 MB of RGBA8 before mips.
    public const int MaxFaceSize = 4096;

    private byte[][] _faces = Array.Empty<byte[]>();
    private int _faceSize;
    private bool _hasMipmaps;
    private bool _hdr;

    // 0 when nothing has been uploaded.
    public int FaceSize => _faceSize;

    // CubemapFace order. Empty until data is set.
    public byte[][] Faces => _faces;

    public bool HasMipmaps => _hasMipmaps;

    // Faces are half-float RGBA in linear light, values above 1 kept.
    public bool IsHdr => _hdr;

    public long MemorySize
    {
        get
        {
            long total = 0;
            foreach (var face in _faces)
                total += face?.LongLength ?? 0;
            return total;
        }
    }

    // Null until the renderer reports.
    public long? GpuBytes { get; private set; }

    // Null for procedural data.
    public (int Width, int Height)? SourcePanoramaSize { get; private set; }

    // The light the faces hold as nine coefficients, worked out when they arrive, since the faces themselves
    // are let go once the renderer has them. What an ambient light fed from a sky or a probe reads.
    public SphericalHarmonicsL2? Radiance { get; private set; }

    // Gather and decode this cubemap from its URL. The source is a single equirectangular image; six separate
    // face images go through the assembler component instead, which reuses the ordinary texture pipeline per
    // face rather than growing a second one here.
    protected override async Task LoadSelf()
    {
        var descriptor = TargetVariant as CubemapVariantDescriptor ?? CubemapVariantDescriptor.Default;

        var bytes = await AssetManager.RequestGather(AssetURL).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
        {
            FailLoad($"No image data gathered for {AssetURL}");
            return;
        }

        byte[]? panorama;
        int width, height;
        bool hdr;
        try
        {
            // An HDR panorama cannot go through the 8-bit decoder: Radiance would come back tone-mapped
            // and the sky or probe would lose the range it was chosen for. It decodes to half floats
            // instead and keeps that range on the faces, a lamp at 50 included. -xlinka
            // The panorama is a picture like any other: under the texture size cap it is decoded
            // already reduced, and the faces come from that.
            hdr = TextureMetadata.DetectHdr(bytes);
            int cap = EngineSettings.MaxTextureSize;
            panorama = hdr
                ? CappedImageDecoder.DecodeRgbaHalf(bytes, cap, out width, out height, out _, out _)
                : CappedImageDecoder.DecodeRgba(bytes, cap, out width, out height, out _, out _);
        }
        catch (Exception ex)
        {
            FailLoad($"Failed to decode panorama {AssetURL}: {ex.Message}");
            return;
        }

        if (panorama == null || width <= 0 || height <= 0)
        {
            FailLoad($"Failed to decode panorama {AssetURL}: no pixels");
            return;
        }

        SourcePanoramaSize = (width, height);
        // Never past what the panorama holds. A requested size above it (the texture-size cap arrives as
        // one) only upsamples, and six faces of that is real memory, twice over for HDR.
        int faceSize = System.Math.Min(CubemapProjector.ChooseFaceSize(descriptor.FaceSize, width),
            CubemapProjector.SourceFaceSize(width));
        var faces = hdr
            ? CubemapProjector.ProjectPanoramaHalf(panorama, width, height, faceSize)
            : CubemapProjector.ProjectPanorama(panorama, width, height, faceSize);

        SetFaces(faces, faceSize, descriptor.GenerateMipmaps, hdr);

        if (Hook != null)
            await Hook.WaitForUploadAsync().ConfigureAwait(false);
    }

    // Every face must be square and the same size: RGBA8, or half-float RGBA with hdr set.
    public void SetFaces(byte[][] faces, int faceSize, bool mipmaps = true, bool hdr = false)
    {
        if (faces == null || faces.Length != 6)
            throw new ArgumentException("A cubemap needs exactly six faces", nameof(faces));
        if (faceSize < MinFaceSize)
            throw new ArgumentOutOfRangeException(nameof(faceSize), $"Face size must be at least {MinFaceSize}");

        int expected = faceSize * faceSize * (hdr ? 8 : 4);
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i] == null || faces[i].Length < expected)
                throw new ArgumentException(
                    $"Face {(CubemapFace)i} is {faces[i]?.Length ?? 0} bytes, expected {expected}", nameof(faces));
        }

        _faces = faces;
        _faceSize = faceSize;
        _hasMipmaps = mipmaps;
        _hdr = hdr;
        try
        {
            Radiance = CubemapHarmonics.Project(faces, faceSize, hdr);
        }
        catch (Exception)
        {
            Radiance = null;
        }
        Version++;

        var hook = Hook;
        hook?.UploadCubemap(new CubemapUploadRequest
        {
            Faces = faces,
            FaceSize = faceSize,
            GenerateMipmaps = mipmaps,
            Hdr = hdr,
            Report = bytes => GpuBytes = bytes,
        });
        if (hook != null)
            _ = ReleaseFacesAfterUploadAsync(hook, Version);
    }

    // The renderer builds its cube from the faces once and nothing else reads them, so they go when the
    // upload lands: six faces of a big sky are hundreds of megabytes held for nobody. Without a renderer
    // the faces are the only copy and stay. -xlinka
    private async Task ReleaseFacesAfterUploadAsync(ICubemapAssetHook hook, int version)
    {
        try
        {
            await hook.WaitForUploadAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }
        if (Version == version)
            _faces = Array.Empty<byte[]>();
    }

    public static CubemapAsset FromFaces(byte[][] faces, int faceSize, bool mipmaps = true)
    {
        var asset = new CubemapAsset();
        asset.InitializeDynamic();
        asset.SetFaces(faces, faceSize, mipmaps);
        return asset;
    }

    public override void Unload()
    {
        _faces = Array.Empty<byte[]>();
        _faceSize = 0;
        _hasMipmaps = false;
        _hdr = false;
        GpuBytes = null;
        SourcePanoramaSize = null;
        Radiance = null;
        base.Unload();
    }
}

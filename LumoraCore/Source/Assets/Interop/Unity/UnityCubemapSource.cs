// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lumora.Core.Math;
using Lumora.Core.Persistence;
using Lumora.Core.Photos;

namespace Lumora.Core.Assets.Interop.Unity;

// A texture the project imported as a cubemap, redrawn as the panorama our cubemaps are made from. The
// project's picture can be six faces in a row, a column or a cross, a latitude-longitude panorama, or a
// mirrored ball, and its import settings say which or leave it to the picture's shape: 6:1 and 1:6 are
// rows of faces, 4:3 and 3:4 crosses, 2:1 a panorama and anything else a ball.
//
// Every pixel of our panorama asks for its direction, turned into the project's left-handed space (X
// flips), and reads the source there. The faces are the ordinary graphics cube layout in that space, a
// panorama keeps +X at its centre column and up along its top row, and a ball sees +Z at its centre. -xlinka
internal static class UnityCubemapSource
{
    private enum Layout { Faces, Panorama, Ball }

    private const int MaxWidth = 4096;
    // A ball's picture holds far less of the sphere than its pixel count suggests; reflections read it soft anyway.
    private const int MaxBallWidth = 2048;

    private static readonly ConditionalWeakTable<UnityPackageArchive, Dictionary<string, Uri?>> _done = new();

    public static async Task<Uri?> ImportAsync(UnityPackageArchive archive, UnityPackageAsset asset, LocalDB? localDB, List<string> notes)
    {
        var done = _done.GetValue(archive, _ => new Dictionary<string, Uri?>(StringComparer.OrdinalIgnoreCase));
        lock (done)
            if (done.TryGetValue(asset.Guid, out var known))
                return known;
        Uri? uri = null;
        try
        {
            uri = await Convert(asset, localDB, notes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            notes.Add($"{Path.GetFileName(asset.Path)}: the cubemap could not be made ({ex.Message}).");
        }
        lock (done)
            done[asset.Guid] = uri;
        return uri;
    }

    private static async Task<Uri?> Convert(UnityPackageAsset asset, LocalDB? localDB, List<string> notes)
    {
        if (asset.File == null)
            return null;
        var bytes = await File.ReadAllBytesAsync(asset.File).ConfigureAwait(false);
        if (TextureMetadata.DetectHdr(bytes))
        {
            notes.Add($"{Path.GetFileName(asset.Path)}: high dynamic range cubemaps are left off.");
            return null;
        }
        byte[]? rgba;
        int w, h;
        if (GifDecoder.IsGif(bytes))
            rgba = GifDecoder.DecodeFirstFrame(bytes, out w, out h);
        else
            rgba = TextureVariantStore.DecodeRgba(bytes, out w, out h);
        if (rgba == null || w <= 0 || h <= 0)
        {
            notes.Add($"{Path.GetFileName(asset.Path)}: the cubemap's picture could not be read.");
            return null;
        }

        var layout = LayoutOf(asset.Meta, w, h);
        int width = layout switch
        {
            Layout.Faces => System.Math.Min(MaxWidth, 4 * FaceEdge(w, h)),
            Layout.Panorama => System.Math.Min(MaxWidth, w),
            _ => System.Math.Min(MaxBallWidth, System.Math.Max(w, h)),
        };
        width = System.Math.Max(16, width & ~1);
        int height = width / 2;
        var source = new Source(rgba, w, h, layout);
        var panorama = new byte[(long)width * height * 4];
        Parallel.For(0, height, y =>
        {
            float v = (y + 0.5f) / height;
            for (int x = 0; x < width; x++)
            {
                var ours = CubemapProjector.PanoramaUvToDirection((x + 0.5f) / width, v);
                var c = source.Sample(new float3(-ours.x, ours.y, ours.z));
                long at = ((long)y * width + x) * 4;
                panorama[at] = c.R;
                panorama[at + 1] = c.G;
                panorama[at + 2] = c.B;
                panorama[at + 3] = c.A;
            }
        });
        var png = PhotoEncoder.Png(panorama, width, height);
        if (localDB != null)
        {
            var local = await localDB.SaveAssetAsync(png, ".png").ConfigureAwait(false);
            return string.IsNullOrEmpty(local) ? null : new Uri(local);
        }
        string path = Path.Combine(PathResolver.TempPath, "cubemap-" + asset.Guid + ".png");
        await File.WriteAllBytesAsync(path, png).ConfigureAwait(false);
        return new Uri(path);
    }

    private static int FaceEdge(int w, int h)
    {
        if (w == 6 * h) return h;
        if (h == 6 * w) return w;
        if (w * 3 == h * 4) return w / 4;
        return h / 4;
    }

    private static Layout LayoutOf(string? metaText, int w, int h)
    {
        int mode = 6;
        if (!string.IsNullOrEmpty(metaText) && UnityYaml.ParseMapping(metaText)["TextureImporter"] is { } importer)
            mode = importer["generateCubemap"]?.Int(6) ?? 6;
        bool faces = w == 6 * h || h == 6 * w || w * 3 == h * 4 || h * 3 == w * 4;
        return mode switch
        {
            1 => Layout.Ball,
            2 => Layout.Panorama,
            5 when faces => Layout.Faces,
            _ => faces ? Layout.Faces : w == 2 * h ? Layout.Panorama : Layout.Ball,
        };
    }

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private sealed class Source
    {
        private readonly byte[] _rgba;
        private readonly int _w, _h;
        private readonly Layout _layout;
        private readonly int _face;
        // Where each face's top-left corner sits in the picture, in faces, and whether it is drawn upside down
        // (the bottom face of a vertical cross).
        private readonly (int X, int Y, bool Turned)[] _cells = new (int, int, bool)[6];

        public Source(byte[] rgba, int w, int h, Layout layout)
        {
            _rgba = rgba;
            _w = w;
            _h = h;
            _layout = layout;
            if (layout != Layout.Faces)
                return;
            _face = FaceEdge(w, h);
            if (w == 6 * h)
                for (int i = 0; i < 6; i++) _cells[i] = (i, 0, false);
            else if (h == 6 * w)
                for (int i = 0; i < 6; i++) _cells[i] = (0, i, false);
            else if (w * 3 == h * 4)
            {
                // +Y over the second column; -X +Z +X -Z across the middle; -Y under the second column.
                _cells[0] = (2, 1, false); _cells[1] = (0, 1, false); _cells[2] = (1, 0, false);
                _cells[3] = (1, 2, false); _cells[4] = (1, 1, false); _cells[5] = (3, 1, false);
            }
            else
            {
                // The same cross stood up: -Z hangs under -Y, turned half round.
                _cells[0] = (2, 1, false); _cells[1] = (0, 1, false); _cells[2] = (1, 0, false);
                _cells[3] = (1, 2, false); _cells[4] = (1, 1, false); _cells[5] = (1, 3, true);
            }
        }

        public Rgba Sample(float3 dir)
        {
            float len = MathF.Sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
            if (len < 1e-8f)
                return Read(_w * 0.5f, _h * 0.5f);
            var d = dir / len;
            switch (_layout)
            {
                case Layout.Faces:
                {
                    var face = CubemapProjector.DirectionToFace(d, out float s, out float t);
                    var cell = _cells[(int)face];
                    if (cell.Turned)
                    {
                        s = 1f - s;
                        t = 1f - t;
                    }
                    float fx = (cell.X + System.Math.Clamp(s, 0f, 1f)) * _face;
                    float fy = (cell.Y + System.Math.Clamp(t, 0f, 1f)) * _face;
                    return Read(System.Math.Clamp(fx, cell.X * _face + 0.5f, (cell.X + 1) * _face - 0.5f),
                        System.Math.Clamp(fy, cell.Y * _face + 0.5f, (cell.Y + 1) * _face - 0.5f));
                }
                case Layout.Panorama:
                {
                    float u = 0.5f - MathF.Atan2(d.z, d.x) / (2f * MathF.PI);
                    u -= MathF.Floor(u);
                    float top = MathF.Acos(System.Math.Clamp(d.y, -1f, 1f)) / MathF.PI;
                    return Read(u * _w, top * _h);
                }
                default:
                {
                    float m = 2f * MathF.Sqrt(d.x * d.x + d.y * d.y + (d.z + 1f) * (d.z + 1f));
                    if (m < 1e-6f)
                        m = 1e-6f;
                    float s = d.x / m + 0.5f;
                    float up = d.y / m + 0.5f;
                    return Read(s * _w, (1f - up) * _h);
                }
            }
        }

        // Bilinear, clamped to the picture's edges.
        private Rgba Read(float fx, float fy)
        {
            fx -= 0.5f;
            fy -= 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = System.Math.Clamp(x0 + 1, 0, _w - 1), y1 = System.Math.Clamp(y0 + 1, 0, _h - 1);
            x0 = System.Math.Clamp(x0, 0, _w - 1);
            y0 = System.Math.Clamp(y0, 0, _h - 1);
            long i00 = ((long)y0 * _w + x0) * 4, i10 = ((long)y0 * _w + x1) * 4, i01 = ((long)y1 * _w + x0) * 4, i11 = ((long)y1 * _w + x1) * 4;
            byte Channel(int c)
            {
                float top = _rgba[i00 + c] + (_rgba[i10 + c] - _rgba[i00 + c]) * tx;
                float bottom = _rgba[i01 + c] + (_rgba[i11 + c] - _rgba[i01 + c]) * tx;
                return (byte)System.Math.Clamp((int)MathF.Round(top + (bottom - top) * ty), 0, 255);
            }
            return new Rgba(Channel(0), Channel(1), Channel(2), Channel(3));
        }
    }
}

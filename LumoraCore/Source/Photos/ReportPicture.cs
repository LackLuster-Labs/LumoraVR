// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Photos;

// A picture ready to go with a report: the file, how it is written, its size in pixels.
public sealed record ReportImage(byte[] Bytes, ImageEncoding Encoding, int Width, int Height)
{
    public string ContentType => Encoding switch
    {
        ImageEncoding.Png => "image/png",
        ImageEncoding.Jpg => "image/jpeg",
        _ => "image/webp",
    };

    public string FileName => "picture" + Encoding.Extension();
}

// The picture for a report: what this person sees right now, or one of their photos, small enough for the
// service. Either way the pixels are written out again, which also drops whatever else the file carried
// (where and when a photo was taken). -xlinka
public static class ReportPicture
{
    public const int MaxSide = 1920;
    private const int FilmWidth = 1600;
    private const int FilmHeight = 900;
    private const float VrFieldOfView = 90f;
    private const float Quality = 0.85f;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // The local user's view of a world from their eyes (from the desktop camera itself in third person or
    // free cam). No camera ever films the dash or any other private interface, so the form that asked is
    // not in it. Null when the renderer gave nothing back in time.
    public static async Task<ReportImage?> TakeAsync(World world)
    {
        if (world == null || world.IsDisposed || world.IsDestroyed)
            return null;
        var done = new TaskCompletionSource<ReportImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.StartTask(async () =>
        {
            try
            {
                done.TrySetResult(await Film(world));
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    Logging.Logger.Warn($"Reports: no picture of the view: {ex.Message}");
                done.TrySetResult(null);
            }
        });
        var first = await Task.WhenAny(done.Task, Task.Delay(Patience)).ConfigureAwait(false);
        if (first != done.Task)
        {
            Logging.Logger.Warn("Reports: the picture of the view took too long");
            return null;
        }
        return await done.Task.ConfigureAwait(false);
    }

    private static async Task<ReportImage?> Film(World world)
    {
        await WorldContext.ToWorld();
        if (world.IsDisposed || !View(world, out var eye, out var looking, out var up, out float fov, out bool wearer))
            return null;
        var lens = new PhotoLens(fov, FilmWidth, FilmHeight) { WearerView = wearer };
        var frames = await PhotoShot.TakeRawAsync(world, new[] { (eye, PhotoShot.Looking(looking, up)) }, lens);
        if (frames?[0] is not { } frame || frame.Width <= 0 || frame.Height <= 0)
            return null;
        await WorldContext.ToBackground();
        return Fit(frame.Rgba, frame.Width, frame.Height);
    }

    private static bool View(World world, out float3 eye, out float3 looking, out float3 up, out float fov, out bool wearer)
    {
        var input = Engine.Current?.InputInterface;
        bool vr = input?.IsVRActive == true;
        fov = !vr && input != null && input.DesktopCameraFovY > 1f ? input.DesktopCameraFovY : VrFieldOfView;
        floatQ rotation;
        if (input != null && input.DesktopExternalCameraAim)
        {
            eye = input.DesktopCameraPosition;
            rotation = input.DesktopCameraRotation;
            wearer = false;
        }
        else if (world.LocalUser?.Root?.HeadSlot is { IsDestroyed: false } head)
        {
            eye = head.GlobalPosition;
            rotation = head.GlobalRotation;
            wearer = true;
        }
        else
        {
            eye = looking = up = float3.Zero;
            wearer = false;
            return false;
        }
        looking = rotation * float3.Backward;
        up = rotation * float3.Up;
        return true;
    }

    // A photo's file made fit to send. Null when it does not read as an image.
    public static Task<ReportImage?> PrepareAsync(byte[] encoded) => Task.Run(() => Prepare(encoded));

    public static ReportImage? Prepare(byte[] encoded)
    {
        byte[]? rgba;
        int width, height;
        try
        {
            rgba = TextureVariantStore.DecodeRgba(encoded, out width, out height);
        }
        catch (Exception)
        {
            return null;
        }
        if (rgba == null || width <= 0 || height <= 0)
            return null;
        return Fit(rgba, width, height);
    }

    // No wider or taller than MaxSide, then written as a JPEG; one still over the service's limit (or a PNG,
    // which is all a machine with no renderer can write) is brought down a quarter at a time until it fits.
    public static ReportImage? Fit(byte[] rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4)
            return null;
        Flatten(rgba);
        float scale = MathF.Min(1f, (float)MaxSide / System.Math.Max(width, height));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int w = System.Math.Max(1, (int)MathF.Round(width * scale));
            int h = System.Math.Max(1, (int)MathF.Round(height * scale));
            var pixels = w == width && h == height ? rgba : Shrink(rgba, width, height, w, h);
            var (bytes, written) = PhotoEncoder.Encode(pixels, w, h, ImageEncoding.Jpg, Quality);
            if (bytes is { Length: > 0 } && bytes.Length <= ReportLimits.ImageMaxBytes)
                return new ReportImage(bytes, written, w, h);
            scale *= 0.75f;
        }
        return null;
    }

    // JPEG keeps no alpha, so anything see-through sits on white rather than on whatever colour its clear
    // pixels happened to hold.
    private static void Flatten(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255)
                continue;
            int clear = 255 - a;
            rgba[i] = (byte)((rgba[i] * a + 255 * clear) / 255);
            rgba[i + 1] = (byte)((rgba[i + 1] * a + 255 * clear) / 255);
            rgba[i + 2] = (byte)((rgba[i + 2] * a + 255 * clear) / 255);
            rgba[i + 3] = 255;
        }
    }

    // Each output pixel the average of the source pixels under it.
    private static byte[] Shrink(byte[] rgba, int width, int height, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int oy = 0; oy < h; oy++)
        {
            int y0 = (int)((long)oy * height / h);
            int y1 = System.Math.Max(y0 + 1, (int)((long)(oy + 1) * height / h));
            for (int ox = 0; ox < w; ox++)
            {
                int x0 = (int)((long)ox * width / w);
                int x1 = System.Math.Max(x0 + 1, (int)((long)(ox + 1) * width / w));
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * width * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = row + x * 4;
                        r += rgba[i];
                        g += rgba[i + 1];
                        b += rgba[i + 2];
                        a += rgba[i + 3];
                        n++;
                    }
                }
                int o = (oy * w + ox) * 4;
                output[o] = (byte)(r / n);
                output[o + 1] = (byte)(g / n);
                output[o + 2] = (byte)(b / n);
                output[o + 3] = (byte)(a / n);
            }
        }
        return output;
    }
}

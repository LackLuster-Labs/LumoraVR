// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;

namespace Lumora.Core.Photos;

// The picture a saved thing's card shows: the thing alone on a clear background, from three-quarters round
// and a little above, in a studio's light whatever the world around it is doing. An avatar is taken from its
// front, anything else from the corner of its own box nearest the side its saver is looking at. Filmed
// through PhotoShot by a camera of this machine's own at twice the size, then cut to a square around what
// actually drew and brought down, which keeps the framing tight whatever the bounds got wrong. Null with no
// renderer behind the camera, when nothing of it drew, or when the picture takes too long; a save goes ahead
// without one. -xlinka
public static class ItemThumbnail
{
    public const int Size = 256;
    private const int Film = Size * 2;
    private const float FieldOfView = 30f;
    // Of the picture's side, clear on each side of the thing.
    private const float Margin = 0.07f;
    private const float Deg = MathF.PI / 180f;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    public static async Task<byte[]?> CaptureAsync(Slot slot)
    {
        var world = slot?.World;
        if (slot == null || slot.IsDestroyed || world == null || world.IsDisposed || world.IsDestroyed)
            return null;
        var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.StartTask(async () =>
        {
            try
            {
                done.TrySetResult(await TakeAsync(world, slot));
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    Logging.Logger.Warn($"Inventory: no picture of \"{slot.Name}\": {ex.Message}");
                done.TrySetResult(null);
            }
        });
        var first = await Task.WhenAny(done.Task, Task.Delay(Patience)).ConfigureAwait(false);
        if (first != done.Task)
        {
            Logging.Logger.Warn($"Inventory: the picture of \"{slot.Name}\" took too long, saving it without one");
            return null;
        }
        return await done.Task.ConfigureAwait(false);
    }

    private static async Task<byte[]?> TakeAsync(World world, Slot slot)
    {
        await WorldContext.ToWorld();
        if (slot.IsDestroyed || !Frame(world, slot, out var centre, out var look, out float distance, out float radius))
            return null;
        // A first picture that runs off an edge is taken again from further back, once. One that still does
        // is not the thing alone: a camera with no band left to borrow films everything.
        for (int attempt = 0; ; attempt++)
        {
            var lens = new PhotoLens(FieldOfView, Film, Film)
            {
                Only = slot,
                Transparent = true,
                Studio = true,
                NearClip = MathF.Max(0.001f, (distance - radius) * 0.5f),
            };
            var frames = await PhotoShot.TakeRawAsync(world, new[] { (centre - look * distance, PhotoShot.Looking(look, float3.Up)) }, lens);
            // Gone while it was filmed, the camera had nothing left to keep to and filmed the world.
            if (slot.IsDestroyed || frames?[0] is not { } frame || frame.Width <= 0 || frame.Height <= 0)
                return null;
            await WorldContext.ToBackground();
            var square = Cut(frame, out bool clipped);
            if (square == null)
                return null;
            if (clipped)
            {
                if (attempt > 0)
                    return null;
                distance *= 2f;
                await WorldContext.ToWorld();
                if (slot.IsDestroyed)
                    return null;
                continue;
            }
            // PNG, the one encoding here that keeps the clear background.
            return PhotoEncoder.Encode(square, Size, Size, ImageEncoding.Png, 1f).Bytes;
        }
    }

    // A picture's own card: the image whole, fitted into the square and centred, clear around it. Null when
    // the file does not read as an image.
    public static byte[]? FromImage(byte[] encoded)
    {
        byte[]? rgba;
        int w, h;
        try
        {
            rgba = TextureVariantStore.DecodeRgba(encoded, out w, out h);
        }
        catch (Exception)
        {
            return null;
        }
        if (rgba == null || w <= 0 || h <= 0)
            return null;

        // Weighted by coverage while filtering, like a film frame, so a clear edge pulls in no dark rim.
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            rgba[i] = (byte)(rgba[i] * a / 255);
            rgba[i + 1] = (byte)(rgba[i + 1] * a / 255);
            rgba[i + 2] = (byte)(rgba[i + 2] * a / 255);
        }

        float scale = MathF.Min((float)Size / w, (float)Size / h);
        int fw = System.Math.Clamp((int)MathF.Round(w * scale), 1, Size);
        int fh = System.Math.Clamp((int)MathF.Round(h * scale), 1, Size);
        int left = (Size - fw) / 2, top = (Size - fh) / 2;
        float step = 1f / scale;
        int taps = System.Math.Clamp((int)MathF.Ceiling(step), 1, 8);

        var output = new byte[Size * Size * 4];
        Span<float> sum = stackalloc float[4];
        for (int oy = 0; oy < fh; oy++)
        {
            for (int ox = 0; ox < fw; ox++)
            {
                sum.Clear();
                for (int ty = 0; ty < taps; ty++)
                {
                    float sy = (oy + (ty + 0.5f) / taps) * step - 0.5f;
                    for (int tx = 0; tx < taps; tx++)
                        Bilinear(rgba, w, h, (ox + (tx + 0.5f) / taps) * step - 0.5f, sy, sum);
                }
                float k = 1f / (taps * taps);
                float a = sum[3] * k;
                int o = ((top + oy) * Size + left + ox) * 4;
                output[o + 3] = (byte)System.Math.Clamp((int)(a + 0.5f), 0, 255);
                if (a < 0.5f)
                    continue;
                float unpremultiply = 255f / a;
                for (int c = 0; c < 3; c++)
                    output[o + c] = (byte)System.Math.Clamp((int)(sum[c] * k * unpremultiply + 0.5f), 0, 255);
            }
        }
        Bleed(output);
        return PhotoEncoder.Encode(output, Size, Size, ImageEncoding.Png, 1f).Bytes;
    }

    // Where the camera stands: turned round from the side it is seen from and lifted, then back far enough
    // that every corner of the thing's box is in view.
    private static bool Frame(World world, Slot slot, out float3 centre, out float3 look, out float distance, out float radius)
    {
        centre = float3.Zero;
        look = float3.Backward;
        distance = 0f;
        radius = 0f;
        if (!SlotBoundsHelper.TryComputeLocalBounds(slot, out var box))
            return false;
        Span<float3> corners = stackalloc float3[8];
        centre = slot.LocalPointToGlobal(box.Center);
        for (int i = 0; i < 8; i++)
        {
            corners[i] = slot.LocalPointToGlobal(new float3(
                (i & 1) == 0 ? box.Min.x : box.Max.x,
                (i & 2) == 0 ? box.Min.y : box.Max.y,
                (i & 4) == 0 ? box.Min.z : box.Max.z));
            radius = MathF.Max(radius, (corners[i] - centre).Length);
        }
        if (!(radius > 1e-5f) || float.IsInfinity(radius))
            return false;

        bool avatar = slot.GetComponentInChildren<AvatarForm>() != null;
        float3 front = avatar
            ? slot.GlobalRotation * float3.Backward
            : (world.LocalUser?.Root?.HeadPosition ?? centre + slot.GlobalRotation * float3.Backward) - centre;
        front.y = 0f;
        if (front.LengthSquared < 1e-8f)
            front = slot.GlobalRotation * float3.Backward;
        front.y = 0f;
        front = front.LengthSquared < 1e-8f ? float3.Backward : front.Normalized;

        float3 round;
        if (avatar || !TryDiagonal(slot, front, out round))
        {
            float turn = (avatar ? 25f : 35f) * Deg;
            round = new float3(front.x * MathF.Cos(turn) + front.z * MathF.Sin(turn), 0f, front.z * MathF.Cos(turn) - front.x * MathF.Sin(turn));
        }
        float rise = (avatar ? 8f : 24f) * Deg;
        var toward = (round * MathF.Cos(rise) + float3.Up * MathF.Sin(rise)).Normalized;
        look = -toward;

        var rotation = PhotoShot.Looking(look, float3.Up);
        var right = rotation * float3.Right;
        var up = rotation * float3.Up;
        float tan = MathF.Tan(FieldOfView * 0.5f * Deg);
        foreach (var corner in corners)
        {
            var d = corner - centre;
            float reach = MathF.Max(MathF.Abs(float3.Dot(d, right)), MathF.Abs(float3.Dot(d, up)));
            distance = MathF.Max(distance, reach / tan + float3.Dot(d, toward));
        }
        distance = MathF.Max(distance * 1.08f, radius * 1.05f);
        return true;
    }

    // Of a thing standing about upright, the corner of its own box nearest the side it is seen from, so the
    // picture is three-quarters of its shape and not of wherever its saver happened to stand.
    private static bool TryDiagonal(Slot slot, float3 seen, out float3 diagonal)
    {
        diagonal = seen;
        var rotation = slot.GlobalRotation;
        if (float3.Dot(rotation * float3.Up, float3.Up) < 0.7f)
            return false;
        var x = rotation * float3.Right;
        x.y = 0f;
        if (x.LengthSquared < 1e-6f)
            return false;
        x = x.Normalized;
        var z = new float3(-x.z, 0f, x.x);
        float best = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            var corner = (x * ((i & 1) == 0 ? 1f : -1f) + z * ((i & 2) == 0 ? 1f : -1f)).Normalized;
            float score = float3.Dot(corner, seen);
            if (score > best)
            {
                best = score;
                diagonal = corner;
            }
        }
        return true;
    }

    // The film cut to a square round everything that drew, with a margin, and brought down to Size. The
    // film comes back with its colours already scaled by coverage at the edges, so it is filtered like that
    // and divided out after. Clear pixels by an edge take the colour of the nearest drawn one, or filtering
    // on the card would pull a dark rim into every edge. Null when nothing drew. -xlinka
    private static byte[]? Cut(PixelFrame frame, out bool clipped)
    {
        clipped = false;
        int w = frame.Width, h = frame.Height;
        var rgba = frame.Rgba;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (rgba[row + x * 4 + 3] < 8)
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0)
            return null;
        clipped = minX == 0 || minY == 0 || maxX == w - 1 || maxY == h - 1;

        float side = MathF.Max(maxX - minX + 1, maxY - minY + 1) / (1f - 2f * Margin);
        side = MathF.Max(side, 24f);
        float left = (minX + maxX + 1) * 0.5f - side * 0.5f;
        float top = (minY + maxY + 1) * 0.5f - side * 0.5f;
        float step = side / Size;
        int taps = System.Math.Clamp((int)MathF.Ceiling(step), 1, 4);

        var output = new byte[Size * Size * 4];
        Span<float> sum = stackalloc float[4];
        for (int oy = 0; oy < Size; oy++)
        {
            for (int ox = 0; ox < Size; ox++)
            {
                sum.Clear();
                for (int ty = 0; ty < taps; ty++)
                {
                    float sy = top + (oy + (ty + 0.5f) / taps) * step - 0.5f;
                    for (int tx = 0; tx < taps; tx++)
                    {
                        float sx = left + (ox + (tx + 0.5f) / taps) * step - 0.5f;
                        Bilinear(rgba, w, h, sx, sy, sum);
                    }
                }
                float scale = 1f / (taps * taps);
                int o = (oy * Size + ox) * 4;
                float a = sum[3] * scale;
                output[o + 3] = (byte)System.Math.Clamp((int)(a + 0.5f), 0, 255);
                if (a < 0.5f)
                    continue;
                if (a >= 254.5f)
                {
                    for (int c = 0; c < 3; c++)
                        output[o + c] = (byte)System.Math.Clamp((int)(sum[c] * scale + 0.5f), 0, 255);
                    continue;
                }
                float coverage = a / 255f;
                for (int c = 0; c < 3; c++)
                {
                    float linear = ToLinear(sum[c] * scale / 255f) / coverage;
                    output[o + c] = (byte)System.Math.Clamp((int)(ToGamma(linear) * 255f + 0.5f), 0, 255);
                }
            }
        }
        Bleed(output);
        return output;
    }

    private static void Bilinear(byte[] rgba, int w, int h, float x, float y, Span<float> sum)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        for (int j = 0; j < 2; j++)
        {
            int py = y0 + j;
            if (py < 0 || py >= h)
                continue;
            float wy = j == 0 ? 1f - fy : fy;
            for (int i = 0; i < 2; i++)
            {
                int px = x0 + i;
                if (px < 0 || px >= w)
                    continue;
                float weight = wy * (i == 0 ? 1f - fx : fx);
                if (weight <= 0f)
                    continue;
                int at = (py * w + px) * 4;
                sum[0] += rgba[at] * weight;
                sum[1] += rgba[at + 1] * weight;
                sum[2] += rgba[at + 2] * weight;
                sum[3] += rgba[at + 3] * weight;
            }
        }
    }

    private static float ToLinear(float v)
        => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static float ToGamma(float v)
    {
        v = System.Math.Clamp(v, 0f, 1f);
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }

    // A few pixels out is all filtering reaches; the clear rest stays black and costs the file nothing.
    private static void Bleed(byte[] rgba)
    {
        const int Reach = 3;
        int count = Size * Size;
        var steps = new byte[count];
        var queue = new int[count];
        int head = 0, tail = 0;
        for (int i = 0; i < count; i++)
        {
            if (rgba[i * 4 + 3] == 0)
                continue;
            steps[i] = 1;
            queue[tail++] = i;
        }
        while (head < tail)
        {
            int i = queue[head++];
            if (steps[i] > Reach)
                continue;
            int x = i % Size;
            if (x > 0) Spread(i, i - 1);
            if (x < Size - 1) Spread(i, i + 1);
            if (i >= Size) Spread(i, i - Size);
            if (i < count - Size) Spread(i, i + Size);
        }

        void Spread(int from, int to)
        {
            if (steps[to] != 0)
                return;
            steps[to] = (byte)(steps[from] + 1);
            rgba[to * 4] = rgba[from * 4];
            rgba[to * 4 + 1] = rgba[from * 4 + 1];
            rgba[to * 4 + 2] = rgba[from * 4 + 2];
            queue[tail++] = to;
        }
    }
}

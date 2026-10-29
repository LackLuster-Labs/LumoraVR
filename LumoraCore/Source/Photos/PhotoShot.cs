// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Photos;

// How a photo is filmed: the width of view and size, and what is kept out of it. Mask takes away whole
// bands (name plates, lasers); LeaveOut takes away objects; Only films one object and nothing else, on a
// clear background when Transparent. Studio adds a photo studio's light for the picture alone.
public readonly record struct PhotoLens(float FieldOfView, int Width, int Height)
{
    public int Mask { get; init; } = -1;
    public IReadOnlyList<Slot>? LeaveOut { get; init; } = null;
    public Slot? Only { get; init; } = null;
    public bool Transparent { get; init; } = false;
    public bool WearerView { get; init; } = false;
    public float NearClip { get; init; } = 0.02f;
    public bool Studio { get; init; } = false;
}

// Photos from points of view: cameras of this machine's own, never sent, film one frame each at the size
// asked, all in the same frame, read back and written as an image. What the renderer keeps out of every
// camera (anything private) stays out. Anything another camera here is keeping out of its own view is kept
// out of a photo too: two cameras cannot keep the same object out on their own, so the photo leaves out
// every band the cameras borrow. Runs in a world task: it waits for frames. -xlinka
public static class PhotoShot
{
    // A camera looks down its slot's -Z.
    public static floatQ Looking(float3 direction, float3 up) => floatQ.LookRotation(-direction, up).Conjugate;

    // Quads and text are readable from +Z, opposite to a camera's viewing direction.
    public static floatQ Facing(float3 viewerDirection, float3 up) => Looking(-viewerDirection, up);

    public static async Task<byte[]?> TakeAsync(World world, float3 position, floatQ rotation, float fieldOfView, int width, int height,
        ImageEncoding encoding, IReadOnlyList<Slot>? leaveOut = null)
    {
        var frames = await TakeRawAsync(world, new[] { (position, rotation) }, new PhotoLens(fieldOfView, width, height) { LeaveOut = leaveOut });
        var frame = frames?[0];
        if (frame == null)
            return null;
        return PhotoEncoder.Encode(frame.Rgba, frame.Width, frame.Height, encoding, Quality(encoding)).Bytes;
    }

    public static float Quality(ImageEncoding encoding) => encoding == ImageEncoding.Png ? 1f : 0.92f;

    // Every view filmed through the same lens, in order; null when the renderer gave nothing back.
    public static async Task<PixelFrame?[]?> TakeRawAsync(World world, IReadOnlyList<(float3 Position, floatQ Rotation)> views, PhotoLens lens)
    {
        await WorldContext.ToWorld();
        if (world.IsDisposed || views.Count == 0)
            return null;
        var slot = world.RootSlot.AddLocalSlot("Photo Cameras");
        var targets = new RenderTextureProvider[views.Count];
        var frames = new PixelFrame?[views.Count];
        try
        {
            for (int i = 0; i < views.Count; i++)
            {
                var eye = slot.AddLocalSlot("View " + i);
                eye.GlobalPosition = views[i].Position;
                eye.GlobalRotation = views[i].Rotation;
                var target = eye.AttachComponent<RenderTextureProvider>();
                target.Size.Value = new int2(lens.Width, lens.Height);
                var camera = eye.AttachComponent<Camera>();
                camera.FieldOfView.Value = System.Math.Clamp(lens.FieldOfView, 1f, 170f);
                camera.NearClip.Value = lens.NearClip;
                camera.FarClip.Value = 4000f;
                camera.CullingMask.Value = lens.Mask & ~RenderLayerOverride.CameraPoolLayers;
                camera.WearerView.Value = lens.WearerView;
                camera.StudioLight.Value = lens.Studio;
                if (lens.Transparent)
                {
                    camera.Clear.Value = ClearMode.Color;
                    camera.BackgroundColor.Value = new color(0f, 0f, 0f, 0f);
                }
                camera.TargetTexture.Target = target;
                if (lens.Only is { IsDestroyed: false } only)
                    camera.SelectiveRender.Add(only);
                else if (lens.LeaveOut != null)
                {
                    foreach (var hidden in lens.LeaveOut)
                    {
                        if (hidden is { IsDestroyed: false })
                            camera.ExcludeRender.Add(hidden);
                    }
                }
                targets[i] = target;
            }
            for (int wait = 0; wait < 600 && !AllReady(targets); wait++)
                await WorldContext.NextUpdate();
            if (!AllReady(targets))
                return null;
            // The cameras hand their view to the textures on their own update.
            await WorldContext.NextUpdate();
            await WorldContext.NextUpdate();
            var reads = new Task<PixelFrame?>[targets.Length];
            for (int i = 0; i < targets.Length; i++)
                reads[i] = ((RenderTexture)targets[i].Asset).CaptureRawAsync();
            for (int i = 0; i < reads.Length; i++)
                frames[i] = await reads[i];
        }
        finally
        {
            world.RunSynchronously(() =>
            {
                if (!slot.IsDestroyed)
                    slot.Destroy();
            });
        }
        await WorldContext.ToWorld();
        return frames;
    }

    private static bool AllReady(RenderTextureProvider[] targets)
    {
        for (int i = 0; i < targets.Length; i++)
        {
            if (targets[i].Asset is not RenderTexture)
                return false;
        }
        return true;
    }
}

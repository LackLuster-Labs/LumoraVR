// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

#if GODOT_PC
using System;
using System.Runtime.InteropServices;
using Godot;
using Godot.NativeInterop;

namespace Lumora.Godot.Hooks.Video;

// A DECODED PICTURE LIVES IN THE ENGINE'S MEMORY, NOT OURS.
//
// The planes are one engine byte array, held by the one-element array the device's texture call takes. libVLC
// writes straight into it, and the card's copy is made from it as it stands, so a frame never passes through a
// managed array on its way up: handing a managed array to the engine costs a fresh native allocation and a
// copy every frame, which was most of what a playing video cost the main thread. Nothing on the engine side
// ever writes the array, so it is never copied on write and Pointer stays put until Dispose.
//
// Layout, one byte a texel: pitch x lumaLines of brightness, then chromaLines rows holding U on the left half
// and V on the right half, so the whole picture is one R8 texture pitch x (lumaLines + chromaLines). -xlinka
internal sealed unsafe class VideoFrameBuffer : IDisposable
{
    public readonly int Width;
    public readonly int Height;
    public readonly int Pitch;
    public readonly int LumaLines;
    public readonly int ChromaLines;
    public readonly int Size;
    public readonly IntPtr Pointer;
    public readonly global::Godot.Collections.Array<byte[]> Layers = new();
    public readonly RDTextureFormat Format;

    public VideoFrameBuffer(int width, int height, int pitch, int lumaLines, int chromaLines)
    {
        Width = width;
        Height = height;
        Pitch = pitch;
        LumaLines = lumaLines;
        ChromaLines = chromaLines;
        Size = pitch * (lumaLines + chromaLines);

        // Black until the first picture lands; zeroed planes would read as green.
        byte* fill = (byte*)NativeMemory.Alloc((nuint)Size);
        try
        {
            new Span<byte>(fill, pitch * lumaLines).Fill(16);
            new Span<byte>(fill + pitch * lumaLines, pitch * chromaLines).Fill(128);
            godot_packed_byte_array packed = NativeFuncs.godotsharp_packed_byte_array_new_mem_copy(fill, Size);
            try
            {
                Pointer = (IntPtr)packed.Buffer;
                NativeFuncs.godotsharp_variant_new_packed_byte_array(out godot_variant variant, in packed);
                using var held = Variant.CreateTakingOwnershipOfDisposableValue(in variant);
                ((global::Godot.Collections.Array)Layers).Add(held);
            }
            finally
            {
                packed.Dispose();
            }
        }
        finally
        {
            NativeMemory.Free(fill);
        }

        Format = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R8Unorm,
            Width = (uint)pitch,
            Height = (uint)(lumaLines + chromaLines),
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
    }

    public ReadOnlySpan<byte> Span => new((void*)Pointer, Size);

    public bool Matches(int width, int height, int pitch, int lumaLines)
        => Width == width && Height == height && Pitch == pitch && LumaLines == lumaLines;

    public void Dispose()
    {
        ((global::Godot.Collections.Array)Layers).Dispose();
        Format.Dispose();
    }
}
#endif

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core.Media;

namespace Lumora.Godot.Hooks.Video;

// Where a decoder hands its picture. The hook implements this and owns the GPU texture, so a backend
// never has to know whether it is feeding a material, a UI quad or nothing at all. Everything here runs
// on the main thread from inside the backend's Tick. -xlinka
//
// A planar 4:2:0 picture is one R8 texture, pitch x (lumaLines + chromaLines): the brightness rows, then the
// colour rows with U on the left half and V on the right half. width and height are the visible picture,
// which may be smaller than the planes.
public interface IVideoFrameTarget
{
    // Seconds the viewer needs between pictures, read once a tick: below zero none (nobody can see it), zero
    // every decoded frame. A backend that uploads off the main thread paces itself by it; sound never is.
    double FrameInterval { get; }

    // A picture the backend already put on the card. The target owns the texture from here and frees it once
    // a newer one replaces it.
    void PresentPlanes(Rid texture, int width, int height, int pitch, int lumaLines, int chromaLines);

    // The same picture in memory, for when the card cannot take it off the main thread. The target copies
    // out of it before returning.
    void SubmitPlanes(ReadOnlySpan<byte> planes, int width, int height, int pitch, int lumaLines, int chromaLines);

    // For a backend that already owns a Godot texture and would only lose time being read back into
    // ours. The object must stay the same for the life of the backend.
    void PresentTexture(Texture2D? texture, int width, int height);
}

// One decoder. The hook picks an implementation per Load and drives it from the main thread; the
// decoder itself runs on whatever threads it likes and only publishes through Tick. -xlinka
public interface IVideoDecoderBackend : IDisposable
{
    // MediaServices.EngineNative / MediaServices.EngineLibVlc.
    string EngineName { get; }

    // Seconds the decoder is ahead (+) or behind (-) the position the core last asked for.
    float CurrentClockError { get; }

    VideoStats Stats { get; }

    event Action<VideoReadyInfo>? Ready;
    event Action<string>? Failed;

    void Open(string source, string? audioSource, int? audioTrackIndex, bool isStream, bool isLive, int maxWidth, int maxHeight);

    void UpdateState(bool play, bool loop, double position, float speed);

    void SetVolume(float volume);
    void SetAudioTrack(int? index);
    void SetAudioSink(IVideoAudioSink? sink);

    // Main thread, once per frame: publish a decoded frame if one is waiting and apply whatever the
    // last UpdateState asked for. Nothing else touches a scene object; a backend may make device
    // textures on its own threads.
    void Tick(IVideoFrameTarget target);

    void Stop();
}

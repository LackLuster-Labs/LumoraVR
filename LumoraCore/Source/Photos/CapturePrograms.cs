// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Photos;

// The streaming program's processes, by name, compared without ".exe" and ignoring case. The program being
// open only says to go and read its log and link; its mux helper runs only while it writes a recording, so
// seeing that one is a confirmation.
//
// Nothing else is looked for. Other capture programs, the driver overlays, the OS game bar and voice chat
// apps never say when they capture, so a mark for them would mean nothing. -xlinka
public static class CapturePrograms
{
    public enum Kind : byte
    {
        None,
        Obs,
        Capturing,
    }

    private static readonly (string Name, Kind Kind)[] Known =
    {
        ("obs64", Kind.Obs),
        ("obs32", Kind.Obs),
        ("obs", Kind.Obs),
        // started for a file recording and closed when the file ends, by every program built on the same core
        ("obs-ffmpeg-mux", Kind.Capturing),
    };

    public static Kind Classify(ReadOnlySpan<char> processName)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        foreach (var (known, kind) in Known)
        {
            if (name.Equals(known, StringComparison.OrdinalIgnoreCase))
                return kind;
        }
        return Kind.None;
    }
}

// One look at what runs on this machine. A probe hands it every process name; it keeps only what it found,
// so a look allocates nothing past the probe's own reading.
public sealed class CaptureScan
{
    public bool ObsOpen { get; private set; }

    public CaptureState Strongest { get; private set; }

    public int Looked { get; private set; }

    public void Reset()
    {
        ObsOpen = false;
        Strongest = CaptureState.None;
        Looked = 0;
    }

    public void See(ReadOnlySpan<char> processName)
    {
        Looked++;
        switch (CapturePrograms.Classify(processName))
        {
            case CapturePrograms.Kind.Obs:
                ObsOpen = true;
                break;
            case CapturePrograms.Kind.Capturing:
                Strongest = CaptureState.Recording;
                break;
        }
    }
}

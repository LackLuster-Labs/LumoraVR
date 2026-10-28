// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net.Quic;
using System.Runtime.Versioning;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNQ;

// QUIC needs msquic: Windows 11 or Server 2022 (our Windows builds carry it), or Linux with libmsquic installed.
// Android, Quest and Pico have none. Where it is missing the transport is never registered, so nothing is
// advertised and an lnq address is passed over when joining. -xlinka
public static class LNQSupport
{
    public const string Scheme = "lnq";

    private static bool? _available;

    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    public static bool IsAvailable => _available ??= Probe();

    private static bool Probe()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return false;
        try
        {
            bool supported = QuicListener.IsSupported && QuicConnection.IsSupported;
            if (!supported)
                LumoraLogger.Log("[QUIC] Not available on this system (no msquic); QUIC addresses are skipped");
            return supported;
        }
        catch (Exception ex)
        {
            LumoraLogger.Log($"[QUIC] Not available: {ex.Message}");
            return false;
        }
    }
}

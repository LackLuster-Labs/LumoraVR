// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net.Sockets;

namespace Lumora.Nexus.Transport.LNT;

// Our own frames decide what goes first, so the kernel should hold as little unsent as it can while keeping the
// link full: no Nagle delay, and on Linux a low-water mark on unsent bytes so a fresh foreground frame is never
// queued behind megabytes of bulk already handed over. Elsewhere the kernel's own sizing stays. -xlinka
internal static class LNTSockets
{
    private const int TcpLevel = 6;
    private const int LinuxNotSentLowWater = 25;
    private const int NotSentLowWaterBytes = 128 * 1024;

    public static void Configure(Socket socket)
    {
        try
        {
            socket.NoDelay = true;
        }
        catch (SocketException)
        {
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            try
            {
                socket.SetRawSocketOption(TcpLevel, LinuxNotSentLowWater, BitConverter.GetBytes(NotSentLowWaterBytes));
            }
            catch (Exception)
            {
            }
        }
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Net.Sockets;

namespace Lumora.Core.Helpers
{
    // Free-port discovery by BINDING, not by enumerating listeners.
    //
    // This used to ask IPGlobalProperties for every active listener and pick a random port not in the
    // list. .NET on Android has no implementation of that call and throws PlatformNotSupportedException,
    // which surfaced as the local home failing to start on a Pico 4 and the whole engine init aborting
    // in Phase 4. Binding a socket to a candidate port and closing it again is what "is this port free"
    // actually means, it is implemented everywhere .NET runs, and it also catches ports the listener
    // table would not show (reserved ranges, a socket mid-teardown). -xlinka
    internal class SimpleIpHelpers
    {
        private static readonly int minPort = 49152;
        private static readonly int maxPort = 65535;

        public static int GetAvailablePortUdpOrThrow(int maxAtemptsint)
            => GetAvailablePortUdp(maxAtemptsint) ?? throw new Exception("No available ports found");

        public static int GetAvailablePortTcpOrThrow(int maxAtemptsint)
            => GetAvailablePortTcp(maxAtemptsint) ?? throw new Exception("No available ports found");

        public static int? GetAvailablePortUdp(int maxAtemptsint)
            => Probe(maxAtemptsint, ProtocolType.Udp, SocketType.Dgram);

        // How many ports up from the chosen hosting port further hosted worlds may take.
        public const int HostingPortSpan = 32;

        // The port a world about to be hosted listens on: the first free one from the hosting port set in the
        // settings, which is what a forwarded range needs, or any free one when none is set or the range is full.
        public static ushort HostingPortUdp()
        {
            int first = EngineSettings.HostingPort;
            if (first > 0)
            {
                for (int port = first; port < first + HostingPortSpan && port <= ushort.MaxValue; port++)
                {
                    if (CanBind(port, ProtocolType.Udp, SocketType.Dgram))
                        return (ushort)port;
                }
                Logging.Logger.Warn($"[lnl] Hosting ports {first}-{first + HostingPortSpan - 1} are all taken; using any free port");
            }
            return (ushort)(GetAvailablePortUdp(10) ?? 6000);
        }

        public static int? GetAvailablePortTcp(int maxAtemptsint)
            => Probe(maxAtemptsint, ProtocolType.Tcp, SocketType.Stream);

        private static int? Probe(int attempts, ProtocolType protocol, SocketType socketType)
        {
            var rand = new Random();
            for (var i = 0; i < System.Math.Max(attempts, 1); i++)
            {
                int port = rand.Next(minPort, maxPort);
                if (CanBind(port, protocol, socketType))
                    return port;
            }
            return null;
        }

        private static bool CanBind(int port, ProtocolType protocol, SocketType socketType)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, socketType, protocol);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}

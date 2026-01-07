using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lumora.Core.Helpers
{
    internal class SimpleIpHelpers
    {
        private static readonly int minPort = 49152;
        private static readonly int maxPort = 65535;
        public static int GetAvailablePortUdpOrThrow(int maxAtemptsint)
        {
            var rand = new Random();
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var udpConnInfoArray = ipGlobalProperties.GetActiveUdpListeners();
            for (var i = 0; i < maxAtemptsint; i++)
            {
                var port = rand.Next(minPort, maxPort);
                if (!udpConnInfoArray.Any(ucpi => ucpi.Port == port))
                {
                    return port;
                }
            }
            throw new Exception("No available ports found");
        }
        public static int GetAvailablePortTcpOrThrow(int maxAtemptsint)
        {
            var rand = new Random();
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();
            for (var i = 0; i < maxAtemptsint; i++)
            {
                var port = rand.Next(minPort, maxPort);
                if (!tcpConnInfoArray.Any(ucpi => ucpi.Port == port))
                {
                    return port;
                }
            }
            throw new Exception("No available ports found");
        }
        public static int? GetAvailablePortUdp(int maxAtemptsint)
        {
            var rand = new Random();
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var udpConnInfoArray = ipGlobalProperties.GetActiveUdpListeners();
            for (var i = 0; i < maxAtemptsint; i++)
            {
                var port = rand.Next(minPort, maxPort);
                if (!udpConnInfoArray.Any(ucpi => ucpi.Port == port))
                {
                    return port;
                }
            }
            return null;
        }
        public static int? GetAvailablePortTcp(int maxAtemptsint)
        {
            var rand = new Random();
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var TcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();
            for (var i = 0; i < maxAtemptsint; i++)
            {
                var port = rand.Next(minPort, maxPort);
                if (!TcpConnInfoArray.Any(ucpi => ucpi.Port == port))
                {
                    return port;
                }
            }
            return null;
        }
        public static int? GetAvailablePortUdpStrict(int maxAtempts)
        {
            var random = new Random();
            for (var i = 0; i < maxAtempts; i++)
            {
                var port = random.Next(minPort, maxPort);
                if (IsPortAvailableUdp(port))
                {
                    return port;
                }
            }
            return null;
        }
        public static int? GetAvailablePortTcpStrict(int maxAtempts)
        {
            var random = new Random();
            for (var i = 0; i < maxAtempts; i++)
            {
                var port = random.Next(minPort, maxPort);
                if (IsPortAvailableTcp(port))
                {
                    return port;
                }
            }
            return null;
        }
        public static bool IsPortAvailableTcp(int port)
        {
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();
            return tcpConnInfoArray.All(tcpi => tcpi.Port != port);
        }
        public static bool IsPortAvailableUdp(int port)
        {
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var udpConnInfoArray = ipGlobalProperties.GetActiveUdpListeners();
            return udpConnInfoArray.All(ucpi => ucpi.Port != port);
        }
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Lumora.Nexus.Transport.PortMapping;

// UPnP Internet Gateway Device: found by an SSDP search on this network, then driven over SOAP through its
// WANIPConnection (or WANPPPConnection) service. Leases are asked for short; a router that only takes
// permanent forwards gets one, and the sweep on the next start removes any of ours a crash left behind. -xlinka
internal sealed class UpnpIgdClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
    };
    public const string DescriptionPrefix = "Lumora ";

    private readonly IPAddress _local;
    private readonly PortProtocol _protocol;
    private readonly string _protocolName;
    private string? _controlUrl;
    private string? _serviceType;

    public UpnpIgdClient(IPAddress local, PortProtocol protocol = PortProtocol.Udp)
    {
        _local = local;
        _protocol = protocol;
        _protocolName = protocol == PortProtocol.Tcp ? "TCP" : "UDP";
    }

    public async Task<bool> DiscoverAsync(CancellationToken cancellationToken)
    {
        var locations = await SearchAsync(cancellationToken).ConfigureAwait(false);
        foreach (var location in locations)
        {
            try
            {
                var text = await Http.GetStringAsync(location, cancellationToken).ConfigureAwait(false);
                var xml = XDocument.Parse(text);
                var baseUrl = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;
                foreach (var service in xml.Descendants().Where(e => e.Name.LocalName == "service"))
                {
                    var type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "";
                    var control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value ?? "";
                    if (control.Length == 0 || !(type.Contains(":WANIPConnection:") || type.Contains(":WANPPPConnection:")))
                        continue;
                    var root = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? location : baseUrl.Trim());
                    _controlUrl = new Uri(root, control.Trim()).ToString();
                    _serviceType = type.Trim();
                    return true;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
        return false;
    }

    private async Task<List<string>> SearchAsync(CancellationToken cancellationToken)
    {
        var found = new List<string>();
        using var socket = new UdpClient(new IPEndPoint(_local, 0));
        var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        foreach (var target in SearchTargets)
        {
            var search = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + target + "\r\n\r\n");
            await socket.SendAsync(search, search.Length, group).ConfigureAwait(false);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2.5));
        try
        {
            while (true)
            {
                var answer = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var match = Regex.Match(Encoding.ASCII.GetString(answer.Buffer), @"(?im)^location:\s*(\S+)");
                if (match.Success && !found.Contains(match.Groups[1].Value))
                    found.Add(match.Groups[1].Value);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }
        return found;
    }

    public async Task<PortMapResult?> MapAsync(int internalPort, int externalPort, int lifetimeSeconds, CancellationToken cancellationToken)
    {
        var (ok, fault, _) = await CallAsync("AddPortMapping", new[]
        {
            ("NewRemoteHost", ""),
            ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewProtocol", _protocolName),
            ("NewInternalPort", internalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewInternalClient", _local.ToString()),
            ("NewEnabled", "1"),
            ("NewPortMappingDescription", DescriptionPrefix + internalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewLeaseDuration", lifetimeSeconds.ToString(CultureInfo.InvariantCulture)),
        }, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return PortMapResult.No("UPnP error " + fault);

        var (gotAddress, _, values) = await CallAsync("GetExternalIPAddress", Array.Empty<(string, string)>(), cancellationToken).ConfigureAwait(false);
        var external = IPAddress.None;
        if (gotAddress && values.TryGetValue("NewExternalIPAddress", out var text))
            IPAddress.TryParse(text, out external!);
        return new PortMapResult
        {
            Mapped = true,
            External = external ?? IPAddress.None,
            ExternalPort = externalPort,
            LifetimeSeconds = lifetimeSeconds,
        };
    }

    public async Task UnmapAsync(int externalPort, CancellationToken cancellationToken)
        => await CallAsync("DeletePortMapping", new[]
        {
            ("NewRemoteHost", ""),
            ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewProtocol", _protocolName),
        }, cancellationToken).ConfigureAwait(false);

    // Our own forwards to this machine whose port nothing here listens on any more: left by a crash under a
    // router that only takes permanent forwards.
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var stale = new List<int>();
        for (int index = 0; index < 128; index++)
        {
            var (ok, _, values) = await CallAsync("GetGenericPortMappingEntry", new[]
            {
                ("NewPortMappingIndex", index.ToString(CultureInfo.InvariantCulture)),
            }, cancellationToken).ConfigureAwait(false);
            if (!ok)
                break;
            if (values.GetValueOrDefault("NewPortMappingDescription", "").StartsWith(DescriptionPrefix, StringComparison.Ordinal)
                && values.GetValueOrDefault("NewInternalClient", "") == _local.ToString()
                && values.GetValueOrDefault("NewProtocol", "") == _protocolName
                && int.TryParse(values.GetValueOrDefault("NewExternalPort", ""), out int external)
                && int.TryParse(values.GetValueOrDefault("NewInternalPort", ""), out int inside)
                && PortIsFree(inside))
                stale.Add(external);
        }
        foreach (int external in stale)
            await UnmapAsync(external, cancellationToken).ConfigureAwait(false);
    }

    private bool PortIsFree(int port)
    {
        try
        {
            using var probe = _protocol == PortProtocol.Tcp
                ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                : new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // One SOAP action. Ok and the response's fields, or the UPnP error code it failed with.
    private async Task<(bool Ok, string Fault, Dictionary<string, string> Values)> CallAsync(string action,
        (string Name, string Value)[] arguments, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_controlUrl == null || _serviceType == null)
            return (false, "no gateway", values);
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
            .Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:").Append(action)
            .Append(" xmlns:u=\"").Append(_serviceType).Append("\">");
        foreach (var (name, value) in arguments)
            body.Append('<').Append(name).Append('>').Append(System.Security.SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml"),
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_serviceType}#{action}\"");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            XDocument? xml = null;
            try
            {
                xml = XDocument.Parse(text);
            }
            catch (System.Xml.XmlException)
            {
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
                return (false, code ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), values);
            }
            var reply = xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == action + "Response");
            if (reply != null)
            {
                foreach (var field in reply.Elements())
                    values[field.Name.LocalName] = field.Value;
            }
            return (true, "", values);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, ex.GetType().Name, values);
        }
    }
}

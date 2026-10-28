// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Cloud;

// The NAT/relay nodes the api lists. One shared copy, fetched when someone asks and it is older than the
// entries say they stay good for. A failed fetch keeps the last good list, so a hiccup at the api never
// drops a host off its node. Only nodes on this list are ever used: a host registers with no other, and a
// joiner refuses a node address that is not on it. -xlinka
public static class NetworkNodeDirectory
{
    public const string KindNat = "nat";
    public const string KindRelay = "relay";
    public const int ProtocolVersion = 2;

    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly SemaphoreSlim FetchGate = new(1, 1);
    private static IReadOnlyList<NetworkNode> _nodes = Array.Empty<NetworkNode>();
    private static DateTime _fetchedUtc;
    private static DateTime _goodUntilUtc;
    private static bool _reportedFailure;

    public static string ListUrl => ServiceConfig.Current.NodeListUrl;

    public static IReadOnlyList<NetworkNode> Cached => _nodes;

    // The last round trip measured to each node, by address:port, whoever measured it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> Pings = new(StringComparer.OrdinalIgnoreCase);

    public static void RecordPing(NetworkNode node, double milliseconds) => Pings[node.Address + ":" + node.Port] = milliseconds;

    public static double? LastPing(NetworkNode node) => Pings.TryGetValue(node.Address + ":" + node.Port, out var ms) ? ms : null;

    public static async Task<IReadOnlyList<NetworkNode>> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && DateTime.UtcNow < _goodUntilUtc)
            return _nodes;

        await FetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && DateTime.UtcNow < _goodUntilUtc)
                return _nodes;
            if (refresh && DateTime.UtcNow - _fetchedUtc < TimeSpan.FromSeconds(2))
                return _nodes;
            await FetchAsync(cancellationToken).ConfigureAwait(false);
            return _nodes;
        }
        finally
        {
            FetchGate.Release();
        }
    }

    // The listed node a node address points at, by its v4 or v6 address and main port. Not found in the copy
    // at hand, the list is fetched again before the answer is no.
    public static async Task<NetworkNode?> FindListedAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        host = host.Trim('[', ']');
        var found = Match(await GetAsync(false, cancellationToken).ConfigureAwait(false), host, port);
        return found ?? Match(await GetAsync(true, cancellationToken).ConfigureAwait(false), host, port);
    }

    private static NetworkNode? Match(IReadOnlyList<NetworkNode> nodes, string host, int port)
    {
        foreach (var node in nodes)
        {
            if (node.Port == port && node.ProtocolVersion == ProtocolVersion
                && (string.Equals(node.Address, host, StringComparison.OrdinalIgnoreCase)
                    || (node.Address6.Length > 0 && SameAddress(node.Address6, host))))
                return node;
        }
        return null;
    }

    private static bool SameAddress(string a, string b)
        => System.Net.IPAddress.TryParse(a, out var x) && System.Net.IPAddress.TryParse(b, out var y) && x.Equals(y);

    // The order a hosted session tries nodes in: the picked one first when it is listed, then the rest by
    // their last round trip, the ones never measured after those, in list order. Draining and full nodes are
    // left out.
    public static List<NetworkNode> Ranked(string preferredId)
    {
        var ranked = _nodes
            .Where(n => n.Kind == KindNat && n.ProtocolVersion == ProtocolVersion && n.HasCapacity)
            .Select((node, index) => (node, index))
            .OrderBy(t => preferredId.Length > 0 && t.node.Id == preferredId ? 0 : 1)
            .ThenBy(t => LastPing(t.node) ?? double.MaxValue)
            .ThenBy(t => t.index)
            .Select(t => t.node)
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ranked.RemoveAll(n => !seen.Add(n.Id + "|" + n.Address + ":" + n.Port));
        return ranked;
    }

    private static async Task FetchAsync(CancellationToken cancellationToken)
    {
        _fetchedUtc = DateTime.UtcNow;
        try
        {
            using var response = await Http.GetAsync(ListUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var nodes = Parse(body);
            _nodes = nodes;
            int lifetime = nodes.Where(n => n.Ttl > 0).Select(n => n.Ttl).DefaultIfEmpty(0).Min();
            _goodUntilUtc = DateTime.UtcNow + (lifetime > 0 ? TimeSpan.FromSeconds(Math.Clamp(lifetime, 10, 300)) : DefaultLifetime);
            if (_reportedFailure)
                LumoraLogger.Log($"[lnl] Node list back, {nodes.Count} node(s)");
            _reportedFailure = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _goodUntilUtc = DateTime.UtcNow + FailureRetry;
            if (!_reportedFailure)
                LumoraLogger.Warn($"[lnl] Node list unavailable at {ListUrl} ({ex.Message}); keeping {_nodes.Count} known node(s)");
            _reportedFailure = true;
        }
    }

    private static IReadOnlyList<NetworkNode> Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    root = property.Value;
                    break;
                }
            }
        }
        if (root.ValueKind != JsonValueKind.Array)
            return Array.Empty<NetworkNode>();

        var nodes = new List<NetworkNode>();
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            var node = new NetworkNode
            {
                Id = Text(element, "id"),
                Name = Text(element, "name"),
                Region = Text(element, "region"),
                Address = Text(element, "address"),
                Address6 = Text(element, "address6"),
                Port = Number(element, "port"),
                TcpPort = Math.Clamp(Number(element, "tcpPort"), 0, 65535),
                Kind = Text(element, "kind"),
                ProtocolVersion = Number(element, "protocolVersion"),
                UserCount = Number(element, "userCount"),
                Capacity = Number(element, "capacity"),
                Ttl = Number(element, "ttl"),
                LastSeen = Time(element, "lastSeen")
            };
            if (node.IsUsable)
                nodes.Add(node);
        }
        return nodes;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string Text(JsonElement element, string name)
        => TryGet(element, name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => ""
        } : "";

    private static int Number(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d))
            return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int i) ? i : 0;
    }

    private static DateTime Time(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var v))
            return default;
        if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
            return t;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long unix))
            return unix > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return default;
    }
}

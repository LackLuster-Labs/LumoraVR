// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.PortMapping;

public enum PortMapState
{
    Off,
    Searching,
    Mapped,
    Refused,
    NoRouter,
    NotPublic,
}

// Asks the router to forward a hosted session's port (UDP for LNL and QUIC, TCP for TCP), so people outside reach
// it directly: PCP first, then NAT-PMP on the same router port, then UPnP. The forward is leased for ten minutes and renewed at half that,
// so one left by a crash lapses on its own; it is removed outright when the session ends. A router whose own
// outside address is private (carrier NAT) is no use to forward on, and the forward is taken back. -xlinka
public sealed class PortMapper : IDisposable
{
    private const int LeaseSeconds = 600;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private readonly int _port;
    private readonly PortProtocol _protocol;
    private readonly string _name;
    private CancellationTokenSource? _cts;
    private Func<CancellationToken, Task>? _unmap;

    public PortMapper(int port, PortProtocol protocol = PortProtocol.Udp)
    {
        _port = port;
        _protocol = protocol;
        _name = protocol == PortProtocol.Tcp ? "TCP" : "UDP";
    }

    public PortProtocol Protocol => _protocol;

    public PortMapState State { get; private set; } = PortMapState.Off;

    // "PCP", "NAT-PMP" or "UPnP" once mapped.
    public string Method { get; private set; } = "";

    // The router's outside address and the port it forwards, while mapped.
    public IPEndPoint? External { get; private set; }

    public int InternalPort => _port;

    // Raised off the main thread when the state or the outside address moves.
    public event Action? Changed;

    public void Start()
    {
        if (_cts != null)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    // Takes the forward back before returning, up to two seconds, so quitting never leaves one standing.
    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
            return;
        cts.Cancel();
        var unmap = Interlocked.Exchange(ref _unmap, null);
        if (unmap != null)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                Task.Run(() => unmap(limit.Token)).Wait(TimeSpan.FromSeconds(2));
                LumoraLogger.Log($"[lnl] Router forward of {_name} {_port} removed");
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Removing the router forward of {_name} {_port} failed: {ex.GetBaseException().Message}");
            }
        }
        Set(PortMapState.Off, "", null);
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Set(PortMapState.Searching, "", null);
                var (state, renewEvery) = await MapOnceAsync(cancellationToken).ConfigureAwait(false);
                if (state != PortMapState.Mapped)
                {
                    await Task.Delay(state == PortMapState.NoRouter ? RetryAfter * 5 : RetryAfter, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                // Renewed with the same protocol for as long as the router keeps saying yes.
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(renewEvery, cancellationToken).ConfigureAwait(false);
                    var renewed = await RenewAsync(cancellationToken).ConfigureAwait(false);
                    if (!renewed)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Router forward of {_name} {_port}: {ex.Message}");
                try
                {
                    await Task.Delay(RetryAfter, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private Func<CancellationToken, Task<PortMapResult?>>? _renew;

    private async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        var renew = _renew;
        var result = renew != null ? await renew(cancellationToken).ConfigureAwait(false) : null;
        if (result is { Mapped: true })
            return true;
        LumoraLogger.Warn($"[lnl] Router stopped renewing the forward of {_name} {_port}: {result?.Detail ?? "no answer"}");
        Interlocked.Exchange(ref _unmap, null);
        Set(PortMapState.Refused, Method, null);
        return false;
    }

    private async Task<(PortMapState State, TimeSpan RenewEvery)> MapOnceAsync(CancellationToken cancellationToken)
    {
        if (!RouterAddress.TryFind(out var gateway, out var local))
        {
            Set(PortMapState.NoRouter, "", null);
            return (PortMapState.NoRouter, default);
        }

        bool refused = false;
        var answers = new List<string>();
        var pcp = new PcpClient(gateway, local, _protocol);
        var result = await pcp.MapAsync(_port, _port, LeaseSeconds, cancellationToken).ConfigureAwait(false);
        if (result is { Mapped: true })
            return Accept("PCP", result, ct => pcp.MapAsync(_port, result.ExternalPort, LeaseSeconds, ct), ct => pcp.UnmapAsync(_port, ct));
        refused |= result is { Refused: true };
        answers.Add(result?.Detail ?? "no PCP");

        var pmp = new NatPmpClient(gateway, local, _protocol);
        result = await pmp.MapAsync(_port, _port, LeaseSeconds, cancellationToken).ConfigureAwait(false);
        if (result is { Mapped: true })
            return Accept("NAT-PMP", result, ct => pmp.MapAsync(_port, result.ExternalPort, LeaseSeconds, ct), ct => pmp.UnmapAsync(_port, ct));
        refused |= result is { Refused: true };
        answers.Add(result?.Detail ?? "no NAT-PMP");

        var upnp = new UpnpIgdClient(local, _protocol);
        if (await upnp.DiscoverAsync(cancellationToken).ConfigureAwait(false))
        {
            await upnp.SweepAsync(cancellationToken).ConfigureAwait(false);
            int lease = LeaseSeconds;
            for (int external = _port; external < _port + 8 && external <= ushort.MaxValue; external++)
            {
                result = await upnp.MapAsync(_port, external, lease, cancellationToken).ConfigureAwait(false);
                // Some routers only take permanent forwards; the sweep on a later start covers a crash.
                if (result is { Mapped: false } && result.Detail.EndsWith(" 725", StringComparison.Ordinal))
                {
                    lease = 0;
                    result = await upnp.MapAsync(_port, external, lease, cancellationToken).ConfigureAwait(false);
                }
                if (result is { Mapped: true })
                {
                    int port = external;
                    int held = lease;
                    return Accept("UPnP", result, ct => upnp.MapAsync(_port, port, held, ct), ct => upnp.UnmapAsync(port, ct));
                }
                // 718: that outside port is forwarded to someone else already; try the next one up.
                if (result == null || !result.Detail.EndsWith(" 718", StringComparison.Ordinal))
                    break;
            }
            refused = true;
            answers.Add(result?.Detail ?? "no UPnP answer");
        }
        else
        {
            answers.Add("no UPnP");
        }

        var state = refused ? PortMapState.Refused : PortMapState.NoRouter;
        LumoraLogger.Log($"[lnl] Router forward of {_name} {_port}: {(refused ? "the router refused" : "no router answered")} ({string.Join(", ", answers)})");
        Set(state, "", null);
        return (state, default);
    }

    private (PortMapState, TimeSpan) Accept(string method, PortMapResult result,
        Func<CancellationToken, Task<PortMapResult?>> renew, Func<CancellationToken, Task> unmap)
    {
        _unmap = unmap;
        _renew = renew;
        var external = new IPEndPoint(result.External, result.ExternalPort);
        if (!RouterAddress.IsPublic(result.External))
        {
            // Forwarding behind another NAT reaches nobody; take it back now.
            LumoraLogger.Log($"[lnl] Router forward of {_name} {_port} by {method} lands on {external}, which is not public; taking it back");
            var undo = Interlocked.Exchange(ref _unmap, null);
            if (undo != null)
                _ = Task.Run(() => undo(CancellationToken.None));
            Set(PortMapState.NotPublic, method, null);
            return (PortMapState.NotPublic, default);
        }
        int lifetime = result.LifetimeSeconds > 0 ? result.LifetimeSeconds : LeaseSeconds;
        var renewEvery = TimeSpan.FromSeconds(Math.Clamp(lifetime / 2, 30, LeaseSeconds / 2));
        LumoraLogger.Log($"[lnl] Router forwards {external} to {_name} {_port} by {method}, lease {(result.LifetimeSeconds > 0 ? result.LifetimeSeconds + " s" : "permanent")}");
        Set(PortMapState.Mapped, method, external);
        return (PortMapState.Mapped, renewEvery);
    }

    private void Set(PortMapState state, string method, IPEndPoint? external)
    {
        if (State == state && Method == method && Equals(External, external))
            return;
        State = state;
        Method = method;
        External = external;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[lnl] Router forward change handler threw: {ex.Message}");
        }
    }
}

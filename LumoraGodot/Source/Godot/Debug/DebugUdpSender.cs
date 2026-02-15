using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lumora.Core.Logging;

namespace Aquamarine.Godot.Debug;

#nullable enable

/// <summary>
/// Sends log messages, performance data, and memory breakdowns
/// to the debug console process via UDP on localhost.
/// Fire-and-forget: if the console isn't running, packets are silently dropped.
/// </summary>
public class DebugUdpSender : IDisposable
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _endpoint;
    public const int Port = 19840;

    public DebugUdpSender()
    {
        _client = new UdpClient();
        _endpoint = new IPEndPoint(IPAddress.Loopback, Port);

        Logger.OnLogWritten += OnLog;
        AppDomain.CurrentDomain.UnhandledException += OnCrash;
    }

    private void OnLog(Logger.LogLevel level, string timestamp, string message)
    {
        // Sanitize pipes from message to not break protocol
        var safeMsg = message.Replace('|', '/');
        Send($"{level}|{timestamp}|{safeMsg}");
    }

    private void OnCrash(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Send($"ERROR|{DateTime.Now:HH:mm:ss}|UNHANDLED EXCEPTION: {ex?.Message}");
        Send($"ERROR|{DateTime.Now:HH:mm:ss}|Stack: {ex?.StackTrace}");
        if (e.IsTerminating)
            Send($"ERROR|{DateTime.Now:HH:mm:ss}|APPLICATION TERMINATING");
    }

    /// <summary>
    /// Send performance metrics to the debug console.
    /// </summary>
    public void SendPerf(
        float fps, float frameTime, float renderTime, float physicsTime,
        string worldName, int slots, int components, int users,
        long gcMemBytes, long videoMemBytes, int godotObjects, int godotNodes)
    {
        Send($"PERF|{fps:F1}|{frameTime:F2}|{renderTime:F2}|{physicsTime:F2}" +
             $"|{worldName}|{slots}|{components}|{users}" +
             $"|{gcMemBytes}|{videoMemBytes}|{godotObjects}|{godotNodes}");
    }

    /// <summary>
    /// Send memory breakdown to the debug console.
    /// Format: MEM|totalBytes|gcBytes|gen0|gen1|gen2|name:count:bytes,name:count:bytes,...
    /// </summary>
    public void SendMemory(long totalEstimated, long gcBytes,
        int gen0, int gen1, int gen2,
        IEnumerable<(string name, int count, long bytes)> topComponents)
    {
        var components = string.Join(",",
            topComponents.Select(c => $"{c.name}:{c.count}:{c.bytes}"));
        Send($"MEM|{totalEstimated}|{gcBytes}|{gen0}|{gen1}|{gen2}|{components}");
    }

    private void Send(string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            if (bytes.Length <= 65000)
                _client.Send(bytes, bytes.Length, _endpoint);
        }
        catch { /* fire and forget */ }
    }

    public void Dispose()
    {
        Logger.OnLogWritten -= OnLog;
        AppDomain.CurrentDomain.UnhandledException -= OnCrash;
        _client?.Dispose();
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Godot;
using LogLevel = Lumora.Core.Logging.Logger.LogLevel;

namespace Aquamarine.Godot.Debug;

#nullable enable

/// <summary>
/// Debug console scene root for the separate debug process.
/// Receives logs and performance telemetry over UDP from the game process.
/// </summary>
public partial class DebugWindow : Control
{
    private const int MaxLogEntries = 5000;
    private const float ConnectionTimeoutSec = 2.0f;

    private readonly List<LogEntry> _logEntries = new();
    private readonly object _logLock = new();
    private readonly Queue<string> _packetQueue = new();
    private readonly object _packetLock = new();

    private UdpClient? _udpClient;
    private Thread? _listenerThread;
    private volatile bool _listenerRunning;

    private bool _logsDirty;
    private double _secondsSincePacket = double.MaxValue;
    private long _packetCount;
    private int _lastShownLogCount;

    // UI - Logs tab
    private RichTextLabel? _logDisplay;
    private CheckButton? _filterLog;
    private CheckButton? _filterWarn;
    private CheckButton? _filterError;
    private CheckButton? _filterDebug;
    private LineEdit? _searchBox;
    private CheckButton? _autoScroll;
    private Button? _clearBtn;
    private Label? _statusLabel;

    // UI - Performance tab
    private Label? _fpsLabel;
    private Label? _frameTimeLabel;
    private Label? _renderTimeLabel;
    private Label? _physicsTimeLabel;
    private Label? _worldNameLabel;
    private Label? _slotsLabel;
    private Label? _componentsLabel;
    private Label? _usersLabel;
    private Label? _gcMemLabel;
    private Label? _videoMemLabel;
    private Label? _objectsLabel;
    private Label? _nodesLabel;

    // UI - Memory tab
    private RichTextLabel? _memoryDisplay;
    private Label? _totalMemLabel;
    private Label? _gcLabel;

    private struct LogEntry
    {
        public LogLevel Level;
        public string Timestamp;
        public string Message;
    }

    public override void _Ready()
    {
        ConfigureWindow();
        CacheNodeReferences();
        WireEvents();
        StartUdpListener();

        AddLocalLog(LogLevel.LOG, $"Listening for telemetry on udp://127.0.0.1:{DebugUdpSender.Port}");
        _logsDirty = true;

        if (_memoryDisplay != null && string.IsNullOrEmpty(_memoryDisplay.Text))
        {
            _memoryDisplay.Text = "Waiting for memory packets...";
        }
    }

    public override void _Process(double delta)
    {
        _secondsSincePacket += delta;
        DrainPackets();

        if (_logsDirty)
        {
            _logsDirty = false;
            RebuildLogDisplay();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            GetTree().Quit();
        }
    }

    public override void _ExitTree()
    {
        StopUdpListener();
    }

    private void ConfigureWindow()
    {
        DisplayServer.WindowSetTitle("Lumora Debug Console");
        DisplayServer.WindowSetSize(new Vector2I(900, 650));
        DisplayServer.WindowSetMinSize(new Vector2I(600, 400));

        var screenCount = DisplayServer.GetScreenCount();
        if (screenCount > 1)
        {
            var secondScreen = DisplayServer.ScreenGetPosition(1);
            DisplayServer.WindowSetPosition(secondScreen + new Vector2I(50, 50));
        }
    }

    private void CacheNodeReferences()
    {
        _filterLog = GetNodeOrNull<CheckButton>("%FilterLog");
        _filterWarn = GetNodeOrNull<CheckButton>("%FilterWarn");
        _filterError = GetNodeOrNull<CheckButton>("%FilterError");
        _filterDebug = GetNodeOrNull<CheckButton>("%FilterDebug");
        _searchBox = GetNodeOrNull<LineEdit>("%SearchBox");
        _autoScroll = GetNodeOrNull<CheckButton>("%AutoScroll");
        _clearBtn = GetNodeOrNull<Button>("%ClearBtn");
        _logDisplay = GetNodeOrNull<RichTextLabel>("%LogDisplay");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");

        _fpsLabel = GetNodeOrNull<Label>("%FPSValue");
        _frameTimeLabel = GetNodeOrNull<Label>("%FrameTimeValue");
        _renderTimeLabel = GetNodeOrNull<Label>("%RenderTimeValue");
        _physicsTimeLabel = GetNodeOrNull<Label>("%PhysicsTimeValue");
        _worldNameLabel = GetNodeOrNull<Label>("%WorldNameValue");
        _slotsLabel = GetNodeOrNull<Label>("%SlotsValue");
        _componentsLabel = GetNodeOrNull<Label>("%ComponentsValue");
        _usersLabel = GetNodeOrNull<Label>("%UsersValue");
        _gcMemLabel = GetNodeOrNull<Label>("%GCMemValue");
        _videoMemLabel = GetNodeOrNull<Label>("%VideoMemValue");
        _objectsLabel = GetNodeOrNull<Label>("%ObjectsValue");
        _nodesLabel = GetNodeOrNull<Label>("%NodesValue");

        _memoryDisplay = GetNodeOrNull<RichTextLabel>("%MemoryDisplay");
        _totalMemLabel = GetNodeOrNull<Label>("%TotalMemLabel");
        _gcLabel = GetNodeOrNull<Label>("%GCLabel");
    }

    private void WireEvents()
    {
        _filterLog?.Connect("toggled", Callable.From<bool>(_ => _logsDirty = true));
        _filterWarn?.Connect("toggled", Callable.From<bool>(_ => _logsDirty = true));
        _filterError?.Connect("toggled", Callable.From<bool>(_ => _logsDirty = true));
        _filterDebug?.Connect("toggled", Callable.From<bool>(_ => _logsDirty = true));
        _searchBox?.Connect("text_changed", Callable.From<string>(_ => _logsDirty = true));

        if (_clearBtn != null)
        {
            _clearBtn.Pressed += () =>
            {
                lock (_logLock)
                {
                    _logEntries.Clear();
                }

                _logsDirty = true;
            };
        }
    }

    private void StartUdpListener()
    {
        try
        {
            _udpClient = new UdpClient(DebugUdpSender.Port);
            _udpClient.Client.ReceiveTimeout = 1000;

            _listenerRunning = true;
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "LumoraDebugUdpListener"
            };
            _listenerThread.Start();
        }
        catch (Exception ex)
        {
            AddLocalLog(LogLevel.ERROR, $"Failed to bind UDP port {DebugUdpSender.Port}: {ex.Message}");
            _logsDirty = true;
        }
    }

    private void StopUdpListener()
    {
        _listenerRunning = false;

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
        }
        catch
        {
            // no-op
        }

        if (_listenerThread != null && _listenerThread.IsAlive)
        {
            _listenerThread.Join(500);
        }
    }

    private void ListenLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (_listenerRunning)
        {
            try
            {
                var bytes = _udpClient!.Receive(ref remote);
                var packet = Encoding.UTF8.GetString(bytes);

                lock (_packetLock)
                {
                    _packetQueue.Enqueue(packet);
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }

                if (!_listenerRunning)
                {
                    break;
                }

                EnqueueInternalError($"UDP socket error: {ex.SocketErrorCode}");
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_listenerRunning)
                {
                    break;
                }

                EnqueueInternalError($"UDP listener exception: {ex.Message}");
            }
        }
    }

    private void EnqueueInternalError(string message)
    {
        lock (_packetLock)
        {
            _packetQueue.Enqueue($"ERROR|{DateTime.Now:HH:mm:ss}|{message}");
        }
    }

    private void DrainPackets()
    {
        while (true)
        {
            string? packet = null;

            lock (_packetLock)
            {
                if (_packetQueue.Count > 0)
                {
                    packet = _packetQueue.Dequeue();
                }
            }

            if (packet == null)
            {
                break;
            }

            _packetCount++;
            _secondsSincePacket = 0;
            HandlePacket(packet);
        }
    }

    private void HandlePacket(string packet)
    {
        var segments = packet.Split('|');
        if (segments.Length == 0)
        {
            return;
        }

        var kind = segments[0].Trim().ToUpperInvariant();

        switch (kind)
        {
            case "LOG":
            case "WARN":
            case "ERROR":
            case "DEBUG":
                HandleLogPacket(packet, kind);
                break;
            case "PERF":
                HandlePerfPacket(segments);
                break;
            case "MEM":
                HandleMemoryPacket(segments);
                break;
        }
    }

    private void HandleLogPacket(string packet, string kind)
    {
        var parts = packet.Split('|', 3);
        if (parts.Length < 3)
        {
            return;
        }

        var timestamp = parts[1].Trim();
        var message = parts[2];

        var level = kind switch
        {
            "WARN" => LogLevel.WARN,
            "ERROR" => LogLevel.ERROR,
            "DEBUG" => LogLevel.DEBUG,
            _ => LogLevel.LOG
        };

        OnLogReceived(level, timestamp, message);
    }

    private void HandlePerfPacket(string[] parts)
    {
        if (parts.Length < 13)
        {
            return;
        }

        if (!TryFloat(parts[1], out var fps)) return;
        if (!TryFloat(parts[2], out var frameTime)) return;
        if (!TryFloat(parts[3], out var renderTime)) return;
        if (!TryFloat(parts[4], out var physicsTime)) return;

        var worldName = parts[5];

        if (!int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slots)) return;
        if (!int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var components)) return;
        if (!int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var users)) return;

        if (!long.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gcMemBytes)) return;
        if (!long.TryParse(parts[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var videoMemBytes)) return;
        if (!int.TryParse(parts[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var godotObjects)) return;
        if (!int.TryParse(parts[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var godotNodes)) return;

        SetLabel(_fpsLabel, $"{fps:F1}",
            fps < 30f ? new Color(1f, 0.3f, 0.3f) :
            fps < 60f ? new Color(1f, 0.85f, 0.2f) :
            new Color(0.3f, 0.9f, 0.4f));

        SetLabel(_frameTimeLabel, $"{frameTime:F2} ms");
        SetLabel(_renderTimeLabel, $"{renderTime:F2} ms");
        SetLabel(_physicsTimeLabel, $"{physicsTime:F2} ms");
        SetLabel(_worldNameLabel, worldName);
        SetLabel(_slotsLabel, $"{slots:N0}");
        SetLabel(_componentsLabel, $"{components:N0}");
        SetLabel(_usersLabel, $"{users:N0}");
        SetLabel(_gcMemLabel, FormatBytes(gcMemBytes));
        SetLabel(_videoMemLabel, FormatBytes(videoMemBytes));
        SetLabel(_objectsLabel, $"{godotObjects:N0}");
        SetLabel(_nodesLabel, $"{godotNodes:N0}");
    }

    private void HandleMemoryPacket(string[] parts)
    {
        if (parts.Length < 7 || _memoryDisplay == null)
        {
            return;
        }

        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalEstimated)) return;
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gcBytes)) return;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gen0)) return;
        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gen1)) return;
        if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gen2)) return;

        if (_totalMemLabel != null)
        {
            _totalMemLabel.Text = $"Total: {FormatBytes(totalEstimated)}";
        }

        if (_gcLabel != null)
        {
            _gcLabel.Text = $"GC: {FormatBytes(gcBytes)} | Gen0: {gen0} | Gen1: {gen1} | Gen2: {gen2}";
        }

        _memoryDisplay.Clear();
        var entries = parts[6].Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawEntry in entries)
        {
            var componentParts = rawEntry.Split(':', 3);
            if (componentParts.Length != 3)
            {
                continue;
            }

            var name = componentParts[0];
            if (!int.TryParse(componentParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) continue;
            if (!long.TryParse(componentParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)) continue;

            var pct = totalEstimated > 0 ? bytes * 100f / totalEstimated : 0f;

            _memoryDisplay.PushColor(new Color(0.7f, 0.7f, 0.75f));
            _memoryDisplay.AddText($"  {name}");
            _memoryDisplay.Pop();

            _memoryDisplay.PushColor(new Color(0.5f, 0.5f, 0.55f));
            _memoryDisplay.AddText($" x{count}");
            _memoryDisplay.Pop();

            _memoryDisplay.PushColor(new Color(0.6f, 0.85f, 0.6f));
            _memoryDisplay.AddText($"  {FormatBytes(bytes)} ({pct:F1}%)\n");
            _memoryDisplay.Pop();
        }
    }

    private void OnLogReceived(LogLevel level, string timestamp, string message)
    {
        lock (_logLock)
        {
            _logEntries.Add(new LogEntry
            {
                Level = level,
                Timestamp = timestamp,
                Message = message
            });

            if (_logEntries.Count > MaxLogEntries)
            {
                _logEntries.RemoveRange(0, _logEntries.Count - MaxLogEntries);
            }
        }

        _logsDirty = true;
    }

    private void AddLocalLog(LogLevel level, string message)
    {
        OnLogReceived(level, DateTime.Now.ToString("HH:mm:ss"), message);
    }

    private void RebuildLogDisplay()
    {
        if (_logDisplay == null)
        {
            return;
        }

        var showLog = _filterLog?.ButtonPressed ?? true;
        var showWarn = _filterWarn?.ButtonPressed ?? true;
        var showError = _filterError?.ButtonPressed ?? true;
        var showDebug = _filterDebug?.ButtonPressed ?? true;
        var search = _searchBox?.Text?.Trim() ?? string.Empty;
        var hasSearch = !string.IsNullOrEmpty(search);

        _logDisplay.Clear();

        int total;
        int shown = 0;

        lock (_logLock)
        {
            total = _logEntries.Count;

            foreach (var entry in _logEntries)
            {
                if (entry.Level == LogLevel.LOG && !showLog) continue;
                if (entry.Level == LogLevel.WARN && !showWarn) continue;
                if (entry.Level == LogLevel.ERROR && !showError) continue;
                if (entry.Level == LogLevel.DEBUG && !showDebug) continue;

                if (hasSearch && !entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                shown++;

                _logDisplay.PushColor(new Color(0.4f, 0.4f, 0.45f));
                _logDisplay.AddText($"[{entry.Timestamp}] ");
                _logDisplay.Pop();

                var (levelColor, levelTag) = GetLevelStyle(entry.Level);
                _logDisplay.PushColor(levelColor);
                _logDisplay.PushBold();
                _logDisplay.AddText($"[{levelTag}] ");
                _logDisplay.Pop();
                _logDisplay.Pop();

                var msgColor = entry.Level == LogLevel.ERROR ? new Color(1f, 0.5f, 0.5f) :
                    entry.Level == LogLevel.WARN ? new Color(1f, 0.9f, 0.6f) :
                    entry.Level == LogLevel.DEBUG ? new Color(0.55f, 0.55f, 0.6f) :
                    new Color(0.85f, 0.85f, 0.9f);

                _logDisplay.PushColor(msgColor);
                _logDisplay.AddText(entry.Message);
                _logDisplay.Pop();
                _logDisplay.Newline();
            }
        }

        _lastShownLogCount = shown;
        UpdateStatusLabel(total);

        if ((_autoScroll?.ButtonPressed ?? true) && _logDisplay.GetLineCount() > 0)
        {
            _logDisplay.ScrollToLine(_logDisplay.GetLineCount() - 1);
        }
    }

    private void UpdateStatusLabel(int total)
    {
        if (_statusLabel == null)
        {
            return;
        }

        var connectionState = _secondsSincePacket <= ConnectionTimeoutSec ? "Connected" : "Waiting";
        _statusLabel.Text =
            $"{total} messages | Showing: {_lastShownLogCount} | UDP: {connectionState} | Packets: {_packetCount}";
    }

    private static (Color color, string tag) GetLevelStyle(LogLevel level)
    {
        return level switch
        {
            LogLevel.LOG => (new Color(0.6f, 0.8f, 0.6f), "LOG"),
            LogLevel.WARN => (new Color(1f, 0.85f, 0.2f), "WRN"),
            LogLevel.ERROR => (new Color(1f, 0.3f, 0.3f), "ERR"),
            LogLevel.DEBUG => (new Color(0.5f, 0.5f, 0.55f), "DBG"),
            _ => (new Color(0.7f, 0.7f, 0.7f), "???")
        };
    }

    private static bool TryFloat(string value, out float parsed)
    {
        return float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out parsed);
    }

    private static void SetLabel(Label? label, string text, Color? color = null)
    {
        if (label == null)
        {
            return;
        }

        label.Text = text;
        if (color.HasValue)
        {
            label.AddThemeColorOverride("font_color", color.Value);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

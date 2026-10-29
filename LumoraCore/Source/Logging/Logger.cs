// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Environment = System.Environment;

namespace Lumora.Core.Logging
{
    public static class Logger
    {
        private const int KeepLogFiles = 10;

        private static string? _logFile;
        private static bool _fileResolved;
        private static readonly object _fileLock = new();
        private static readonly DateTime _started = DateTime.UtcNow;
        private static readonly ConcurrentQueue<string> _logQueue = new();
        private static readonly AutoResetEvent _logEvent = new(false);
        private static readonly CancellationTokenSource _cancellationTokenSource = new();
        public delegate void GameEngineLogProxy(LogLevel level,String messages);
        public static event GameEngineLogProxy? LogToGameEngine;
        static Logger()
        {
            Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));
        }

        public static event Action<LogLevel, string, string> OnLogWritten = null!;

        // This session's file, inside the one data folder. Resolved on the first flush rather than in
        // the static constructor, so the folder the bootstrap pins is the one that wins even when
        // something logs before the bootstrap has run. -xlinka
        public static string LogFile
        {
            get { lock (_fileLock) { ResolveFile(); return _logFile ?? string.Empty; } }
        }
        public static string LogDirectory => Persistence.PathResolver.LogsPath;

        // Flip to true when actively diagnosing - Debug calls are firehose and choke
        // the console/file otherwise. Several call sites also read this to skip building expensive
        // diagnostic strings at all, so it stays a plain bool. - xlinka
        public static bool EnableDebug
        {
            get => MinimumLevel == LogLevel.DEBUG;
            set => MinimumLevel = value ? LogLevel.DEBUG : LogLevel.LOG;
        }

        // The floor. Anything below it is dropped before it costs a timestamp, a formatted string, a
        // queue entry or a GD.Print. Default LOG, so DEBUG stays off; set LUMORA_LOG_LEVEL to
        // DEBUG/LOG/WARN/ERROR to move it without a rebuild - WARN is the one to use when you want a
        // session log you can actually read. -xlinka
        public static LogLevel MinimumLevel { get; set; } = ReadLevelFromEnvironment();

        // The enum is ordered by age, not by severity (DEBUG was added last), so rank it explicitly
        // rather than comparing the underlying values.
        private static int Severity(LogLevel level) => level switch
        {
            LogLevel.DEBUG => 0,
            LogLevel.LOG => 1,
            LogLevel.WARN => 2,
            LogLevel.ERROR => 3,
            _ => 1,
        };

        private static LogLevel ReadLevelFromEnvironment()
        {
            var raw = Environment.GetEnvironmentVariable("LUMORA_LOG_LEVEL");
            return raw?.Trim().ToUpperInvariant() switch
            {
                "DEBUG" => LogLevel.DEBUG,
                "LOG" or "INFO" => LogLevel.LOG,
                "WARN" or "WARNING" => LogLevel.WARN,
                "ERROR" => LogLevel.ERROR,
                _ => LogLevel.LOG,
            };
        }

        // REPEAT SUPPRESSION. A line that fires every frame is not information, it is a denial of
        // service on the log: the file balloons (one warning per second per hand left a 292 MB engine
        // log behind, every line of it a stack trace), the console window chokes, and the one line that
        // matters is buried. The same text more than RepeatThreshold times inside RepeatWindow is
        // dropped until the window lapses, then one summary says how many went. Keyed on the exact
        // text, so two hands alternating the same complaint count as two spammers, not none. -xlinka
        private const int RepeatThreshold = 5;
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);
        private const int RepeatTableCap = 512;
        private sealed class RepeatEntry
        {
            public DateTime WindowStart;
            public int Count;
            public int Dropped;
            public LogLevel Level;
        }
        private static readonly Dictionary<string, RepeatEntry> _repeats = new();
        private static readonly object _repeatLock = new();

        private static void WriteLog(LogLevel level, string message)
        {
            if (Severity(level) < Severity(MinimumLevel)) return;
            DateTime now = DateTime.Now;
            string? summary = null;
            lock (_repeatLock)
            {
                if (_repeats.TryGetValue(message, out var entry))
                {
                    if (now - entry.WindowStart > RepeatWindow)
                    {
                        if (entry.Dropped > 0)
                            summary = Summary(message, entry, now);
                        entry.WindowStart = now;
                        entry.Count = 1;
                        entry.Dropped = 0;
                        entry.Level = level;
                    }
                    else
                    {
                        entry.Count++;
                        if (entry.Count > RepeatThreshold)
                        {
                            entry.Dropped++;
                            return;
                        }
                    }
                }
                else
                {
                    if (_repeats.Count >= RepeatTableCap)
                        PruneRepeats(now);
                    _repeats[message] = new RepeatEntry { WindowStart = now, Count = 1, Level = level };
                }
            }
            if (summary != null)
                Emit(level, now, summary);
            Emit(level, now, message);
        }

        private static string Summary(string message, RepeatEntry entry, DateTime now)
            => $"{message} (repeated {entry.Dropped} more times in the last {(now - entry.WindowStart).TotalSeconds:F0}s, dropped)";

        // Entries whose window lapsed with drops still owed get their summary here, so a spammer that
        // simply stops still leaves a count behind. Runs on the flush thread.
        private static void SweepRepeats()
        {
            DateTime now = DateTime.Now;
            List<(LogLevel level, string text)>? due = null;
            lock (_repeatLock)
            {
                List<string>? expired = null;
                foreach (var pair in _repeats)
                {
                    if (now - pair.Value.WindowStart <= RepeatWindow)
                        continue;
                    if (pair.Value.Dropped > 0)
                        (due ??= new()).Add((pair.Value.Level, Summary(pair.Key, pair.Value, now)));
                    (expired ??= new()).Add(pair.Key);
                }
                if (expired != null)
                    foreach (var key in expired)
                        _repeats.Remove(key);
            }
            if (due == null)
                return;
            foreach (var (level, text) in due)
                Emit(level, now, text);
        }

        private static void PruneRepeats(DateTime now)
        {
            List<string>? expired = null;
            foreach (var pair in _repeats)
            {
                if (now - pair.Value.WindowStart > RepeatWindow)
                    (expired ??= new()).Add(pair.Key);
            }
            if (expired != null)
                foreach (var key in expired)
                    _repeats.Remove(key);
            if (_repeats.Count >= RepeatTableCap)
                _repeats.Clear();
        }

        // The game engine's console prints on the logger's own thread. A print there flushes stdout every line
        // in a debug build, and a burst of lines in a load cost the frame that logged them. A warning or an
        // error still prints on the caller's thread, after everything queued ahead of it, so the console keeps
        // its order and the line before a crash is not left sitting in a queue. -xlinka
        private static readonly ConcurrentQueue<(LogLevel Level, string Message)> _engineQueue = new();
        private static readonly object _engineLock = new();

        private static void Emit(LogLevel level, DateTime now, string message)
        {
            string timestamp = now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{timestamp}] [{level}] {message}";
            _logQueue.Enqueue(logEntry);

            if (level == LogLevel.WARN || level == LogLevel.ERROR)
            {
                lock (_engineLock)
                {
                    DrainEngineQueue();
                    if (LogToGameEngine is { } engine)
                        engine(level, message);
                    else
                        Console.WriteLine(logEntry);
                }
            }
            else
            {
                _engineQueue.Enqueue((level, message));
            }
            _logEvent.Set();
            OnLogWritten?.Invoke(level, timestamp, message);
        }

        // Under _engineLock.
        private static void DrainEngineQueue()
        {
            var engine = LogToGameEngine;
            while (_engineQueue.TryDequeue(out var line))
                engine?.Invoke(line.Level, line.Message);
        }

        // Prints whatever is still queued for the game engine's console, on this thread.
        public static void FlushToGameEngine()
        {
            lock (_engineLock)
                DrainEngineQueue();
        }

        private static void ProcessLogQueue(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logEvent.WaitOne(TimeSpan.FromSeconds(10)); // Wait for either a timeout or a new log entry
                    FlushToGameEngine();
                    SweepRepeats();
                    // The bootstrap pins the data folder within the first frames. Until it has, hold the
                    // queue rather than commit the file to the fallback folder; after a few seconds
                    // assume there is no bootstrap (a test, a tool) and write where the default says.
                    if (!Persistence.PathResolver.IsInitialized && DateTime.UtcNow - _started < TimeSpan.FromSeconds(3))
                        continue;
                    FlushLogQueue();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Logger encountered an error: {ex.Message}");
                }
            }
        }

        private static void FlushLogQueue()
        {
            if (_logQueue.IsEmpty) return;
            string? file;
            lock (_fileLock)
            {
                ResolveFile();
                file = _logFile;
            }
            if (string.IsNullOrWhiteSpace(file))
            {
                while (_logQueue.TryDequeue(out _)) { }
                return;
            }

            try
            {
                using var writer = new StreamWriter(file, append: true);
                while (_logQueue.TryDequeue(out var logEntry))
                {
                    writer.WriteLine(logEntry);
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Failed to write logs to file: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Failed to write logs to file: {ex.Message}");
            }
        }

        private static void ResolveFile()
        {
            if (_fileResolved)
                return;
            _fileResolved = true;
            foreach (var directory in CandidateDirectories())
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;
                try
                {
                    Directory.CreateDirectory(directory);
                    _logFile = Path.Combine(directory, $"lumora_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                    PruneOldLogs(directory);
                    return;
                }
                catch
                {
                    // Try the next candidate. Logging must never block startup.
                }
            }
            _logFile = null;
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            yield return Persistence.PathResolver.LogsPath;
            yield return Path.Combine(Path.GetTempPath(), Persistence.PathResolver.FolderName, "logs");
        }

        // Sessions are cheap to start and nobody deletes logs by hand: keep the newest few.
        private static void PruneOldLogs(string directory)
        {
            try
            {
                var files = Directory.GetFiles(directory, "lumora_*.log");
                if (files.Length <= KeepLogFiles)
                    return;
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length - KeepLogFiles; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        public static void Shutdown()
        {
            _cancellationTokenSource.Cancel();
            SweepRepeats();
            FlushLogQueue(); // Ensure any remaining logs are written
        }

        public static void Log(string message)
        {
            WriteLog(LogLevel.LOG, message);
        }
        public static void Warn(string message)
        {
            WriteLog(LogLevel.WARN, message);
        }
        public static void Error(string message)
        {
            WriteLog(LogLevel.ERROR, message);
        }
        public static void Debug(string message)
        {
            WriteLog(LogLevel.DEBUG, message);
        }

        // An interpolated debug line is only put together when debug lines are kept. Hundreds of them sit on
        // paths that run every frame (every material update wrote two), and each built its string just to
        // have it dropped at the floor. -xlinka
        public static void Debug(ref DebugMessage message)
        {
            if (message.Enabled)
                WriteLog(LogLevel.DEBUG, message.Text());
        }

        [InterpolatedStringHandler]
        public ref struct DebugMessage
        {
            private DefaultInterpolatedStringHandler _text;
            public readonly bool Enabled;

            public DebugMessage(int literalLength, int formattedCount, out bool isEnabled)
            {
                Enabled = isEnabled = MinimumLevel == LogLevel.DEBUG;
                _text = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
            }

            public void AppendLiteral(string value) => _text.AppendLiteral(value);
            public void AppendFormatted<T>(T value) => _text.AppendFormatted(value);
            public void AppendFormatted<T>(T value, string? format) => _text.AppendFormatted(value, format);
            public void AppendFormatted<T>(T value, int alignment) => _text.AppendFormatted(value, alignment);
            public void AppendFormatted<T>(T value, int alignment, string? format) => _text.AppendFormatted(value, alignment, format);
            public void AppendFormatted(ReadOnlySpan<char> value) => _text.AppendFormatted(value);
            public void AppendFormatted(string? value) => _text.AppendFormatted(value);
            public void AppendFormatted(object? value, int alignment = 0, string? format = null) => _text.AppendFormatted(value, alignment, format);

            internal string Text() => _text.ToStringAndClear();
        }

        public enum LogLevel
        {
            LOG,
            WARN,
            ERROR,
            DEBUG
        }
    }
}

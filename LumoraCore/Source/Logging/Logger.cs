// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Environment = System.Environment;

namespace Lumora.Core.Logging
{
    public static class Logger
    {
        private static readonly string LogDirectory;
        private static readonly string LogFile;
        private static readonly ConcurrentQueue<string> _logQueue = new();
        private static readonly AutoResetEvent _logEvent = new(false);
        private static readonly CancellationTokenSource _cancellationTokenSource = new();
        private static readonly Lazy<MethodInfo?> GodotPrintMethod = new(() =>
        {
            try
            {
                var godotType = Type.GetType("Godot.GD, GodotSharp");
                return godotType?.GetMethod("Print", new[] { typeof(object[]) });
            }
            catch
            {
                return null;
            }
        });

        static Logger()
        {
            LogDirectory = ResolveLogDirectory();
            LogFile = string.IsNullOrWhiteSpace(LogDirectory)
                ? string.Empty
                : Path.Combine(LogDirectory, $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            // Start the background logging task
            Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));
        }

        public static event Action<string> OnFormattedLogMessageWritten;
        public static event Action<string> OnPrettyLogMessageWritten;
        public static event Action<LogLevel, string, string> OnLogWritten;

        private static void WriteLog(LogLevel level, string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{timestamp}] [{level}] {message}";
            _logQueue.Enqueue(logEntry);
            _logEvent.Set();

            // Keep stdout focused on important messages.
            if (level == LogLevel.WARN || level == LogLevel.ERROR)
            {
                Console.WriteLine(logEntry);
            }

            // Mirror all levels to the Godot output panel so diagnostics from
            // Lumora.Core.Logging.Logger are visible in-editor, not just in the
            // file at %APPDATA%/Lumora/logs/. Filtering this to WARN/ERROR
            // hid debugging info that callers expected to see.
            // - xlinka
            TryMirrorToGodotConsole(level, message);

            OnFormattedLogMessageWritten?.Invoke(message);
            OnLogWritten?.Invoke(level, timestamp, message);
            switch (level)
            {
                case LogLevel.LOG:
                    OnPrettyLogMessageWritten?.Invoke($"  [{DateTime.Now:HH:mm:ss}] [{level}] {message}");
                    break;
                case LogLevel.WARN:
                    OnPrettyLogMessageWritten?.Invoke($"  [{DateTime.Now:HH:mm:ss}] [{level}] {message}");
                    break;
                case LogLevel.ERROR:
                    OnPrettyLogMessageWritten?.Invoke($"  [{DateTime.Now:HH:mm:ss}] [{level}] {message}");
                    break;
                case LogLevel.DEBUG:
                    OnPrettyLogMessageWritten?.Invoke($"  [{DateTime.Now:HH:mm:ss}] [{level}] {message}");
                    break;
            }
        }

        private static void TryMirrorToGodotConsole(LogLevel level, string message)
        {
            try
            {
                var printMethod = GodotPrintMethod.Value;
                if (printMethod != null)
                {
                    printMethod.Invoke(null, new object[] { new object[] { $"[{level}] {message}" } });
                }
            }
            catch
            {
                // Ignore if not running in Godot context.
            }
        }

        private static void ProcessLogQueue(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logEvent.WaitOne(TimeSpan.FromSeconds(10)); // Wait for either a timeout or a new log entry
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
            if (string.IsNullOrWhiteSpace(LogFile))
            {
                while (_logQueue.TryDequeue(out _)) { }
                return;
            }

            try
            {
                using var writer = new StreamWriter(LogFile, append: true);
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

        private static string ResolveLogDirectory()
        {
            foreach (var basePath in GetCandidateBasePaths())
            {
                if (string.IsNullOrWhiteSpace(basePath))
                    continue;

                try
                {
                    var logDirectory = Path.Combine(basePath, "Lumora", "logs");
                    Directory.CreateDirectory(logDirectory);
                    return logDirectory;
                }
                catch
                {
                    // Try the next candidate. Logging must never block startup.
                }
            }

            return string.Empty;
        }

        private static string[] GetCandidateBasePaths()
        {
            return new[]
            {
                TryGetGodotUserDataDir(),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.GetTempPath()
            };
        }

        private static string TryGetGodotUserDataDir()
        {
            try
            {
                var godotOsType = Type.GetType("Godot.OS, GodotSharp");
                var method = godotOsType?.GetMethod("GetUserDataDir", Type.EmptyTypes);
                return method?.Invoke(null, null) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void Shutdown()
        {
            _cancellationTokenSource.Cancel();
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

        public enum LogLevel
        {
            LOG,
            WARN,
            ERROR,
            DEBUG
        }
    }
}

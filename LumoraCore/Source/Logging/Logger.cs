using System;
using System.Collections.Concurrent;
using System.IO;
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

        static Logger()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string gameName = "Lumora";
            LogDirectory = Path.Combine(appData, gameName, "logs");

            // Ensure the directory exists
            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);

            LogFile = Path.Combine(LogDirectory, $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            // Start the background logging task
            Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));
        }

        public static event Action<string> OnFormattedLogMessageWritten;
        public static event Action<string> OnPrettyLogMessageWritten;

        private static void WriteLog(LogLevel level, string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{timestamp}] [{level}] {message}";
            _logQueue.Enqueue(logEntry);
            _logEvent.Set();
            Console.WriteLine(logEntry);

            // Also output to Godot console if running in Godot
            try
            {
                var godotType = Type.GetType("Godot.GD, GodotSharp");
                if (godotType != null)
                {
                    var printMethod = godotType.GetMethod("Print", new[] { typeof(object[]) });
                    if (printMethod != null)
                    {
                        printMethod.Invoke(null, new object[] { new object[] { $"[{level}] {message}" } });
                    }
                }
            }
            catch { /* Ignore if not in Godot context */ }

            OnFormattedLogMessageWritten?.Invoke(message);
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

using System;
using System.IO;
using System.Collections.Generic;
using Godot;
using LumoraVR.Source.Logging;

namespace LumoraVR.Source.Management
{
    public partial class LocalDatabase : Node
    {
        public static LocalDatabase Instance;
        private const string DATABASE_FILENAME = "userdata.db";
        private const string LOCK_FILENAME = "userdata.lock";
        private string _databasePath;
        private string _lockFilePath;
        private FileStream _lockFile;
        private Dictionary<string, object> _cache = new();
        private byte[] _encryptionKey;
        private byte[] _iv;

        public override void _Ready()
        {
            Instance = this;
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            try
            {
                SetupPaths();
                HandleLockFile();
                GenerateEncryptionKey();
                LoadDatabase();
                Logger.Log("Local database initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize database: {ex.Message}");
                throw;
            }
        }

        private void SetupPaths()
        {
            string basePath;
            if (OS.GetName() == "Windows")
            {
                var localLow = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "LumoraVR"
                );
                basePath = localLow;
            }
            else
            {
                // Default Godot path for other platforms
                basePath = Path.Combine(OS.GetUserDataDir(), "LumoraVR");
            }
            if (ArgumentCache.Instance?.Arguments.TryGetValue("dbpath", out string dbpath) ?? false)
            {
                basePath = dbpath;
            }
            _databasePath = Path.Combine(basePath, DATABASE_FILENAME);
            _lockFilePath = Path.Combine(basePath, LOCK_FILENAME);
            Directory.CreateDirectory(basePath);

            Logger.Log($"Database path: {_databasePath}");
        }

        public override void _ExitTree()
        {
            try
            {
                SaveDatabase();
                _lockFile?.Close();
                if (File.Exists(_lockFilePath))
                {
                    File.Delete(_lockFilePath);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during database cleanup: {ex.Message}");
            }
        }
    }
}
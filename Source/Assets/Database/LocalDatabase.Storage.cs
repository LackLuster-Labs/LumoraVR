using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using LumoraVR.Source.Logging;

namespace LumoraVR.Source.Management
{
    public partial class LocalDatabase
    {
        private void LoadDatabase()
        {
            try
            {
                if (!File.Exists(_databasePath))
                {
                    _cache = new Dictionary<string, object>();
                    SaveDatabase();
                    return;
                }

                var encryptedData = File.ReadAllBytes(_databasePath);
                var jsonString = DecryptData(encryptedData);
                _cache = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonString)
                        ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load database: {ex.Message}");
                _cache = new Dictionary<string, object>();
                SaveDatabase();
            }
        }

        private void SaveDatabase()
        {
            try
            {
                var jsonString = JsonSerializer.Serialize(_cache);
                var encryptedData = EncryptData(jsonString);
                File.WriteAllBytes(_databasePath, encryptedData);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save database: {ex.Message}");
                throw;
            }
        }

        public T GetValue<T>(string key, T defaultValue = default)
        {
            try
            {
                if (_cache.TryGetValue(key, out var value))
                {
                    if (value is JsonElement jsonElement)
                    {
                        return JsonSerializer.Deserialize<T>(jsonElement.GetRawText());
                    }
                    return (T)value;
                }
                return defaultValue;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error retrieving value for key {key}: {ex.Message}");
                return defaultValue;
            }
        }

        public void SetValue<T>(string key, T value)
        {
            try
            {
                _cache[key] = value;
                SaveDatabase();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting value for key {key}: {ex.Message}");
                throw;
            }
        }

        public void DeleteValue(string key)
        {
            try
            {
                if (_cache.Remove(key))
                {
                    SaveDatabase();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error deleting value for key {key}: {ex.Message}");
                throw;
            }
        }

        public bool HasKey(string key)
        {
            return _cache.ContainsKey(key);
        }

        public void ClearDatabase()
        {
            try
            {
                _cache.Clear();
                SaveDatabase();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error clearing database: {ex.Message}");
                throw;
            }
        }
    }
}
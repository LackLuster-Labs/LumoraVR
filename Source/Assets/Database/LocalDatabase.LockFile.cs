using System;
using System.IO;
using System.Text;
using Godot;
using LumoraVR.Source.Logging;

namespace LumoraVR.Source.Management
{
    public partial class LocalDatabase
    {
        private void HandleLockFile()
        {
            if (File.Exists(_lockFilePath))
            {
                try
                {
                    var existingLock = File.Open(_lockFilePath, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None);
                    existingLock.Close();
                    File.Delete(_lockFilePath);
                }
                catch (IOException)
                {
                    Logger.Error("Another instance of LumoraVR is running. Multiple instances are not supported.");
                    GetTree().Quit();
                    return;
                }
            }

            _lockFile = File.Open(_lockFilePath, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.None);
            _lockFile.Write(Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString()));
            _lockFile.Flush();
        }
    }
}

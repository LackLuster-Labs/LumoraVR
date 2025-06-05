using System.Linq;
using Godot;

namespace LumoraVR.Source.Management
{
    public partial class ServerManager
    {
        private static ServerType _currentServerType = ServerType.None;
        // Server type enum
        public enum ServerType
        {
            None,
            Local,
            Standard,
            NotAServer
        }

        // Helper method to determine server type
        public static ServerType CurrentServerType
        {
            get
            {
                if (_currentServerType == ServerType.None)
                {
                    if (ArgumentCache.Instance?.IsFlagActive("run-home-server") ?? false)
                    {
                        _currentServerType = ServerType.Local;
                        return _currentServerType;
                    }
                    if (ArgumentCache.Instance?.IsFlagActive("run-server") ?? false)
                    {
                        _currentServerType = ServerType.Standard;
                        return _currentServerType;
                    }
                    _currentServerType = ServerType.NotAServer;
                }
                return _currentServerType;
            }
        }
    }
}

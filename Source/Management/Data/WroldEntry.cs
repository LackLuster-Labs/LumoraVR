using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LumoraVR.Source.Management.Data
{
    public readonly struct WorldEntry
    {
        public WorldEntry(string worldId, string worldName, SessionInfo[] sessions)
        {
            WorldId = worldId;
            WorldName = worldName;
            Sessions = sessions;
        }
        public readonly string WorldId;
        public readonly string WorldName;
        public readonly SessionInfo[] Sessions;
    }
}

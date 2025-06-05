using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LumoraVR.Source.Networking
{
    public partial class LiteNetLibMultiplayerPeer
    {
        public const string RoomKey = "LumoraVR"; //TODO
        public const byte ControlChannel = 0b00111111;
        public const byte ControlSetLocalID = 0x01;
    }
}

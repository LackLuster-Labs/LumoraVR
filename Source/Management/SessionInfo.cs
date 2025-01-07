using System.Net;
using LiteNetLib;

namespace Aquamarine.Source.Management;

public class SessionInfo
{
    public static IPEndPoint SessionServer = NetUtils.MakeEndPoint("relay.xlinka.com", 8000);
    public static IPEndPoint RelayServer = NetUtils.MakeEndPoint("relay.xlinka.com", 8090);
    public static string SessionList = "https://api.xlinka.com/sessions/";
    
    public string Name { get; set; }
    public string SessionIdentifier { get; set; }
    public string WorldIdentifier { get; set; }
    public bool Direct { get; set; }
}

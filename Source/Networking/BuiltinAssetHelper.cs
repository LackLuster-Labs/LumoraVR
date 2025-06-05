using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using FileAccess = Godot.FileAccess;

namespace LumoraVR.Source.Networking;

public static class BuiltinAssetHelper
{
    private const string BuiltinSchema = "builtin://";
    private static readonly List<string> BuiltinAssets = new[]
    {
        "Textures/Dot.png",
        "Assets/Models/headset.gltf",
        
        //John LumoraVR John LumoraVR
        "Assets/Prefabs/johnlumoravr.prefab",
        "Assets/Prefabs/johnlumoravrhumanoid.prefab",
        "Assets/Models/johnlumoravr.glb",
        "Assets/Models/johnlumoravrhumanoid.glb",
        "Assets/Models/johnlumoravr.meshfile",
        "Assets/Models/johnlumoravrhumanoid.meshfile",
    }.Select(i => $"builtin://{i}").ToList();
    public static bool ValidPath(string path)
    {
        if (!path.StartsWith(BuiltinSchema)) return false;
        if (!BuiltinAssets.Contains(path)) return false;
        return true;
    }
    public static byte[] GetBuiltinAssetData(string path)
    {
        if (ValidPath(path))
        {
            var realPath = path.Replace(BuiltinSchema, "res://");
            var file = FileAccess.Open(realPath, FileAccess.ModeFlags.Read);
            var buffer = file.GetBuffer((long)file.GetLength());
            file.Close();
            return buffer;
        }
        return null;
    }
}

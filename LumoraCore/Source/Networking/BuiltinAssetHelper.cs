using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lumora.Core.Networking;

public static class BuiltinAssetHelper
{
    private const string BuiltinSchema = "builtin://";
    private static readonly List<string> BuiltinAssets = new[]
    {
        "Textures/Dot.png",
        "Assets/Models/headset.gltf",
        
        // Default avatar models
        "Assets/Prefabs/defaultavatar.prefab",
        "Assets/Prefabs/defaultavatarhumanoid.prefab",
        "Assets/Models/defaultavatar.glb",
        "Assets/Models/defaultavatarhumanoid.glb",
        "Assets/Models/defaultavatar.meshfile",
        "Assets/Models/defaultavatarhumanoid.meshfile",
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
            // TODO: Implement platform-agnostic asset loading
            // This needs to be implemented by the platform-specific layer
            throw new System.NotImplementedException("Platform-specific asset loading needs to be implemented via IAssetProvider");
        }
        return null;
    }
}

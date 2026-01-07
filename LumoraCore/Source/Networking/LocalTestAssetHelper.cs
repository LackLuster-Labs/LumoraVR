
namespace Lumora.Core.Networking;

public static class LocalTestAssetHelper
{
    private const bool EnableLocalTest = true; //DISABLE THIS IN PROD

    private const string LocalTestSchema = "localtest://";
    public static bool ValidPath(string path)
    {
        if (!EnableLocalTest) return false;
        if (!path.StartsWith(LocalTestSchema)) return false;
        return true;
    }
    public static byte[] GetLocalTestAssetData(string path)
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

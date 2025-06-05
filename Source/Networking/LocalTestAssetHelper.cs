using Godot;

namespace LumoraVR.Source.Networking;

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
            var realPath = path.Replace(LocalTestSchema, "user://");
            var file = FileAccess.Open(realPath, FileAccess.ModeFlags.Read);
            var buffer = file.GetBuffer((long)file.GetLength());
            file.Close();
            return buffer;
        }
        return null;
    }
}

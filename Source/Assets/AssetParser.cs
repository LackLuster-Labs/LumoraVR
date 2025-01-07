using Godot;

namespace Aquamarine.Source.Assets;

public static class AssetParser
{
    public static ImageTexture ParseImage(string path, byte[] data)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".png"))
        {
            var img = new Image();
            img.LoadPngFromBuffer(data);
            return ImageTexture.CreateFromImage(img);
        }
        if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg"))
        {
            var img = new Image();
            img.LoadJpgFromBuffer(data);
            return ImageTexture.CreateFromImage(img);
        }
        if (lower.EndsWith(".tga"))
        {
            var img = new Image();
            img.LoadTgaFromBuffer(data);
            return ImageTexture.CreateFromImage(img);
        }
        if (lower.EndsWith(".ktx"))
        {
            var img = new Image();
            img.LoadKtxFromBuffer(data);
            return ImageTexture.CreateFromImage(img);
        }
        if (lower.EndsWith(".webp"))
        {
            var img = new Image();
            img.LoadWebpFromBuffer(data);
            return ImageTexture.CreateFromImage(img);
        }
        if (lower.EndsWith(".svg"))
        {
            var img = new Image();
            img.LoadSvgFromBuffer(data);
            return ImageTexture.CreateFromImage(img);
        }
        return null;
    }
    public static MeshAsset ParseMesh(byte[] data)
    {
        var meshFile = MeshFile.Deserialize(data);

        if (meshFile.Valid())
        {
            //GD.Print("valid mesh");
            var (mesh, skin) = meshFile.Instantiate();
            return new MeshAsset()
            {
                Mesh = mesh,
                Skin = skin,
            };
        }
        else
        {
            GD.Print("not valid mesh");
        }
        return null;
    }
}

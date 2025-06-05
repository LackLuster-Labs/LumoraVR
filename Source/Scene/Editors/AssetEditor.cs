using System;
using System.IO;
using System.Linq;
using LumoraVR.Source.Assets;
using LumoraVR.Source.Scene.Assets;
using Godot;
using FileAccess = Godot.FileAccess;

namespace LumoraVR.Source.Scene.Editors;

public partial class AssetEditor : PanelContainer
{
    [Export] public PrefabEditor PrefabEditor;
    [ExportGroup("Internal")]
    [Export] public OptionButton AssetTypeOptions;
    [Export] public Button AssetAddButton;
    [Export] public Button AssetImportButton;
    [Export] public Control CurrentAssetContainer;
    [Export] public Control AssetInventoryContainer;

    private FileDialog _fileDialog;

    public override void _Ready()
    {
        base._Ready();

        AssetImportButton.Pressed += AssetImportButtonOnPressed;
        PrefabEditor.HierarchyChanged += HierarchyChanged;

        var allowedAdd = SceneObjectHelpers.StaticAssetTypes.OrderBy(i => (int)i);
        foreach (var item in allowedAdd) AssetTypeOptions.AddItem(item.ToString(), (int)item);

        AssetAddButton.Pressed += AssetAddButtonOnPressed;
    }
    private void AssetAddButtonOnPressed()
    {
        var prefab = PrefabEditor.EditingPrefab;
        var index = (AssetProviderType)AssetTypeOptions.GetSelectedId();
        if (!SceneObjectHelpers.StaticAssetTypes.Contains(index)) return;
        var usedIndices = prefab.Assets.Select(i => i.Key).OrderBy(i => i).ToArray();
        var ind = usedIndices.FirstOrDefault();
        while (usedIndices.Contains(ind)) ind++;
        var child = new PrefabAsset
        {
            Type = index,
        };
        prefab.Assets.Add(ind, child);
        PrefabEditor.HierarchyChanged();
    }
    private void HierarchyChanged()
    {
        GenerateCurrentAssets();
    }
    private void AssetImportButtonOnPressed()
    {
        if (!IsInstanceValid(_fileDialog))
        {
            _fileDialog = new FileDialog();
            AddChild(_fileDialog);
            _fileDialog.FileMode = FileDialog.FileModeEnum.OpenFiles;
            _fileDialog.Access = FileDialog.AccessEnum.Filesystem;
            _fileDialog.UseNativeDialog = false;
            _fileDialog.FilesSelected += FileDialogOnFilesSelected;
        }
        if (!_fileDialog.Visible)
        {
            _fileDialog.PopupCentered(Vector2I.One * 512);
        }
    }
    private void FileDialogOnFilesSelected(string[] paths)
    {
        foreach (var p in paths)
        {
            var file = FileAccess.Open(p, FileAccess.ModeFlags.Read);
            var fileName = Path.GetFileName(p);
            var fileNameMinusExtension = Path.GetFileNameWithoutExtension(p);
            var data = file.GetBuffer((long)file.GetLength());
            file.Close();

            if (fileName.EndsWith(".gltf") || fileName.EndsWith(".glb"))
            {
                var gltfDoc = new GltfDocument();
                var gltfState = new GltfState();
                gltfDoc.AppendFromBuffer(data, "", gltfState);
                var scene = gltfDoc.GenerateScene(gltfState);

                var meshes = scene.FindChildren("*").OfType<MeshInstance3D>().ToList();
                foreach (var m in meshes)
                {
                    if (m.Mesh is not ArrayMesh arrMesh) continue;

                    var name = m.Mesh.ResourceName;
                    var meshFile = MeshFile.FromArrayMesh(arrMesh);
                    var toLocation = FileAccess.Open($"user://{fileNameMinusExtension}.{name}.meshfile", FileAccess.ModeFlags.Write);
                    toLocation.StoreBuffer(meshFile.Serialize());
                    toLocation.Close();
                }
            }
            else
            {
                var toLocation = FileAccess.Open($"user://{fileName}", FileAccess.ModeFlags.Write);
                toLocation.StoreBuffer(data);
                toLocation.Close();
            }
        }
    }
    private void GenerateCurrentAssets()
    {
        foreach (var child in CurrentAssetContainer.GetChildren())
        {
            CurrentAssetContainer.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var asset in PrefabEditor.EditingPrefab.Assets)
        {
            var icon = AssetIcon.Packed.Instantiate<AssetIcon>();
            icon.TypeLabel.Text = asset.Value.Type.ToString();

            if (SceneObjectHelpers.FileAssetTypes.Contains(asset.Value.Type))
            {
                icon.NameLabel.Text = asset.Value.Data["path"].AsString();
            }

            CurrentAssetContainer.AddChild(icon);
        }
    }
}

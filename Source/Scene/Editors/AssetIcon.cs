using Godot;

namespace LumoraVR.Source.Scene.Editors;

public partial class AssetIcon : PanelContainer
{
    [Export] public Label TypeLabel;
    [Export] public Label NameLabel;
    [Export] public TextureRect PreviewImage;
    [Export] public Button RemoveButton;

    public static PackedScene Packed = ResourceLoader.Load<PackedScene>("res://Scenes/Editors/AssetIcon.tscn");
}

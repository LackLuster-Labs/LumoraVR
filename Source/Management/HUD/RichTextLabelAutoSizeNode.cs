using Godot;

namespace LumoraVR.Source.Management.HUD;

/// <summary>
/// Automatically sets up a RichTextLabelAutoSize upon being attached.
/// </summary>
[GlobalClass, Tool]
public partial class RichTextLabelAutoSizeNode : Control
{
    public override void _Ready()
    {
        if (GetChildCount() > 0)
        {
            return;
        }

        SizeFlagsHorizontal = SizeFlags.Fill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var text = new RichTextLabelAutoSize();
        AddChild(text);
        text.Owner = GetTree().EditedSceneRoot;
    }
}

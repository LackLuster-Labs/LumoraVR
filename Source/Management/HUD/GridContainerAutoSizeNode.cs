using Godot;
using System;

namespace LumoraVR.Source.Management.HUD;

/// <summary>
/// Implements the Auto Size text feature.
/// </summary>
public partial class GridContainerAutoSizeNode : GridContainer
{
    [Export] public int CellSize = 256;

    public override void _Ready()
    {
        Resized += OnResized;
    }

    void OnResized()
    {
        Columns = (int)MathF.Round(Size.X / CellSize);
    }
}

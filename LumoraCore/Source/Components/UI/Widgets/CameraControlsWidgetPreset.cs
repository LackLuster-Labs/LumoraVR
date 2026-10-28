// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The camera on and off, how it moves, and what it takes. A mode the world's rules refuse is greyed. The
// mirror puts the camera on the desktop window; on a desktop that is while the dash is shut. In a headset it
// blinks while off, because a headset user streaming without it is streaming their own eyes. -xlinka
[ComponentCategory("Hidden")]
public sealed class CameraControlsWidgetPreset : DirectorWidgetPreset
{
    private const float TitleTop = 10f;
    private const float TitleHeight = 26f;
    private const float StripHeight = 32f;
    private const float ModesTop = 44f;
    private const float ActionsTop = 84f;
    private const double BlinkSeconds = 0.5;

    private static readonly PhotoCameraMode[] Modes =
    {
        PhotoCameraMode.FirstPerson, PhotoCameraMode.ThirdPerson, PhotoCameraMode.Group,
        PhotoCameraMode.Stationary, PhotoCameraMode.Manual,
    };

    private static readonly string[] ModeNames = { "First person", "Third person", "Group", "Stationary", "Manual" };

    private static readonly PhotoShape[] Shapes = { PhotoShape.Flat, PhotoShape.Panorama, PhotoShape.Stereo, PhotoShape.Object };

    private static readonly string[] ShapeNames = { "Photo", "360", "Stereo", "Object" };

    private SettingsChip? _mirror;

    public CameraControlsWidgetPreset()
    {
        MinSize.Value = new float2(420f, 130f);
        PreferredSize.Value = new float2(600f, 146f);
        MaxSize.Value = new float2(1000f, 200f);
    }

    protected override void Build(Widget widget, Slot root)
    {
        var title = Row(root, "Title", TitleTop, TitleHeight, Pad, Pad);
        Label(title, "Text", "Camera", DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, float2.Zero, new float2(-(ToggleSwitch.Width + 8f), 0f), SemiboldFont);
        var power = SettingsUI.Child(title, "Power", new float2(1f, 0f), float2.One, new float2(-ToggleSwitch.Width, 0f), float2.Zero);
        Switch(power, director => director.Active, (director, on) => director.Active = on);

        var modes = Row(root, "Modes", ModesTop, StripHeight, Pad, Pad);
        for (int i = 0; i < Modes.Length; i++)
        {
            var mode = Modes[i];
            Chip(Cell(modes, ModeNames[i], i, Modes.Length), ModeNames[i], director => director.Mode = mode,
                live: _ => CameraRules.ModeAllowed(FocusedWorld, mode),
                lit: director => director.Mode == mode);
        }

        var actions = Row(root, "Actions", ActionsTop, StripHeight, Pad, Pad);
        int count = Shapes.Length + 1;
        _mirror = Chip(Cell(actions, "Mirror", 0, count), "Mirror", director => director.Mirror = !director.Mirror,
            painted: true);
        for (int i = 0; i < Shapes.Length; i++)
        {
            var shape = Shapes[i];
            Chip(Cell(actions, ShapeNames[i], i + 1, count), ShapeNames[i], director => director.TakePhoto(shape),
                live: director => director.Camera != null);
        }

        Settle();
    }

    protected override void Refresh()
    {
        base.Refresh();
        if (_mirror == null)
            return;
        var director = Director;
        if (director == null)
        {
            PaintChip(_mirror, false, false);
        }
        else if (director.Mirror)
        {
            PaintChip(_mirror, true, true);
        }
        else if (!InVR)
        {
            PaintChip(_mirror, false, true);
        }
        else
        {
            bool red = (int)(World.Time.TotalTime / BlinkSeconds) % 2 == 1;
            PaintChip(_mirror, red, true, DashTheme.Negative);
        }
    }
}

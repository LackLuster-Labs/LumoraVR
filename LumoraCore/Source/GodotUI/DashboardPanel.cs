// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI;

/// <summary>
/// Dashboard panel component for the userspace overlay.
/// Displays HomeDash.tscn as a 3D panel that can be toggled with menu button (VR) or Escape (desktop).
/// </summary>
[ComponentCategory("GodotUI")]
public class DashboardPanel : GodotUIPanel
{
    /// <summary>
    /// Whether the dashboard is currently visible.
    /// </summary>
    public readonly Sync<bool> IsVisible;

    /// <summary>
    /// Distance from the user's head to spawn the panel.
    /// </summary>
    public readonly Sync<float> SpawnDistance;

    /// <summary>
    /// Vertical offset from head position (negative = below eye level).
    /// </summary>
    public readonly Sync<float> VerticalOffset;

    protected override string DefaultScenePath => "res://Scenes/UI/Dashboard/HomeDash.tscn";
    protected override float2 DefaultSize => new float2(900, 700);
    protected override float DefaultPixelsPerUnit => 800f;
    protected override int DefaultResolutionScale => 3;

    public override void OnAwake()
    {
        base.OnAwake();

        IsVisible.OnChanged += _ => NotifyChanged();
    }

    public override void OnInit()
    {
        base.OnInit();
        IsVisible.Value = false;
        SpawnDistance.Value = 0.8f;
        VerticalOffset.Value = -0.1f;
    }

    /// <summary>
    /// Toggle the dashboard visibility.
    /// </summary>
    public void Toggle()
    {
        IsVisible.Value = !IsVisible.Value;
    }

    /// <summary>
    /// Show the dashboard.
    /// </summary>
    public void Show()
    {
        IsVisible.Value = true;
    }

    /// <summary>
    /// Hide the dashboard.
    /// </summary>
    public void Hide()
    {
        IsVisible.Value = false;
    }

    public override void HandleButtonPress(string buttonPath)
    {
        // Handle Exit button - hide dashboard instead of destroying
        if (buttonPath.EndsWith("ExitButton") || buttonPath.EndsWith("CloseButton"))
        {
            Hide();
            return;
        }

        base.HandleButtonPress(buttonPath);
    }

    /// <summary>
    /// Override Close to hide instead of destroy (dashboard persists in userspace).
    /// </summary>
    public override void Close()
    {
        Hide();
    }
}

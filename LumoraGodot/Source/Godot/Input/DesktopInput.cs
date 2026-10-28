// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using LumoraLogger = Lumora.Core.Logging.Logger;
using Lumora.Source.UI;
using Lumora.Source.Godot.UI;
using Lumora.Source.Godot.Input;

namespace Lumora.Source.Input;

// Desktop camera-side scene overlay: the screen reticle over the composited dash and the free-cursor
// ray the laser follows while the OS cursor is unlocked. Movement and buttons live on the
// InputInterface drivers; this node is scene-side only.
//
// It used to also cast a RayCast3D against the UI layer every frame and every physics tick and pose
// a pair of idle hands off the result. Nothing read either: the laser does its own hit test in the
// engine and the hands come from the avatar. -xlinka
public partial class DesktopInput : Node3D
{
    private Camera3D _camera = null!;
    private Control _cursorUI = null!;
    private CircleCursor _cursorDot = null!;

    public Camera3D Camera => _camera;

    public override void _Ready()
    {
        CreateCursorUI();
        LumoraLogger.Log("DesktopInput initialized");
    }

    public override void _ExitTree()
    {
        InterfaceSettings.Changed -= ApplyCursorSettings;
        base._ExitTree();
    }

    private void CreateCursorUI()
    {
        var canvasLayer = new CanvasLayer();
        canvasLayer.Name = "DesktopCursorLayer";
        canvasLayer.Layer = 101;
        AddChild(canvasLayer);

        _cursorUI = new Control();
        _cursorUI.Name = "CursorContainer";
        _cursorUI.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _cursorUI.MouseFilter = Control.MouseFilterEnum.Ignore;
        canvasLayer.AddChild(_cursorUI);

        _cursorDot = new CircleCursor();
        _cursorDot.Name = "CursorDot";
        _cursorDot.MouseFilter = Control.MouseFilterEnum.Ignore;
        _cursorUI.AddChild(_cursorDot);

        ApplyCursorSettings();
        InterfaceSettings.Changed += ApplyCursorSettings;
    }

    private void ApplyCursorSettings()
    {
        if (_cursorDot == null) return;

        float size = InterfaceSettings.ReticleSize;
        _cursorDot.Radius = size;
        _cursorDot.Thickness = InterfaceSettings.ReticleThickness;
        _cursorDot.Style = InterfaceSettings.Style;
        _cursorDot.CursorColor = InterfaceSettings.ReticleColor;
        _cursorDot.CustomMinimumSize = new Vector2(size * 2 + 4, size * 2 + 4);
        _cursorDot.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        UpdateCamera();
        UpdateCursorPosition();
        UpdateCursorRay();
        ReportDisplay();
    }

    // The window, where it sits on the desktop, and the screen under it, in the pixels the mouse's desktop
    // position uses, so a script can place the mouse inside the window.
    private static void ReportDisplay()
    {
        var input = Lumora.Core.Engine.Current?.InputInterface;
        if (input == null)
            return;
        var window = DisplayServer.WindowGetSize();
        var position = DisplayServer.WindowGetPosition();
        var screen = DisplayServer.ScreenGetSize();
        input.Display.Report(new Lumora.Core.Math.int2(window.X, window.Y),
            new Lumora.Core.Math.int2(position.X, position.Y),
            new Lumora.Core.Math.int2(screen.X, screen.Y));
    }

    // Push the free-cursor ray and camera projection info to the engine while
    // the OS cursor is unlocked (dash open, or something asked for it free). The
    // interaction laser casts along this ray so the in-world cursor follows the
    // mouse exactly.
    private void UpdateCursorRay()
    {
        var input = Lumora.Core.Engine.Current?.InputInterface;
        if (input == null)
            return;

        var viewport = GetViewport();
        if (_camera != null && viewport != null)
        {
            var size = viewport.GetVisibleRect().Size;
            if (size.Y > 0f)
            {
                var camPos = _camera.GlobalPosition;
                var camRot = _camera.GlobalTransform.Basis.GetRotationQuaternion();
                input.SetDesktopViewInfo(
                    _camera.Fov,
                    size.X / size.Y,
                    new Lumora.Core.Math.float3(camPos.X, camPos.Y, camPos.Z),
                    new Lumora.Core.Math.floatQ(camRot.X, camRot.Y, camRot.Z, camRot.W));
            }
        }

        var camera = _camera;
        if ((!DashboardToggle.IsDashboardVisible && TouchControls.Instance?.PointerActive != true && !input.CursorFree)
            || input.IsVRActive || camera == null || viewport == null)
        {
            input.SetDesktopCursorRay(false, default, default);
            return;
        }

        var mousePos = TouchControls.Instance is { PointerActive: true } touch ? touch.PointerPosition : PointerPosition(viewport, input);
        var origin = camera.ProjectRayOrigin(mousePos);
        var direction = camera.ProjectRayNormal(mousePos);
        input.SetDesktopCursorRay(
            true,
            new Lumora.Core.Math.float3(origin.X, origin.Y, origin.Z),
            new Lumora.Core.Math.float3(direction.X, direction.Y, direction.Z));
    }

    private void UpdateCamera()
    {
        // Track the single rendering camera in every mode. Third-person/free-cam now move THIS camera
        // (via HeadOutput's pose override) instead of swapping in a separate camera, so the dashboard,
        // cursor ray and laser - which all key off this pose - follow the view in all modes. - xlinka
        _camera = Lumora.Source.Godot.Bootstrap.XRModeManager.Instance?.CurrentCamera ?? _camera;
    }

    private void UpdateCursorPosition()
    {
        if (_cursorDot == null)
            return;

        // In the world the laser's in-world cursor is the pointer. Over the composited dash it is
        // not: the overlay draws above the 3D cursor and the OS cursor is not reliably shown while
        // the game window owns the mouse, so the screen reticle takes over there. Its layer (101)
        // sits one above the dash overlay (100). -xlinka
        var input = Lumora.Core.Engine.Current?.InputInterface;
        bool overDash = input is { IsVRActive: false, IsDashboardOpen: true };
        _cursorDot.Visible = overDash && TouchControls.Instance == null;
        if (overDash)
            _cursorDot.Position = PointerPosition(_cursorDot.GetViewport(), input!) - _cursorDot.Size / 2f;
    }

    // Where the window's pointer is: the pad's dash cursor while a pad is steering it, the mouse otherwise.
    private static Vector2 PointerPosition(Viewport viewport, Lumora.Core.Input.InputInterface input)
    {
        var pad = input.PadCursor;
        if (!pad.Active)
            return viewport.GetMousePosition();
        var size = viewport.GetVisibleRect().Size;
        return new Vector2(pad.Position.x * size.X, pad.Position.y * size.Y);
    }

    public void SetCamera(Camera3D camera)
    {
        _camera = camera;
    }
}

// Reticle drawing for the desktop crosshair. Style selection comes from
// InterfaceSettings.Style so the dashboard "reticle" setting takes effect live.
public partial class CircleCursor : Control
{
    public Color CursorColor { get; set; } = new Color(1f, 1f, 1f, 0.5f);
    public float Radius { get; set; } = 12f;
    public float Thickness { get; set; } = 2f;
    public Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle Style { get; set; }
        = Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Ring;

    public override void _Draw()
    {
        var center = Size / 2f;

        switch (Style)
        {
            case Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Off:
                return;

            case Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Dot:
                DrawCircle(center, Mathf.Max(Thickness, Radius * 0.25f), CursorColor);
                break;

            case Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Crosshair:
                float arm = Radius;
                DrawLine(center + new Vector2(-arm, 0f), center + new Vector2(arm, 0f), CursorColor, Thickness, true);
                DrawLine(center + new Vector2(0f, -arm), center + new Vector2(0f, arm), CursorColor, Thickness, true);
                break;

            case Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Ring:
            default:
                DrawArc(center, Radius, 0, Mathf.Tau, 32, CursorColor, Thickness, true);
                break;
        }
    }
}

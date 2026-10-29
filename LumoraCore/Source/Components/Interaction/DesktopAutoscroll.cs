// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Input.Actions;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

// Middle-button scrolling on a screen: middle-click a list on a panel and it scrolls on its own toward
// wherever the cursor goes, faster the further the cursor is from the spot it started on. Click any button
// or press Escape to stop; hold the middle button, drag and let go to scroll only while it is held. On a
// screen there is no depth to lean into and a wheel moves a notch at a time, so a long page is a lot of
// notches; this is the way through one without a single drag.
//
// Only the desktop pointer starts it, and only over something that scrolls; a middle-click anywhere else
// still opens the context menu. While it runs the cursor is free and the view holds still, so moving the
// mouse moves the pointer and nothing else. The click that ends it is eaten: it does not also press the
// button under the cursor, grab, or close the dash. The wheel is untouched throughout. -xlinka
public static class DesktopAutoscroll
{
    // Degrees off the starting spot, as the eye sees it, so a near panel and a far one scroll alike.
    public const float DeadZoneDegrees = 1.5f;
    public const float MaxNotchesPerSecond = 80f;
    private const float LinearRate = 2.2f;
    private const float SquareRate = 0.10f;
    private const float MarkerDegrees = 4f;
    private const string MarkerIcon = "res://Assets/Textures/Icons/move.svg";

    private static readonly object Owner = new();

    private static InteractionLaser? _offerLaser;
    private static ScrollRect? _offerScroll;
    private static Slot? _offerSurface;
    private static float3 _offerPoint;
    private static bool _offerUserspace;

    private static bool _active;
    private static InteractionLaser? _laser;
    private static ScrollRect? _scroll;
    private static Slot? _surface;
    private static float3 _anchor;
    private static bool _holding;
    private static bool _moved;
    private static bool _dashWasOpen;
    private static UserInputState? _heldState;
    private static Slot? _marker;

    public static bool IsActive => _active;

    public static ScrollRect? Target => _active ? _scroll : null;

    // The last speed it scrolled at, in wheel notches a second.
    public static float NotchesPerSecond { get; private set; }

    public static bool IsDrivenBy(InteractionLaser laser) => _active && ReferenceEquals(_laser, laser);

    // How fast a cursor this far off the start scrolls, in notches a second: nothing inside the dead zone, then
    // gently at first and quickly once it is well away.
    public static float SpeedFor(float degrees)
    {
        float past = degrees - DeadZoneDegrees;
        if (!(past > 0f))
            return 0f;
        return MathF.Min(MaxNotchesPerSecond, LinearRate * past + SquareRate * past * past);
    }

    // The desktop pointer's hover each frame, from the laser's own update. Only a hover over something that
    // scrolls is kept; the dash, sitting in front of the world, wins over a world panel behind it.
    internal static void Offer(InteractionLaser laser, bool userspace, ILaserPointerTarget? target, int pointerId, in float3 hitPoint)
    {
        if (_active || target is not ILaserAxisTarget axis)
            return;
        if (_offerLaser != null && _offerUserspace && !userspace)
            return;
        var scroll = axis.ScrollUnderLaser(laser, pointerId);
        if (scroll == null || scroll.IsDestroyed)
            return;
        _offerLaser = laser;
        _offerScroll = scroll;
        _offerSurface = (target as Component)?.Slot;
        _offerPoint = hitPoint;
        _offerUserspace = userspace;
    }

    // A crosshair left still long enough for its beam to go idle hovers nothing, but it still rests on a panel; the
    // list there is found by a plain hit test, so a middle click on it scrolls instead of opening the menu.
    internal static void OfferResting(InteractionLaser laser, Canvas canvas, in float3 origin, in float3 direction, in float3 hitPoint)
    {
        if (_active || _offerLaser != null)
            return;
        var scroll = canvas.ScrollUnderRay(origin, direction);
        if (scroll == null || scroll.IsDestroyed)
            return;
        _offerLaser = laser;
        _offerScroll = scroll;
        _offerSurface = canvas.Slot;
        _offerPoint = hitPoint;
        _offerUserspace = false;
    }

    // Once a frame, after the actions are read and before any world updates, so whatever a click here starts
    // or ends is out of the actions before a tool or a panel looks at them.
    public static void Step(InputInterface input, float deltaTime)
    {
        var offerLaser = _offerLaser;
        var offerScroll = _offerScroll;
        var offerSurface = _offerSurface;
        var offerPoint = _offerPoint;
        _offerLaser = null;
        _offerScroll = null;
        _offerSurface = null;
        _offerUserspace = false;

        var mouse = input?.Mouse;
        var map = input?.Actions;
        if (input == null || mouse == null || map == null || input.IsVRActive)
        {
            if (_active)
                End(input);
            return;
        }

        if (!_active)
        {
            if (!mouse.MiddleButton.Pressed || offerLaser is not { IsDestroyed: false } || offerScroll is not { IsDestroyed: false }
                || offerSurface is not { IsDestroyed: false } || !offerScroll.CanInteract || TextInput.Focused != null)
                return;
            map.Swallow(ControlRef.Mouse(InputControlCatalog.MouseMiddle), deltaTime);
            Begin(input, offerLaser, offerScroll, offerSurface, offerPoint);
            return;
        }

        bool left = mouse.LeftButton.Pressed;
        bool right = mouse.RightButton.Pressed;
        bool middle = mouse.MiddleButton.Pressed;
        bool escape = input.Keyboard?.IsKeyJustPressed(Key.Escape) == true;
        if (left || right || middle || escape)
        {
            if (left) map.Swallow(ControlRef.Mouse(InputControlCatalog.MouseLeft), deltaTime);
            if (right) map.Swallow(ControlRef.Mouse(InputControlCatalog.MouseRight), deltaTime);
            if (middle) map.Swallow(ControlRef.Mouse(InputControlCatalog.MouseMiddle), deltaTime);
            if (escape) map.Swallow(ControlRef.Key(Key.Escape), deltaTime);
            End(input);
            return;
        }

        // Held, dragged off the start and let go: that was a scroll for as long as it was held.
        if (_holding && !mouse.MiddleButton.Held)
        {
            _holding = false;
            if (_moved)
            {
                End(input);
                return;
            }
        }

        if (_laser is not { IsDestroyed: false } || _scroll is not { IsDestroyed: false } scroll || !scroll.CanInteract
            || _surface is not { IsDestroyed: false } || input.IsDashboardOpen != _dashWasOpen || TextInput.Focused != null)
            End(input);
    }

    private static void Begin(InputInterface input, InteractionLaser laser, ScrollRect scroll, Slot surface, in float3 anchor)
    {
        _active = true;
        _laser = laser;
        _scroll = scroll;
        _surface = surface;
        _anchor = anchor;
        _holding = true;
        _moved = false;
        _dashWasOpen = input.IsDashboardOpen;
        NotchesPerSecond = 0f;

        // The cursor goes loose where the crosshair was, and the view stops following the mouse, so the mouse
        // only ever moves the pointer.
        input.SetCursorFree(Owner, true);
        var state = UserInputState.ForFocusedLocalUser;
        _heldState = state;
        if (state != null && !input.IsDashboardOpen)
        {
            if (input.DesktopExternalCameraAim)
                state.SetDesktopInputSuppressed(Owner, true);
            else
                state.SetViewHeld(Owner, true);
        }
    }

    public static void End(InputInterface? input = null)
    {
        if (!_active)
            return;
        _active = false;
        NotchesPerSecond = 0f;
        (input ?? Engine.Current?.InputInterface)?.SetCursorFree(Owner, false);
        var state = _heldState;
        _heldState = null;
        state?.SetDesktopInputSuppressed(Owner, false);
        state?.SetViewHeld(Owner, false);

        var marker = _marker;
        _marker = null;
        if (marker is { IsDestroyed: false } && marker.World is { } world)
            world.RunSynchronously(() => { if (!marker.IsDestroyed) marker.Destroy(); });
        // Stops where it is, not a few notches further on where the last frame was heading.
        if (_scroll is { IsDestroyed: false } scroll)
            scroll.StopScrolling();
        _laser = null;
        _scroll = null;
        _surface = null;
    }

    // From the driving laser's own update, inside its world: where the pointer is now against where it started,
    // turned into notches for this frame on the scroll it started on.
    internal static void Drive(InteractionLaser laser, float delta, in float3 origin, in float3 direction)
    {
        if (!IsDrivenBy(laser))
            return;
        var scroll = _scroll;
        var surface = _surface;
        if (scroll is not { IsDestroyed: false } || surface is not { IsDestroyed: false })
            return;

        var rotation = surface.GlobalRotation;
        float3 normal = rotation * float3.Forward;
        ShowMarker(laser.World, origin, rotation);

        float facing = float3.Dot(direction, normal);
        if (MathF.Abs(facing) < 1e-4f)
            return;
        float along = float3.Dot(_anchor - origin, normal) / facing;
        if (!(along > 0f))
            return;
        float3 point = origin + direction * along;

        float3 toAnchor = _anchor - origin;
        float3 toPoint = point - origin;
        float lengths = toAnchor.Length * toPoint.Length;
        if (lengths < 1e-8f)
            return;
        float cos = System.Math.Clamp(float3.Dot(toAnchor, toPoint) / lengths, -1f, 1f);
        float degrees = MathF.Acos(cos) * (180f / MathF.PI);
        if (degrees > DeadZoneDegrees)
            _moved = true;

        float speed = SpeedFor(degrees);
        NotchesPerSecond = speed;
        if (speed <= 0f || !(delta > 0f))
            return;
        float3 offset = point - _anchor;
        // Read from its back (a surface turned to face the other way), the panel's own right is the viewer's left.
        float3 right = rotation * float3.Right;
        if (float3.Dot(normal, origin - _anchor) < 0f)
            right = -right;
        float x = float3.Dot(offset, right);
        float y = float3.Dot(offset, rotation * float3.Up);
        float length = MathF.Sqrt(x * x + y * y);
        if (length < 1e-6f)
            return;
        float step = speed * delta / length;
        scroll.ProcessAxis(default, new float2(x * step, y * step));
    }

    // Four arrows where it started, on the panel, a fixed size to the eye. Local to this machine. Out in the room
    // only: on the dash its panels drew over the mark whatever draw band it was given, so it showed only through
    // the gaps between rows. TODO - xlinka: a mark on the dash wants to live in the dash's own canvas.
    private static void ShowMarker(World? world, in float3 eye, in floatQ rotation)
    {
        if (world?.RootSlot == null || world.IsUserspace)
            return;
        var marker = _marker;
        if (marker == null || marker.IsDestroyed)
        {
            marker = world.RootSlot.AddLocalSlot("Autoscroll Anchor");
            marker.Persistent.Value = false;
            var picture = marker.AttachComponent<ImageProvider>();
            picture.GenerateMipmaps.Value = false;
            picture.URL.Value = new Uri(MarkerIcon);
            var quad = marker.AttachComponent<QuadMesh>();
            quad.Size.Value = float2.One;
            var material = marker.AttachComponent<OverlayUnlitMaterial>();
            material.BlendMode.Value = BlendMode.Alpha;
            material.Culling.Value = Culling.None;
            material.Texture.Target = picture;
            // Drawn the same whether or not the panel's own surface is in front of it: it is a mark ON the panel.
            var ink = new colorHDR(UI.DashTheme.Accent.r, UI.DashTheme.Accent.g, UI.DashTheme.Accent.b, 1f);
            material.FrontTintColor.Value = ink;
            material.BehindTintColor.Value = ink;
            var renderer = marker.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = quad;
            renderer.Material.Target = material;
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            _marker = marker;
        }
        float distance = MathF.Max(0.05f, (_anchor - eye).Length);
        float size = 2f * distance * MathF.Tan(MarkerDegrees * 0.5f * MathF.PI / 180f);
        float3 toEye = eye - _anchor;
        marker.GlobalPosition = _anchor + (toEye.LengthSquared > 1e-8f ? toEye.Normalized : rotation * float3.Forward) * MathF.Min(0.01f, distance * 0.25f);
        marker.GlobalRotation = rotation;
        marker.GlobalScale = float3.One * size;
    }
}

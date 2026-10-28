// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// Window controls for a world panel: pin to a user, lock in place, hide from scene cameras, and a frame
// colour for telling a pile of panels apart. The pin parents under the user's ROOT, not the head, so the
// panel rides locomotion without swinging across the view every time the head turns. -xlinka
[ComponentCategory("UI")]
public sealed class PanelWorkspace : Component
{
    public readonly SyncRef<User> PinnedUser = new();
    public readonly SyncRef<Slot> OriginalParent = new();
    public readonly Sync<bool> PositionLocked = new();
    public readonly Sync<bool> HideFromCamera = new();
    public readonly Sync<int> ColorIndex = new();

    public static readonly color[] Colors =
    {
        DashTheme.IconBlue, DashTheme.Warning, DashTheme.Positive,
        DashTheme.Accent, DashTheme.IconPink, DashTheme.IconTeal,
    };

    // The header and edge from before the frame colour went on, while it is on.
    private (color Header, color Border)? _tintedFrom;

    public color? FrameColor => ColorIndex.Value >= 1 && ColorIndex.Value <= Colors.Length ? Colors[ColorIndex.Value - 1] : null;

    public override void OnStart() { base.OnStart(); Keep(); }
    public override void OnChanges() { base.OnChanges(); Keep(); }

    // What a change from elsewhere leaves to redo is redone by the authority, which keeps every panel up, the
    // one in somebody's hand or pinned to them included. Every peer used to, and on a panel somebody else held
    // the others' writes were refused, the hidden-from-cameras layer among them. Whoever presses a control
    // still applies it on the spot. -xlinka
    private void Keep()
    {
        if (World?.IsAuthority != true)
            return;
        using var keep = World.DataModelPermissions?.EnterUiUpkeep(this);
        Apply();
    }

    // Every write is equality-gated: agreeing peers write nothing and the synced shell fields never
    // ping-pong. -xlinka
    private void Apply()
    {
        var shell = Slot.GetComponent<PanelShell>();
        if (shell == null)
            return;
        Set(shell.AllowGrab, !PositionLocked.Value);

        var layer = Slot.GetComponent<RenderLayerOverride>();
        if (HideFromCamera.Value && layer == null)
            layer = Slot.AttachComponent<RenderLayerOverride>();
        // The hook only restamps nodes when its layer changes, so going back to visible is a write to the
        // public layer rather than a destroy that would leave every existing mesh on the eyes-only bit.
        if (layer != null)
            Set(layer.Layer, HideFromCamera.Value ? RenderLayerOverride.EyesOnlyLayer : 1);

        // The frame colour lays a tint over the shell's own header and edge and takes it off again; without one
        // the shell's colours are the panel's own, a player's recolour included, and are left alone. The edge
        // still wearing one of the frame colours is a tint somebody else's machine laid. -xlinka
        var tint = FrameColor;
        bool laidElsewhere = _tintedFrom == null && System.Array.IndexOf(Colors, shell.BorderColor.Value) >= 0;
        if (tint == null && _tintedFrom == null && !laidElsewhere)
            return;
        // Under a tint this machine did not lay, the shell's own colours are gone; the theme's stand in for them.
        var theme = Slot.GetComponent<UITheme>();
        var (header, border) = _tintedFrom ?? (laidElsewhere
            ? (theme?.Header.Value ?? shell.HeaderColor.Value, theme?.Border.Value ?? shell.BorderColor.Value)
            : (shell.HeaderColor.Value, shell.BorderColor.Value));
        if (tint is { } frame)
        {
            _tintedFrom ??= (header, border);
            header = Mix(header, frame, 0.3f);
            border = frame;
        }
        else
        {
            _tintedFrom = null;
        }
        Set(shell.HeaderColor, header);
        Set(shell.BorderColor, border);
    }

    public void TogglePin(User? user)
    {
        if (PinnedUser.Target != null)
        {
            var parent = OriginalParent.Target;
            Slot.SetParent(parent is { IsDestroyed: false } && !parent.IsDescendantOf(Slot) ? parent : World.RootSlot, true);
            PinnedUser.Target = null!;
            OriginalParent.Target = null!;
        }
        else if (user?.Root?.Slot is { IsDestroyed: false } root && root.World == World && !root.IsDescendantOf(Slot))
        {
            PositionLocked.Value = false;
            OriginalParent.Target = Slot.Parent;
            Slot.SetParent(root, true);
            PinnedUser.Target = user;
        }
        Apply();
    }

    public void TogglePositionLock()
    {
        PositionLocked.Value = !PositionLocked.Value;
        if (PositionLocked.Value)
        {
            var grab = Slot.GetComponent<Grabbable>();
            if (grab?.Grabber != null) grab.Grabber.Release(grab);
            if (PinnedUser.Target != null || Slot.Parent != World.RootSlot)
                Slot.SetParent(World.RootSlot, true);
            PinnedUser.Target = null!;
            OriginalParent.Target = null!;
        }
        Apply();
    }

    public void CycleColor()
    {
        ColorIndex.Value = (ColorIndex.Value + 1) % (Colors.Length + 1);
        Apply();
    }

    private static color Mix(in color a, in color b, float t)
        => new(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);

    private static void Set<T>(Sync<T> field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field.Value, value))
            field.Value = value;
    }
}

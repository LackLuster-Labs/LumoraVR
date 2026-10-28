// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Input.Actions;

using Cat = InputControlCatalog;

// The shipped binding table.
//
// This reproduces what the engine did before there were actions, control for control, so nobody who
// updates has to relearn anything: desktop primary is still left click, tool secondary is still R,
// the context menu is still middle click OR T, held objects still ride the wheel with shift to
// scale, VR is still trigger/grip/secondary per hand. Anything that reads differently from the old
// hardcoded version is a bug in this file, not a design decision.
//
// VR face buttons: A/X is the dash, B/Y is the tool secondary, which is also what summons the
// context menu when no tool claims it. A controller with no face buttons folds both onto its one
// menu button, hold for the dash and tap for the menu, through the charge on the controller. The
// stick click is jump, and doubles as the scale modifier for a laser-held object on that hand.
//
// The gamepad column is laid out for a pad alone on a screen, a Steam Deck first: the left stick walks
// and the right stick looks, the way every first-person game on a pad does. A and B are the keyboard's
// Space and C (jump and crouch, up and down in flight), L3 runs until you stop, R3 hops back. The
// triggers are the hand (RT uses, LT grabs), RB is the tool's secondary, Y the radial menu, X holds a
// carried object still to turn it with the right stick and LB with the d-pad resizes it. The d-pad's
// up and down push and pull a carried object or scroll what the laser rests on, left and right walk through the
// number-key tools. Menu opens the dash and View the chat. With the dash open the left stick is a
// cursor, A clicks, B backs out and the right stick scrolls; those live in the Menu set, which shuts
// walking off while the dash is up, so A there never jumps.
//
// Desktop input only ever drives the RIGHT hand - there is one mouse - so the left-hand set gets VR
// bindings only. -xlinka
public static class InputBindingDefaults
{
    // Where the backstep went when G became the chat key. The binding store moves old saved overrides here.
    public const Key BlinkBackstepKey = Key.B;
    public const Key OldBlinkBackstepKey = Key.G;

    private static readonly ControlRef LeftCtrl = ControlRef.Key(Key.LeftControl);
    private static readonly ControlRef RightCtrl = ControlRef.Key(Key.RightControl);
    private static readonly ControlRef LeftAlt = ControlRef.Key(Key.LeftAlt);
    private static readonly ControlRef RightAlt = ControlRef.Key(Key.RightAlt);
    private static readonly ControlRef LeftShift = ControlRef.Key(Key.LeftShift);
    private static readonly ControlRef RightShift = ControlRef.Key(Key.RightShift);

    // The free camera's down key. It was ctrl until ctrl became the camera's chord key; the binding store moves
    // saved overrides off ctrl onto this.
    public const Key FlyDownKey = Key.C;

    public static void Apply(InputBindingMap map)
    {
        foreach (var action in map.AllActions())
        {
            action.ClearBindings();
            ApplyTo(map, action);
        }
    }

    // Assumes the action's list was already cleared.
    public static void ApplyTo(InputBindingMap map, InputAction action)
    {
        switch (action.Set?.Name)
        {
            case "Locomotion":
                ApplyLocomotion(map, action);
                break;
            case "Interaction.Left":
                ApplyInteraction(map, action, Chirality.Left);
                break;
            case "Interaction.Right":
                ApplyInteraction(map, action, Chirality.Right);
                break;
            case "Menu":
                ApplyMenu(map, action);
                break;
            case "Camera":
                ApplyCamera(map, action);
                break;
            case "Editing":
                ApplyEditing(map, action);
                break;
        }

        foreach (var binding in action.Bindings)
            binding.IsDefault = true;
    }

    private static void ApplyLocomotion(InputBindingMap map, InputAction action)
    {
        var loco = map.Locomotion;

        if (action == loco.Move)
        {
            BindWasd(action);
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRStick));
            action.Bind(ControlRef.Pad(Cat.PadLeftStick));
        }
        else if (action == loco.Turn)
        {
            // A headset's turn only. On a screen the pad's right stick is the view (Look), and a snap turn on
            // the same stick would jerk the camera round a step on every hard push.
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRStick), BindingAxis.X);
        }
        else if (action == loco.Look)
        {
            action.Bind(ControlRef.Pad(Cat.PadRightStick));
        }
        else if (action == loco.Fly)
        {
            action.Bind(ControlRef.Key(Key.Space), scale: 1f);
            action.Bind(ControlRef.Key(Key.C), scale: -1f);
            // The right stick's spare axis: turn owns X, so Y is free for altitude.
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRStick), BindingAxis.Y);
            // The pad's Space and C, same as the keys: jump climbs and crouch sinks.
            action.Bind(ControlRef.Pad(Cat.PadFaceDown), scale: 1f);
            action.Bind(ControlRef.Pad(Cat.PadFaceRight), scale: -1f);
            // Matches the old helper: anything under a fifth of a stick's travel is noise, and the
            // remaining range is NOT rescaled so a light push still means a slow climb.
            loco.Fly.Deadzone = 0.2f;
            loco.Fly.RescaleDeadzone = false;
        }
        else if (action == loco.Jump)
        {
            action.Bind(ControlRef.Key(Key.Space));
            // Stick click, not the face buttons: A/X on both hands both jumping left nothing for the
            // context menu and equip actions to sit on. -xlinka
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRStickPress));
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRStickPress));
            action.Bind(ControlRef.Pad(Cat.PadFaceDown));
        }
        else if (action == loco.Sprint)
        {
            action.Bind(ControlRef.Key(Key.LeftShift));
            action.Bind(ControlRef.Key(Key.RightShift));
        }
        else if (action == loco.SprintToggle)
        {
            // A thumb on the stick cannot also hold its click down, so the pad's sprint is a click that lasts
            // until the stick comes back to rest. Shift stays a hold.
            action.Bind(ControlRef.Pad(Cat.PadLeftStickPress));
        }
        else if (action == loco.Crouch)
        {
            // C, not ctrl: ctrl is the desktop resize modifier and cannot double as crouch.
            action.Bind(ControlRef.Key(Key.C));
            action.Bind(ControlRef.Pad(Cat.PadFaceRight));
        }
        else if (action == loco.SlowWalk)
        {
            // Plain Z only: with ctrl held the undo shortcut owns the letter.
            action.Bind(ControlRef.Key(Key.Z));
        }
        else if (action == loco.ToggleMouseCapture)
        {
            action.Bind(ControlRef.Key(Key.Escape));
        }
        else if (action == loco.Respawn)
        {
            // A chord, so it owns R while ctrl is down and never also fires the tool's secondary on R.
            BindChord(action, ControlRef.Key(Key.R), LeftCtrl, RightCtrl);
        }
        else if (action == loco.ScaleUser)
        {
            action.Bind(new InputBinding(ControlRef.Mouse(Cat.MouseWheel), modifiers: new[] { LeftCtrl }));
            action.Bind(new InputBinding(ControlRef.Mouse(Cat.MouseWheel), modifiers: new[] { RightCtrl }));
        }
        else if (action == loco.LeftStick)
        {
            // Per-hand raw sticks stay VR-only. A pad's left stick already means "walk", and letting
            // it also read as a hand tilt would make strafing turn you as well.
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRStick));
        }
        else if (action == loco.RightStick)
        {
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRStick));
        }
        else if (action == loco.BlinkAim)
        {
            action.Bind(ControlRef.Key(Key.T));
            // LB is also the carry's resize modifier. A carrying hand holds it for itself, so a resize never
            // aims an arc and lets go into a hop.
            action.Bind(ControlRef.Pad(Cat.PadLeftShoulder));
        }
        else if (action == loco.BlinkBackstep)
        {
            // B, not G: G opens the world's chat.
            action.Bind(ControlRef.Key(BlinkBackstepKey));
            action.Bind(ControlRef.Pad(Cat.PadRightStickPress));
        }
    }

    private static void ApplyInteraction(InputBindingMap map, InputAction action, Chirality side)
    {
        var hand = map.Interaction(side);
        // One mouse, one keyboard: the desktop hand is the right one. The left hand is VR-only.
        bool desktopHand = side == Chirality.Right;

        if (action == hand.Primary)
        {
            action.Bind(ControlRef.VR(side, Cat.VRTrigger));
            if (desktopHand)
            {
                action.Bind(ControlRef.Mouse(Cat.MouseLeft));
                action.Bind(ControlRef.Pad(Cat.PadRightTrigger));
            }
        }
        else if (action == hand.Grab)
        {
            action.Bind(ControlRef.VR(side, Cat.VRGrip));
            if (desktopHand)
            {
                action.Bind(ControlRef.Mouse(Cat.MouseRight));
                action.Bind(ControlRef.Pad(Cat.PadLeftTrigger));
            }
        }
        else if (action == hand.Secondary)
        {
            action.Bind(ControlRef.VR(side, Cat.VRSecondary));
            action.Bind(ControlRef.VR(side, Cat.VRMenuTap));
            if (desktopHand)
            {
                action.Bind(ControlRef.Key(Key.R));
                action.Bind(ControlRef.Pad(Cat.PadRightShoulder));
            }
        }
        else if (action == hand.Select)
        {
            action.Bind(ControlRef.VR(side, Cat.VRSelectPress));
        }
        else if (action == hand.ContextMenu)
        {
            // Middle click and T each toggle on their own edge, which is why both are here rather
            // than one being a modifier of the other.
            if (desktopHand)
            {
                action.Bind(ControlRef.Mouse(Cat.MouseMiddle));
                action.Bind(ControlRef.Key(Key.T));
                action.Bind(ControlRef.Pad(Cat.PadFaceUp));
            }
        }
        else if (action == hand.HoldFreeze)
        {
            if (desktopHand)
            {
                action.Bind(ControlRef.Key(Key.E));
                action.Bind(ControlRef.Pad(Cat.PadFaceLeft));
            }
        }
        else if (action == hand.HoldModifier)
        {
            // Stick click, not B/Y: secondary opens the context menu even mid-carry now, so B plus
            // stick was popping the menu instead of scaling. The click reads as the trackpad click on
            // a wand, so one binding covers every profile. Jump shares the click and is gated off in
            // the reader while a hand carries on its laser in VR. -xlinka
            action.Bind(ControlRef.VR(side, Cat.VRStickPress));
            if (desktopHand)
            {
                action.Bind(ControlRef.Key(Key.LeftShift));
                action.Bind(ControlRef.Key(Key.RightShift));
                action.Bind(ControlRef.Pad(Cat.PadLeftShoulder));
            }
        }
        else if (action == hand.HoldScroll)
        {
            if (desktopHand)
                action.Bind(ControlRef.Mouse(Cat.MouseWheel));
        }
        else if (action == hand.HoldLook)
        {
            if (desktopHand)
                action.Bind(ControlRef.Mouse(Cat.MouseDelta));
        }
        else if (action == hand.HoldAxis)
        {
            // A pace, not notches: the wheel's distance control moves a share of the distance per notch,
            // which a held d-pad would deliver every frame. So the pad pushes and pulls here, at the
            // headset stick's steady rate, and resizes with the modifier the same way.
            action.Bind(ControlRef.VR(side, Cat.VRStick));
            if (desktopHand)
            {
                action.Bind(ControlRef.Pad(Cat.PadDPadUp), target: BindingAxis.Y, scale: 1f);
                action.Bind(ControlRef.Pad(Cat.PadDPadDown), target: BindingAxis.Y, scale: -1f);
            }
        }
        else if (action == hand.Pointer)
        {
            action.Bind(ControlRef.VR(side, Cat.VRStick));
            if (desktopHand)
            {
                action.Bind(ControlRef.Mouse(Cat.MouseWheel), target: BindingAxis.Y);
                action.Bind(ControlRef.Pad(Cat.PadDPadUp), target: BindingAxis.Y, scale: 1f);
                action.Bind(ControlRef.Pad(Cat.PadDPadDown), target: BindingAxis.Y, scale: -1f);
            }
        }
        else if (action == hand.Stick)
        {
            action.Bind(ControlRef.VR(side, Cat.VRStick));
            action.Bind(ControlRef.Pad(side == Chirality.Left ? Cat.PadLeftStick : Cat.PadRightStick));
        }
        else if (action == hand.NextTool)
        {
            // The number row has no pad, so the d-pad steps through the same tools in the same order.
            if (desktopHand)
                action.Bind(ControlRef.Pad(Cat.PadDPadRight));
        }
        else if (action == hand.PreviousTool)
        {
            if (desktopHand)
                action.Bind(ControlRef.Pad(Cat.PadDPadLeft));
        }
    }

    private static void ApplyMenu(InputBindingMap map, InputAction action)
    {
        if (action == map.Menu.ToggleDashboard)
        {
            action.Bind(ControlRef.Key(Key.Escape));
            action.Bind(ControlRef.Pad(Cat.PadStart));
            // Not the raw menu button: on a controller with no face buttons a menu press is a tap or
            // a hold, decided on release or at full charge, and a raw edge would pop the dash before
            // the two could be told apart. -xlinka
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRPrimary));
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRPrimary));
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRMenuHold));
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRMenuHold));
        }
        else if (action == map.Menu.OpenChat)
        {
            // A screen thing: a headset has no box to open. A Deck has no G, so View opens it there.
            action.Bind(ControlRef.Key(Key.G));
            action.Bind(ControlRef.Pad(Cat.PadBack));
        }
        else if (action == map.Menu.Click)
            action.Bind(ControlRef.Pad(Cat.PadFaceDown));
        else if (action == map.Menu.Back)
            action.Bind(ControlRef.Pad(Cat.PadFaceRight));
        else if (action == map.Menu.Point)
            action.Bind(ControlRef.Pad(Cat.PadLeftStick));
        else if (action == map.Menu.Scroll)
            action.Bind(ControlRef.Pad(Cat.PadRightStick));
    }

    private static void ApplyCamera(InputBindingMap map, InputAction action)
    {
        var camera = map.Camera;

        if (action == camera.ThirdPerson)
            action.Bind(ControlRef.Key(Key.F5));
        else if (action == camera.FreeCam)
            action.Bind(ControlRef.Key(Key.F6));
        else if (action == camera.FlyMove)
        {
            BindWasd(action);
            action.Bind(ControlRef.Pad(Cat.PadLeftStick));
        }
        else if (action == camera.FlyVertical)
        {
            // C sinks, as it crouches on foot: ctrl is the chord key for the camera's drag and focus now, and a
            // held ctrl sinking the camera under the floor while you look round was the old trap.
            action.Bind(ControlRef.Key(Key.Space), scale: 1f);
            action.Bind(ControlRef.Key(FlyDownKey), scale: -1f);
            action.Bind(ControlRef.Pad(Cat.PadFaceDown), scale: 1f);
            action.Bind(ControlRef.Pad(Cat.PadFaceRight), scale: -1f);
        }
        else if (action == camera.FlyFast)
        {
            action.Bind(ControlRef.Key(Key.LeftShift));
            action.Bind(ControlRef.Key(Key.RightShift));
            action.Bind(ControlRef.Pad(Cat.PadLeftStickPress));
        }
        else if (action == camera.OrbitZoom)
            action.Bind(ControlRef.Mouse(Cat.MouseWheel));
        // The photo keys are chords too, so shift + F12 is the timer alone and ctrl or shift + P never reaches
        // anything on a plain P. -xlinka
        else if (action == camera.TakePhoto)
        {
            action.Bind(ControlRef.Key(Key.F12));
            BindChord(action, ControlRef.Key(Key.P), LeftCtrl, RightCtrl);
        }
        else if (action == camera.TimedPhoto)
        {
            BindChord(action, ControlRef.Key(Key.F12), LeftShift, RightShift);
            BindChord(action, ControlRef.Key(Key.P), LeftShift, RightShift);
        }
        // The view keys are chords on ctrl and alt, and a chord owns its control while it is held (see
        // InputBindingMap.Evaluate), so ctrl + click focuses a panel without also pressing what is under the
        // pointer, and ctrl + right drags the camera without also grabbing.
        else if (action == camera.Focus)
            BindChord(action, ControlRef.Key(Key.F), LeftCtrl, RightCtrl);
        else if (action == camera.Unfocus)
            BindChord(action, ControlRef.Key(Key.F), LeftAlt, RightAlt);
        else if (action == camera.FocusPanel)
            BindChord(action, ControlRef.Mouse(Cat.MouseLeft), LeftCtrl, RightCtrl);
        else if (action == camera.Drag)
            BindChord(action, ControlRef.Mouse(Cat.MouseRight), LeftCtrl, RightCtrl);
        else if (action == camera.Orbit)
            BindChord(action, ControlRef.Mouse(Cat.MouseRight), LeftAlt, RightAlt);
        else if (action == camera.Zoom)
            BindChord(action, ControlRef.Mouse(Cat.MouseWheel), LeftCtrl, RightCtrl);
    }

    private static void ApplyEditing(InputBindingMap map, InputAction action)
    {
        if (action == map.Editing.Paste || action == map.Editing.Copy)
        {
            var key = ControlRef.Key(action == map.Editing.Paste ? Key.V : Key.C);
            action.Bind(new InputBinding(key, modifiers: new[] { LeftCtrl }));
            action.Bind(new InputBinding(key, modifiers: new[] { RightCtrl }));
        }
        else if (action == map.Editing.Undo || action == map.Editing.Redo)
        {
            var key = ControlRef.Key(action == map.Editing.Undo ? Key.Z : Key.Y);
            action.Bind(new InputBinding(key, modifiers: new[] { LeftCtrl }));
            action.Bind(new InputBinding(key, modifiers: new[] { RightCtrl }));
        }
    }

    private static void BindChord(InputAction action, ControlRef control, ControlRef left, ControlRef right)
    {
        action.Bind(new InputBinding(control, modifiers: new[] { left }));
        action.Bind(new InputBinding(control, modifiers: new[] { right }));
    }

    private static void BindWasd(InputAction action)
    {
        action.Bind(ControlRef.Key(Key.W), target: BindingAxis.Y, scale: 1f);
        action.Bind(ControlRef.Key(Key.S), target: BindingAxis.Y, scale: -1f);
        action.Bind(ControlRef.Key(Key.A), target: BindingAxis.X, scale: -1f);
        action.Bind(ControlRef.Key(Key.D), target: BindingAxis.X, scale: 1f);
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Input.Actions;

// Typed handles onto one set's actions. Call sites go through these, so an action rename is a
// compile error rather than a string that silently stops matching anything.
public abstract class InputActionGroup
{
    public InputActionSet Set { get; }

    protected InputActionGroup(InputActionSet set)
    {
        Set = set;
    }

    protected DigitalAction Digital(string name, string label, string description = "")
    {
        var action = Set.Add(new DigitalAction(name, label));
        action.Description = description;
        return action;
    }

    protected AnalogAction Analog(string name, string label, string description = "")
    {
        var action = Set.Add(new AnalogAction(name, label));
        action.Description = description;
        return action;
    }

    protected Analog2DAction Analog2D(string name, string label, string description = "")
    {
        var action = Set.Add(new Analog2DAction(name, label));
        action.Description = description;
        return action;
    }
}

// Moving the body: walk, fly, turn, jump, and the blink module's aim/backstep.
public sealed class LocomotionActions : InputActionGroup
{
    public readonly Analog2DAction Move;
    public readonly AnalogAction Turn;
    public readonly Analog2DAction Look;
    public readonly AnalogAction Fly;
    public readonly DigitalAction Jump;
    public readonly DigitalAction Sprint;
    public readonly DigitalAction SprintToggle;
    public readonly DigitalAction Crouch;
    public readonly DigitalAction SlowWalk;
    public readonly DigitalAction ToggleMouseCapture;
    public readonly AnalogAction ScaleUser;
    public readonly Analog2DAction LeftStick;
    public readonly Analog2DAction RightStick;
    public readonly DigitalAction BlinkAim;
    public readonly DigitalAction BlinkBackstep;
    public readonly DigitalAction Respawn;

    public LocomotionActions(InputActionSet set) : base(set)
    {
        Move = Analog2D("Move", "Move", "Walk and strafe.");
        Turn = Analog("Turn", "Turn", "Snap or smooth turn in a headset. A screen turns with the mouse or Look.");
        Look = Analog2D("Look", "Look", "Turns and tilts the view on a screen, like the mouse does.");
        Fly = Analog("Fly", "Fly Up / Down", "Vertical axis for noclip flight.");
        Jump = Digital("Jump", "Jump");
        Sprint = Digital("Sprint", "Sprint");
        SprintToggle = Digital("SprintToggle", "Sprint (Click)", "Runs until you stop moving or crouch.");
        Crouch = Digital("Crouch", "Crouch");
        SlowWalk = Digital("SlowWalk", "Slow Walk", "Toggles walking at a quarter of the pace. Sprint turns it off.");
        ToggleMouseCapture = Digital("ToggleMouseCapture", "Release Mouse", "Frees the cursor from mouse look.");
        ScaleUser = Analog("ScaleUser", "Resize Self", "Grow or shrink yourself.");

        // The two sticks verbatim, before any of the composites above touch them. Blink reads these
        // because it wants each hand's raw tilt with its own hysteresis, not a merged movement axis.
        LeftStick = Analog2D("LeftStick", "Left Stick (raw)", "Raw left-hand VR stick, used by aim gestures.");
        RightStick = Analog2D("RightStick", "Right Stick (raw)", "Raw right-hand VR stick, used by aim gestures.");

        BlinkAim = Digital("BlinkAim", "Blink Aim", "Hold to aim the blink arc (desktop and pad).");
        BlinkBackstep = Digital("BlinkBackstep", "Blink Backstep", "Short hop backwards.");
        Respawn = Digital("Respawn", "Respawn", "A fresh body at a spawn point in the world in focus.");

        // The pad's thumbsticks wear and rest a little off centre; the driver's floor takes the worst of it and
        // this is the look's own ring past it, cut radially so a diagonal keeps its angle.
        Look.Deadzone = 0.05f;
    }
}

// One hand's tools: click, grab, tool secondary, context menu, and held-object handling.
public sealed class InteractionActions : InputActionGroup
{
    public Chirality Side => Set.Side;

    public readonly DigitalAction Primary;
    public readonly DigitalAction Grab;
    public readonly DigitalAction Secondary;
    public readonly DigitalAction Select;
    public readonly DigitalAction ContextMenu;
    public readonly DigitalAction HoldFreeze;
    public readonly DigitalAction HoldModifier;
    public readonly AnalogAction HoldScroll;
    public readonly Analog2DAction HoldLook;
    public readonly Analog2DAction HoldAxis;
    public readonly Analog2DAction Pointer;
    public readonly Analog2DAction Stick;
    public readonly DigitalAction NextTool;
    public readonly DigitalAction PreviousTool;

    public InteractionActions(InputActionSet set) : base(set)
    {
        Primary = Digital("Primary", "Use / Click");
        Grab = Digital("Grab", "Grab");
        Secondary = Digital("Secondary", "Tool Secondary");
        Select = Digital("Select", "Select with Gizmo", "Select or deselect the object under the DevTip.");
        ContextMenu = Digital("ContextMenu", "Context Menu");
        HoldFreeze = Digital("HoldFreeze", "Manipulate Held", "Hold to aim a carried object instead of looking.");
        HoldModifier = Digital("HoldModifier", "Scale Modifier", "Held with distance input, resizes instead of moving.");
        HoldScroll = Analog("HoldScroll", "Held Distance", "Push a carried object nearer or further.");
        HoldLook = Analog2D("HoldLook", "Held Rotation", "Pointer motion applied to a carried object.");
        HoldAxis = Analog2D("HoldAxis", "Held Axis", "Stick axis for distance (Y) and twist (X).");
        Pointer = Analog2D("Pointer", "Pointer Scroll", "Scrolls whatever the laser is pointing at.");
        Stick = Analog2D("Stick", "Menu Flick", "Deflect toward a context menu action, release to take it.");
        NextTool = Digital("NextTool", "Next Tool", "Takes out the next of the number-key tools, and puts it away after the last.");
        PreviousTool = Digital("PreviousTool", "Previous Tool", "Takes out the number-key tool before this one.");

        // Distance/rotation on the same hand read the same wheel and the same stick on purpose - what
        // they mean depends on the modifier - so a radial cut is wrong here.
        HoldAxis.PerAxisDeadzone = true;
        HoldAxis.Deadzone = 0.15f;
        Pointer.PerAxisDeadzone = true;
        Pointer.Deadzone = 0.20f;
        HoldLook.ClampToUnit = false;
    }
}

// The dashboard and the world's chat. Live above locomotion, so opening the dash stops the body dead.
public sealed class MenuActions : InputActionGroup
{
    public readonly DigitalAction ToggleDashboard;
    public readonly DigitalAction OpenChat;
    public readonly DigitalAction Click;
    public readonly DigitalAction Back;
    public readonly Analog2DAction Point;
    public readonly Analog2DAction Scroll;

    public MenuActions(InputActionSet set) : base(set)
    {
        ToggleDashboard = Digital("ToggleDashboard", "Dashboard", "Open or close the dashboard.");
        OpenChat = Digital("OpenChat", "Open Chat", "Type into the world's chat on a screen. Enter sends, Escape closes.");

        // A pad on the dash: the cursor, a click, a way back, and a scroll. The mouse and keyboard already
        // have all four, so these are the pad's column only.
        Click = Digital("Click", "Dash Click", "Clicks what the dash cursor is on. In a text field, brings the on-screen keyboard back.");
        Back = Digital("Back", "Back", "Backs out of a dash screen, then closes the dash; closes a text field or the radial menu.");
        Point = Analog2D("Point", "Dash Cursor", "Moves the cursor over the dash.");
        Scroll = Analog2D("Scroll", "Dash Scroll", "Scrolls the dash under the cursor.");
        Point.Deadzone = 0.05f;
        Scroll.Deadzone = 0.15f;
    }
}

// Desktop camera modes and the free-cam flight controls.
public sealed class CameraActions : InputActionGroup
{
    public readonly DigitalAction ThirdPerson;
    public readonly DigitalAction FreeCam;
    public readonly Analog2DAction FlyMove;
    public readonly AnalogAction FlyVertical;
    public readonly DigitalAction FlyFast;
    public readonly AnalogAction OrbitZoom;
    public readonly DigitalAction TakePhoto;
    public readonly DigitalAction TimedPhoto;
    public readonly DigitalAction Focus;
    public readonly DigitalAction Unfocus;
    public readonly DigitalAction FocusPanel;
    public readonly DigitalAction Drag;
    public readonly DigitalAction Orbit;
    public readonly AnalogAction Zoom;

    public CameraActions(InputActionSet set) : base(set)
    {
        ThirdPerson = Digital("ThirdPerson", "Third Person", "Toggle the orbit camera.");
        FreeCam = Digital("FreeCam", "Free Camera", "Toggle detached flying camera.");
        FlyMove = Analog2D("FlyMove", "Camera Move", "Free-cam horizontal flight.");
        FlyVertical = Analog("FlyVertical", "Camera Up / Down", "Free-cam vertical flight.");
        FlyFast = Digital("FlyFast", "Camera Boost", "Hold for fast free-cam flight.");
        OrbitZoom = Analog("OrbitZoom", "Orbit Zoom", "Third-person camera distance.");
        TakePhoto = Digital("TakePhoto", "Photo", "Hold to frame a photo of what the screen shows, let go to take it.");
        TimedPhoto = Digital("TimedPhoto", "Timed Photo", "Hold to frame a photo, let go to start its timer.");
        Focus = Digital("Focus", "Focus", "Fly the free camera to the object under the pointer and keep it in frame.");
        Unfocus = Digital("Unfocus", "Unfocus", "Let the framed object go, and leave the free camera if Focus opened it.");
        FocusPanel = Digital("FocusPanel", "Focus Panel", "Square the view on the panel under the pointer. Anywhere else to leave.");
        Drag = Digital("Drag", "Camera Drag", "Hold to look with the free camera, pan a focused panel, or move the third-person shoulder.");
        Orbit = Digital("Orbit", "Camera Orbit", "Hold to swing the free camera round what it is focused on.");
        Zoom = Analog("Zoom", "Camera Zoom", "Nearer to or further from a focused panel, or the free camera's focus.");
    }
}

// World editing shortcuts that are not a tool press.
public sealed class EditingActions : InputActionGroup
{
    public readonly DigitalAction Paste;
    public readonly DigitalAction Copy;
    public readonly DigitalAction Undo;
    public readonly DigitalAction Redo;

    public EditingActions(InputActionSet set) : base(set)
    {
        Paste = Digital("Paste", "Paste", "Import whatever is on the clipboard into the world.");
        Copy = Digital("Copy", "Copy", "Copy a held or selected object to the clipboard.");
        Undo = Digital("Undo", "Undo", "Undo the last edit; hold Shift to redo.");
        Redo = Digital("Redo", "Redo", "Redo the last undone edit.");
    }
}

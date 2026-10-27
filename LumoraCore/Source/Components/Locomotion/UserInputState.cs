// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Core.Components;

// Per-local-user input state. Holds the four flags the desktop/VR camera
// and locomotion subsystems used to push around as statics on
// LocomotionController. Attached to the local user's root slot when
// LocomotionController initializes; never spawned on remote users.
//
// Desktop-side scene nodes (drivers, camera controllers) read via the
// `ForFocusedLocalUser` accessor; engine-side components that have a Slot
// should cache the instance from their owning user's root.
// - xlinka
[ComponentCategory("Users")]
public class UserInputState : Component
{
    // Mouse capture for desktop look. The desktop locomotion module raises
    // this when active, and tools or freecam can flip it back to false to
    // release the cursor for UI.
    public bool MouseCaptureRequested { get; private set; }

    public void SetMouseCaptureRequested(bool value)
    {
        MouseCaptureRequested = value;
    }

    // F6 free-cam. Locks the character in place while the camera flies free.
    public bool FreeCamActive { get; private set; }

    public void SetFreeCamActive(bool value)
    {
        FreeCamActive = value;
    }

    // F5 third-person. Character keeps moving but mouse drives orbit
    // instead of head look. The camera controller's flag alone; anything else
    // that wants the view still asks with SetViewHeld.
    public bool MouseLookSuppressed { get; private set; }

    public void SetMouseLookSuppressed(bool value)
    {
        MouseLookSuppressed = value;
    }

    // True while a non-first-person camera (third-person orbit, free-cam) is
    // rendering. First-person-only visual overrides (head hiding) read this
    // so the avatar is fully visible from external views.
    public bool ExternalCameraActive { get; private set; }

    public void SetExternalCameraActive(bool value)
    {
        ExternalCameraActive = value;
    }

    // Multi-requester suppression. Tools, dialogs, etc add themselves while
    // they need exclusive mouse/keyboard control and remove themselves when
    // done. As long as the set is non-empty, desktop look/movement is gated.
    private readonly object _suppressionLock = new();
    private readonly HashSet<object> _suppressionRequests = new();

    public bool DesktopInputSuppressed
    {
        get
        {
            lock (_suppressionLock)
                return _suppressionRequests.Count > 0;
        }
    }

    // Who is holding input down right now, for a log line.
    public string DescribeHolds()
    {
        var parts = new List<string>();
        if (FreeCamActive)
            parts.Add("free camera");
        lock (_suppressionLock)
        {
            foreach (var requester in _suppressionRequests)
            {
                parts.Add(requester switch
                {
                    UI.ContextMenuSystem menu => $"context menu ({(menu.IsDestroyed ? "destroyed" : menu.IsShown ? "up" : "not up")})",
                    Component component => $"{component.GetType().Name} on '{component.Slot?.SlotName.Value}'{(component.IsDestroyed ? " (destroyed)" : "")}",
                    _ => requester.GetType().Name,
                });
            }
        }
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    public void SetDesktopInputSuppressed(object requester, bool value)
    {
        if (requester == null)
            return;

        lock (_suppressionLock)
        {
            if (value)
                _suppressionRequests.Add(requester);
            else
                _suppressionRequests.Remove(requester);
        }
    }

    // The narrow half of the suppression above: TRANSLATION is off, everything else stays live.
    //
    // A seat used to borrow the whole desktop channel, which is the channel a modal dialog takes when it
    // wants the mouse and the keyboard. Sitting down therefore killed mouse look, snap turn and blink along
    // with walking, and a seated user could not even turn their head. Movement gets its own requester set:
    // the locomotion modules read both, the look and turn paths read only the desktop one, so a seat stops
    // you walking away and nothing else. -xlinka
    private readonly object _movementLock = new();
    private readonly HashSet<object> _movementRequests = new();

    public bool MovementSuppressed
    {
        get
        {
            lock (_movementLock)
                return _movementRequests.Count > 0;
        }
    }

    // Whoever is holding movement down, so a module can negotiate with it rather than just giving up -
    // blink stands a seated user up and then hops. Null when nothing is holding.
    public object? MovementSuppressedBy
    {
        get
        {
            lock (_movementLock)
            {
                foreach (var requester in _movementRequests)
                    return requester;
                return null;
            }
        }
    }

    // Whether a requester of this kind is among the holders, whoever came first. A seat parks the
    // character, and anything resuming it has to know the chair still has the user.
    public bool IsMovementSuppressedBy<T>() where T : Component
    {
        lock (_movementLock)
        {
            foreach (var requester in _movementRequests)
            {
                if (requester is T holder && !holder.IsDestroyed)
                    return true;
            }
            return false;
        }
    }

    public void SetMovementSuppressed(object requester, bool value)
    {
        if (requester == null)
            return;

        lock (_movementLock)
        {
            if (value)
                _movementRequests.Add(requester);
            else
                _movementRequests.Remove(requester);
        }
    }

    // The view's own hold: the camera stays where it is and look input turns nothing, but the body still
    // walks. Raised by whatever points with the loose cursor (the context menu, the chat box), in every
    // desktop view, since moving the view would move the aim under the cursor; taking the whole desktop
    // channel for that stopped the walk with it. -xlinka
    private readonly object _viewLock = new();
    private readonly HashSet<object> _viewRequests = new();

    public bool ViewHeld
    {
        get
        {
            lock (_viewLock)
                return _viewRequests.Count > 0;
        }
    }

    public void SetViewHeld(object requester, bool value)
    {
        if (requester == null)
            return;

        lock (_viewLock)
        {
            if (value)
                _viewRequests.Add(requester);
            else
                _viewRequests.Remove(requester);
        }
    }

    // Same multi-requester shape as the suppression set: raised by a hand tool while it is carrying something
    // on its laser, where the wheel pushes that object nearer or further. The third-person orbit reads it and
    // leaves the wheel alone, so reeling a held object in doesn't zoom the camera on the same notch. -xlinka
    private readonly object _wheelCaptureLock = new();
    private readonly HashSet<object> _wheelCaptureRequests = new();

    public bool ScrollWheelCaptured
    {
        get
        {
            lock (_wheelCaptureLock)
                return _wheelCaptureRequests.Count > 0;
        }
    }

    public void SetScrollWheelCaptured(object requester, bool value)
    {
        if (requester == null)
            return;

        lock (_wheelCaptureLock)
        {
            if (value)
                _wheelCaptureRequests.Add(requester);
            else
                _wheelCaptureRequests.Remove(requester);
        }
    }

    // The VR twin of the wheel capture: the stick click is the scale modifier while a hand carries on
    // its laser, and jump sits on the same click. Raised by the carrying hand tool in VR only; the
    // jump reader returns rest while it is up so starting a scale does not hop the user. -xlinka
    private readonly object _stickPressCaptureLock = new();
    private readonly HashSet<object> _stickPressCaptureRequests = new();

    public bool StickPressCaptured
    {
        get
        {
            lock (_stickPressCaptureLock)
                return _stickPressCaptureRequests.Count > 0;
        }
    }

    public void SetStickPressCaptured(object requester, bool value)
    {
        if (requester == null)
            return;

        lock (_stickPressCaptureLock)
        {
            if (value)
                _stickPressCaptureRequests.Add(requester);
            else
                _stickPressCaptureRequests.Remove(requester);
        }
    }

    // The primary trigger belongs to whoever holds this, not to the lasers. Body calibration confirms on a
    // pull of both triggers while the beams point sideways at whatever is beside the user; the hand tools
    // read it and press nothing in the world, except through a context menu the hand has open. -xlinka
    private readonly object _triggerCaptureLock = new();
    private readonly HashSet<object> _triggerCaptureRequests = new();

    public bool TriggerCaptured
    {
        get
        {
            lock (_triggerCaptureLock)
                return _triggerCaptureRequests.Count > 0;
        }
    }

    public void SetTriggerCaptured(object requester, bool value)
    {
        if (requester == null)
            return;

        lock (_triggerCaptureLock)
        {
            if (value)
                _triggerCaptureRequests.Add(requester);
            else
                _triggerCaptureRequests.Remove(requester);
        }
    }

    public override void OnDestroy()
    {
        lock (_suppressionLock)
            _suppressionRequests.Clear();
        lock (_viewLock)
            _viewRequests.Clear();
        lock (_movementLock)
            _movementRequests.Clear();
        lock (_wheelCaptureLock)
            _wheelCaptureRequests.Clear();
        lock (_stickPressCaptureLock)
            _stickPressCaptureRequests.Clear();
        lock (_triggerCaptureLock)
            _triggerCaptureRequests.Clear();

        MouseCaptureRequested = false;
        FreeCamActive = false;
        MouseLookSuppressed = false;
        ExternalCameraActive = false;

        base.OnDestroy();
    }

    // Convenience accessors for code that doesn't hold a Slot

    public static UserInputState ForFocusedLocalUser
    {
        get
        {
            var root = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
            return (root?.Slot?.GetComponent<UserInputState>()) ?? null!;
        }
    }

    public static bool FocusedMouseCaptureRequested => ForFocusedLocalUser?.MouseCaptureRequested ?? false;
    public static bool FocusedFreeCamActive => ForFocusedLocalUser?.FreeCamActive ?? false;
    public static bool FocusedMouseLookSuppressed => ForFocusedLocalUser?.MouseLookSuppressed ?? false;
    public static bool FocusedDesktopInputSuppressed => ForFocusedLocalUser?.DesktopInputSuppressed ?? false;
    public static bool FocusedViewHeld => ForFocusedLocalUser?.ViewHeld ?? false;
    public static bool FocusedExternalCameraActive => ForFocusedLocalUser?.ExternalCameraActive ?? false;
    public static bool FocusedScrollWheelCaptured => ForFocusedLocalUser?.ScrollWheelCaptured ?? false;
    public static bool FocusedStickPressCaptured => ForFocusedLocalUser?.StickPressCaptured ?? false;
}

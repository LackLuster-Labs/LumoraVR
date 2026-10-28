// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core.Input.Actions;

// Every action the local user has, the bindings behind them, and the once-per-frame pass that turns
// hardware into action values.
//
// One of these belongs to the local user, owned by the InputInterface. It is a plain runtime object,
// not a datamodel component: bindings are a property of the person sitting at the machine, not of
// the world, and nothing about them should replicate or end up in a save.
//
// The pass runs BEFORE input receivers so anything reading an action inside an update sees this
// frame's hardware, not last frame's. -xlinka
public sealed class InputBindingMap
{
    // Bumped when the shipped defaults change shape enough that stored overrides are stale.
    // 2: G opens the chat, and the backstep's keyboard key moved to B.
    // 3: the pad column laid out for a pad alone on a screen (Look on the right stick, triggers are the hand).
    // 4: ctrl and alt became the camera's chord keys, and the free camera sinks on C instead of ctrl.
    public const int MapVersion = 4;

    // Interaction sits ABOVE the menu: the dash is aimed at with the same laser the world is, so
    // blocking interaction while the menu is up would leave the menu unclickable.
    private const int PriorityLocomotion = 0;
    private const int PriorityMenu = 100;
    private const int PriorityEditing = 130;
    private const int PriorityCamera = 140;
    private const int PriorityInteraction = 150;

    private readonly List<InputActionSet> _sets = new();
    private readonly Dictionary<string, InputAction> _actionsByPath = new(StringComparer.Ordinal);

    // A control held for one set: every set below it reads that control as untouched. A hold lasts until
    // the pass after the one it was taken in, so whoever wants it keeps it by asking every frame and it
    // lapses on its own when they stop. The laser holds its stick while it rests on something the stick
    // scrolls, which keeps turning and walking off that stick there. -xlinka
    private readonly Dictionary<ControlRef, (int Priority, long Pass)> _holds = new();
    private long _pass;

    // Controls a chord is using this pass: its modifiers are down and so is its own control.
    private readonly HashSet<ControlRef> _chordControls = new();

    // Carrying hands reserve controls before locomotion actions are evaluated. World updates may
    // run less often than input, so a live grab cannot rely on last update's one-pass hold.
    internal event Action? BeforeEvaluate;

    private readonly KeyboardSource _keyboard;
    private readonly MouseSource _mouse;
    private readonly GamepadSource _gamepad;
    private readonly VRControllerSource _vrLeft;
    private readonly VRControllerSource _vrRight;

    public LocomotionActions Locomotion { get; }
    public InteractionActions Left { get; }
    public InteractionActions Right { get; }
    public MenuActions Menu { get; }
    public CameraActions Camera { get; }
    public EditingActions Editing { get; }

    public IReadOnlyList<InputActionSet> Sets => _sets;

    // Raised while a focused text field is eating keystrokes.
    public bool TextFocusHeld
    {
        get => _keyboard.TextFocusHeld;
        set => _keyboard.TextFocusHeld = value;
    }

    // For the controls screen's sanity readout.
    public ControlRef LastActivatedControl { get; private set; }
    public InputBinding? LastActivatedBinding { get; private set; }

    // Whose names a hint on screen uses: the keyboard's or the pad's. It follows the last control pressed,
    // with two exceptions on a Deck, where Steam turns the right trackpad into a mouse and its on-screen
    // keyboard into key presses: a mouse button there never counts, and neither does a key that does not type
    // (a back grip sending F5) or anything typed while Steam's keyboard is up. -xlinka
    public InputDeviceKind HintDevice { get; private set; } = SteamDeckPlatform.IsSteamDeck ? InputDeviceKind.Gamepad : InputDeviceKind.Keyboard;

    // Raised by the platform while Steam's on-screen keyboard is showing.
    public bool SteamKeyboardShown { get; set; }

    // False in headless or before init.
    public bool HasEvaluated { get; private set; }

    public InputBindingMap(InputInterface input)
    {
        _keyboard = new KeyboardSource(input);
        _mouse = new MouseSource(input);
        _gamepad = new GamepadSource(input);
        _vrLeft = new VRControllerSource(input, Chirality.Left);
        _vrRight = new VRControllerSource(input, Chirality.Right);

        Locomotion = new LocomotionActions(CreateSet("Locomotion", "Locomotion", PriorityLocomotion, Chirality.None, blocksLower: false));
        Menu = new MenuActions(CreateSet("Menu", "Menu", PriorityMenu, Chirality.None, blocksLower: true));
        Editing = new EditingActions(CreateSet("Editing", "Editing", PriorityEditing, Chirality.None, blocksLower: false));
        Camera = new CameraActions(CreateSet("Camera", "Camera", PriorityCamera, Chirality.None, blocksLower: false));
        Left = new InteractionActions(CreateSet("Interaction.Left", "Left Hand", PriorityInteraction, Chirality.Left, blocksLower: false));
        Right = new InteractionActions(CreateSet("Interaction.Right", "Right Hand", PriorityInteraction, Chirality.Right, blocksLower: false));

        IndexActions();
        InputBindingDefaults.Apply(this);
    }

    private InputActionSet CreateSet(string name, string label, int priority, Chirality side, bool blocksLower)
    {
        var set = new InputActionSet(name, label, priority, side, blocksLower);
        _sets.Add(set);
        return set;
    }

    private void IndexActions()
    {
        _actionsByPath.Clear();
        foreach (var set in _sets)
        {
            foreach (var action in set.Actions)
                _actionsByPath[action.Path] = action;
        }
    }

    // Right is the fallback, which is where desktop input lands.
    public InteractionActions Interaction(Chirality side) => side == Chirality.Left ? Left : Right;

    public InputAction? Find(string path)
        => path != null && _actionsByPath.TryGetValue(path, out var action) ? action : null;

    public IEnumerable<InputAction> AllActions()
    {
        foreach (var set in _sets)
        {
            foreach (var action in set.Actions)
                yield return action;
        }
    }

    private readonly DigitalAction _heldProbe = new("HeldProbe", "Held Probe");

    // Existing carries can outlive a modal input gate. Sample their bound controls without advancing
    // the real action or allowing a new press through that gate. -xlinka
    internal bool ReadHeldIgnoringGates(DigitalAction action)
    {
        _heldProbe.PressPoint = action.PressPoint;
        _heldProbe.Begin();
        foreach (var binding in action.Bindings)
        {
            if (ModifiersHeld(binding) && Resolve(binding.Control) is { IsAvailable: true } source)
                _heldProbe.Accumulate(binding, source);
        }
        _heldProbe.Finish(0f);
        return _heldProbe.Held;
    }

    // Null when that hardware is not present.
    public IInputSource? Resolve(in ControlRef control) => control.Device switch
    {
        InputDeviceKind.Keyboard => _keyboard,
        InputDeviceKind.Mouse => _mouse,
        InputDeviceKind.Gamepad => _gamepad,
        InputDeviceKind.VRController => control.Side == Chirality.Left ? _vrLeft : _vrRight,
        _ => null
    };

    public bool IsDeviceAvailable(InputDeviceKind device) => device switch
    {
        InputDeviceKind.Keyboard => _keyboard.IsAvailable,
        InputDeviceKind.Mouse => _mouse.IsAvailable,
        InputDeviceKind.Gamepad => _gamepad.IsAvailable,
        InputDeviceKind.VRController => _vrLeft.IsAvailable || _vrRight.IsAvailable,
        _ => false
    };

    // GATING

    // Called by the InputInterface just before evaluation, with the current suppression state.
    public void SetGate(InputActionSet set, bool open)
    {
        if (set != null)
            set.GateOpen = open;
    }

    // EVALUATION

    // Sets that are gated off get released rather than skipped, so a button held when the gate
    // closed reports its release instead of staying stuck down forever.
    public void Evaluate(float deltaTime)
    {
        HasEvaluated = true;
        _pass++;
        BeforeEvaluate?.Invoke();

        // A chord owns its control while it is made, in every set: Ctrl+C must not also crouch, ctrl + click
        // focusing a panel must not also press the button under the pointer, ctrl + wheel must not also
        // scroll. A plain binding of that control reads it as untouched for the pass. -xlinka
        // Indexed, not foreach: these are read-only list interfaces, and every action's enumerator was a box a frame.
        _chordControls.Clear();
        foreach (var set in _sets)
        {
            if (!set.IsLive)
                continue;
            var setActions = set.Actions;
            for (int a = 0; a < setActions.Count; a++)
            {
                var actionBindings = setActions[a].Bindings;
                for (int b = 0; b < actionBindings.Count; b++)
                {
                    var binding = actionBindings[b];
                    if (binding.Modifiers.Count > 0 && ModifiersHeld(binding)
                        && Resolve(binding.Control) is { IsAvailable: true } source
                        && (source.ReadDigital(binding.Control) || source.ReadAnalog(binding.Control) != 0f))
                        _chordControls.Add(binding.Control);
                }
            }
        }

        // A set that asserts itself shuts out everything below it. That is the whole of "the menu
        // blocks locomotion" - no flags passed down, no module checking whether a panel is up.
        int blockBelow = int.MinValue;
        foreach (var set in _sets)
        {
            if (set.BlocksLower && set.Enabled && set.Asserting && set.Priority > blockBelow)
                blockBelow = set.Priority;
        }

        bool capturing = UpdateCapture(deltaTime);
        HoldMenuButtonsWhileTyping();

        foreach (var set in _sets)
        {
            bool live = set.IsLive && set.Priority >= blockBelow && !capturing;
            var actions = set.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (!live || !action.Enabled)
                {
                    action.Release(deltaTime);
                    continue;
                }
                EvaluateAction(action, deltaTime);
            }
            if (ReferenceEquals(set, Menu.Set))
                RelayDashClick();
        }

        TrackLastActivated();
    }

    // The pad's click on the dash is the desktop hand's primary, the same press the mouse makes there, so the
    // dash's own rule about where a press may begin applies to it unchanged. The menu set evaluates before the
    // hands, so the press lands this pass. Only while the dash is up: A is jump everywhere else.
    private void RelayDashClick()
    {
        if (Menu.Set.Asserting && Menu.Set.IsLive && Menu.Click.Held)
            Right.Primary.ExternalInput = true;
    }

    // A text field on a screen takes the pad's click and back for itself (bring Steam's keyboard back, close
    // the field), so walking a set below does not jump or crouch on the same press. Kept past the field
    // closing until the buttons come up, or the B that closed it would crouch the moment it did.
    private bool _typingHold;

    private void HoldMenuButtonsWhileTyping()
    {
        if (TextFocusHeld)
            _typingHold = true;
        else if (_typingHold && !AnyPadControlHeld(Menu.Click) && !AnyPadControlHeld(Menu.Back))
            _typingHold = false;
        if (!_typingHold)
            return;
        HoldPadControls(Menu.Click);
        HoldPadControls(Menu.Back);
    }

    private bool AnyPadControlHeld(InputAction action)
    {
        foreach (var binding in action.Bindings)
        {
            if (binding.Control.Device == InputDeviceKind.Gamepad && _gamepad.ReadDigital(binding.Control))
                return true;
        }
        return false;
    }

    private void HoldPadControls(InputAction action)
    {
        foreach (var binding in action.Bindings)
        {
            if (binding.Control.Device == InputDeviceKind.Gamepad)
                HoldControl(action.Set, binding.Control);
        }
    }

    private void EvaluateAction(InputAction action, float deltaTime)
    {
        action.Begin();
        var bindings = action.Bindings;
        for (int i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            var source = Resolve(binding.Control);
            if (source == null || !source.IsAvailable)
                continue;
            if (!ModifiersHeld(binding))
                continue;
            if (binding.Modifiers.Count == 0 && _chordControls.Count > 0 && _chordControls.Contains(binding.Control))
                continue;
            if (HeldAbove(binding.Control, action.Set.Priority))
                continue;
            action.Accumulate(binding, source);
        }
        action.Finish(deltaTime);
    }

    // Something outside the map took this press for itself after the pass already read it: every action bound
    // to the control reads released from here on, and stays released until the control comes up. Chords that
    // use it count too, so a modifier held with it cannot leak the press through.
    public void Swallow(in ControlRef control, float deltaTime)
    {
        foreach (var set in _sets)
        {
            var actions = set.Actions;
            for (int a = 0; a < actions.Count; a++)
            {
                var bindings = actions[a].Bindings;
                for (int b = 0; b < bindings.Count; b++)
                {
                    if (bindings[b].Control == control)
                    {
                        actions[a].Release(deltaTime);
                        break;
                    }
                }
            }
        }
    }

    public void HoldControl(InputActionSet owner, in ControlRef control)
    {
        if (owner != null)
            _holds[control] = (owner.Priority, _pass);
    }

    private bool HeldAbove(in ControlRef control, int priority)
    {
        if (_holds.Count == 0 || !_holds.TryGetValue(control, out var hold))
            return false;
        if (hold.Pass < _pass - 1)
        {
            _holds.Remove(control);
            return false;
        }
        return hold.Priority > priority;
    }

    private bool ModifiersHeld(InputBinding binding)
    {
        var modifiers = binding.Modifiers;
        bool ctrl = false, alt = false;
        for (int i = 0; i < modifiers.Count; i++)
        {
            ctrl |= modifiers[i] == ControlRef.Key(Key.LeftControl) || modifiers[i] == ControlRef.Key(Key.RightControl);
            alt |= modifiers[i] == ControlRef.Key(Key.LeftAlt) || modifiers[i] == ControlRef.Key(Key.RightAlt);
            var source = Resolve(modifiers[i]);
            if (source == null || !source.IsAvailable || !source.ReadDigital(modifiers[i]))
                return false;
        }
        // AltGr is Ctrl+Alt on some Windows and Linux layouts. It types characters, rather than
        // invoking Ctrl-only shortcuts. An explicitly bound Ctrl+Alt chord remains available.
        if (ctrl && !alt && (_keyboard.ReadDigital(ControlRef.Key(Key.LeftAlt)) || _keyboard.ReadDigital(ControlRef.Key(Key.RightAlt))))
            return false;
        return true;
    }

    private void TrackLastActivated()
    {
        if (_mouse.TryCaptureControl(out var control) || _gamepad.TryCaptureControl(out control)
            || _vrRight.TryCaptureControl(out control) || _vrLeft.TryCaptureControl(out control)
            || _keyboard.TryCaptureControl(out control))
        {
            LastActivatedBinding = _keyboard.CaptureChord(control);
            LastActivatedControl = LastActivatedBinding.Control;
            NoteHintDevice(control);
        }
    }

    private void NoteHintDevice(in ControlRef control)
    {
        switch (control.Device)
        {
            case InputDeviceKind.Gamepad:
            case InputDeviceKind.VRController:
                HintDevice = control.Device;
                break;
            case InputDeviceKind.Mouse:
                if (!SteamDeckPlatform.IsSteamDeck)
                    HintDevice = InputDeviceKind.Keyboard;
                break;
            case InputDeviceKind.Keyboard:
                if (SteamKeyboardShown)
                    break;
                if (SteamDeckPlatform.IsSteamDeck && !TypesText(control))
                    break;
                HintDevice = InputDeviceKind.Keyboard;
                break;
        }
    }

    private static bool TypesText(in ControlRef control)
    {
        if (!InputControlCatalog.TryParseKey(control.Control, out var key))
            return false;
        return key is >= Key.A and <= Key.Z or >= Key.Alpha0 and <= Key.Alpha9 or Key.Space or Key.Return;
    }

    // REBIND LISTENING

    public enum CaptureState
    {
        Idle,
        Listening,
        Captured,
        Cancelled
    }

    public CaptureState CaptureStatus { get; private set; } = CaptureState.Idle;
    public InputAction? CaptureTarget { get; private set; }

    // It is a list because the controls screen presents keyboard and mouse as one column - they are
    // one desk - so a rebind there has to accept whichever of the two the user reaches for.
    public IReadOnlyList<InputDeviceKind> CaptureDevices { get; private set; } = Array.Empty<InputDeviceKind>();

    // Actions in the same set that already use the control just captured.
    public IReadOnlyList<InputAction> CaptureConflicts { get; private set; } = Array.Empty<InputAction>();

    // Raised when a capture finishes, so the screen can repaint and persist.
    public event Action<InputAction?>? BindingsChanged;

    // Listening gates every set off, which is right while somebody is genuinely mid-rebind and very
    // wrong if the screen that started it went away without cancelling. Nothing should be able to
    // leave a client unable to move; if no control arrives in this long, the listener gives up on
    // its own. -xlinka
    private const float CaptureTimeout = 8f;
    private float _captureAge;
    private InputBinding? _captureChord;
    private InputBinding? _captureRelease;
    private readonly List<InputBinding> _captureBindings = new();

    public string CapturePrompt
    {
        get
        {
            if (_captureChord != null)
                return "Release " + _captureChord.Describe();
            if (CaptureTarget == null)
                return string.Empty;
            bool keyboard = false;
            foreach (var device in CaptureDevices)
                keyboard |= device == InputDeviceKind.Keyboard;
            if (!keyboard)
                return "Press a control";
            if (CaptureTarget.Kind == InputActionKind.Analog2D)
            {
                string direction = _captureBindings.Count switch
                {
                    0 => CaptureTarget == Locomotion.Move || CaptureTarget == Camera.FlyMove ? "Forward" : "Up",
                    1 => CaptureTarget == Locomotion.Move || CaptureTarget == Camera.FlyMove ? "Backward" : "Down",
                    2 => "Left",
                    _ => "Right"
                };
                return $"Press {direction} ({_captureBindings.Count + 1}/4)";
            }
            if (CaptureTarget.Kind == InputActionKind.Analog)
            {
                bool positive = _captureBindings.Count == 0;
                string direction = CaptureTarget == Locomotion.Turn ? (positive ? "Right" : "Left")
                    : CaptureTarget == Locomotion.Fly || CaptureTarget == Camera.FlyVertical ? (positive ? "Up" : "Down")
                    : positive ? "Positive" : "Negative";
                return $"Press {direction} ({_captureBindings.Count + 1}/2)";
            }
            return "Press and release a key or chord";
        }
    }

    private void ResetCaptureInput()
    {
        _captureChord = null;
        _captureRelease = null;
        _captureBindings.Clear();
    }

    // Every set is gated off while listening, so the key you press to rebind does not also fire
    // whatever it is currently bound to.
    public void BeginCapture(InputAction action, params InputDeviceKind[] devices)
    {
        if (action == null || !action.Rebindable || devices == null || devices.Length == 0)
            return;
        CaptureTarget = action;
        CaptureDevices = devices;
        CaptureStatus = CaptureState.Listening;
        CaptureConflicts = Array.Empty<InputAction>();
        _captureAge = 0f;
        ResetCaptureInput();
    }

    public void CancelCapture()
    {
        ResetCaptureInput();
        CaptureTarget = null;
        CaptureDevices = Array.Empty<InputDeviceKind>();
        CaptureStatus = CaptureState.Cancelled;
    }

    public void ClearCapture()
    {
        ResetCaptureInput();
        CaptureTarget = null;
        CaptureDevices = Array.Empty<InputDeviceKind>();
        CaptureStatus = CaptureState.Idle;
        CaptureConflicts = Array.Empty<InputAction>();
    }

    private bool UpdateCapture(float deltaTime)
    {
        if (CaptureStatus != CaptureState.Listening || CaptureTarget == null)
            return false;

        _captureAge += deltaTime;
        if (_captureAge > CaptureTimeout)
        {
            CancelCapture();
            return true;
        }

        // Escape always means "leave it alone", on every device family, and is therefore never
        // bindable. Read the raw keyboard rather than an action so a text gate cannot swallow it.
        if (_keyboard.CaptureEscape())
        {
            CancelCapture();
            return true;
        }

        if (_captureRelease != null)
        {
            if (_keyboard.ChordKeysStillHeld(_captureRelease))
                return true;
            _captureRelease = null;
        }
        if (_captureChord != null && !_keyboard.ChordIsHeld(_captureChord))
        {
            var chord = _captureChord;
            _captureChord = null;
            AcceptCapture(chord);
            return true;
        }

        foreach (var device in CaptureDevices)
        {
            if (TryCaptureFrom(device, out var control))
            {
                var chord = _keyboard.CaptureChord(control);
                if (control.Device == InputDeviceKind.Keyboard)
                {
                    // A press begins a chord; release commits it. This leaves time for modifiers or
                    // additional keys, and still allows a modifier to be bound on its own.
                    _captureChord = chord;
                    _captureAge = 0f;
                }
                else
                {
                    _captureChord = null;
                    AcceptCapture(chord);
                }
                break;
            }
        }
        return true;
    }

    private void AcceptCapture(InputBinding chord)
    {
        var action = CaptureTarget!;
        int steps = chord.Control.Device == InputDeviceKind.Keyboard
            ? action.Kind == InputActionKind.Analog2D ? 4 : action.Kind == InputActionKind.Analog ? 2 : 1
            : 1;
        if (steps == 1)
            _captureBindings.Clear();
        int step = _captureBindings.Count;
        var target = steps == 4 ? (step < 2 ? BindingAxis.Y : BindingAxis.X) : DefaultTargetFor(action, chord.Control);
        float scale = steps > 1 && (step == 1 || step == 2) ? -1f : 1f;
        _captureBindings.Add(new InputBinding(chord.Control, BindingAxis.Auto, target, scale, chord.Modifiers));
        _captureAge = 0f;
        if (_captureBindings.Count < steps)
        {
            _captureRelease = chord;
            return;
        }

        // Commit the complete set together. Cancelling midway must leave all previous directions intact.
        foreach (var device in CaptureDevices)
            action.ClearBindings(device);
        foreach (var binding in _captureBindings)
            action.Bind(binding);
        action.IsOverridden = true;

        CaptureConflicts = action.Set?.FindConflicts(action, chord.Control) ?? (IReadOnlyList<InputAction>)Array.Empty<InputAction>();
        CaptureStatus = CaptureState.Captured;
        CaptureTarget = null;
        ResetCaptureInput();
        BindingsChanged?.Invoke(action);
    }

    // A digital control bound onto a two-axis action has to say which half it drives, or it would
    // land on Y by default and a rebound "strafe left" would walk you forwards.
    private static BindingAxis DefaultTargetFor(InputAction action, in ControlRef control)
    {
        if (action.Kind != InputActionKind.Analog2D)
            return BindingAxis.Auto;
        return InputControlCatalog.IsVectorControl(control) ? BindingAxis.Auto : BindingAxis.Y;
    }

    private bool TryCaptureFrom(InputDeviceKind device, out ControlRef control)
    {
        control = default;
        switch (device)
        {
            case InputDeviceKind.Keyboard:
                return _keyboard.TryCaptureControl(out control);
            case InputDeviceKind.Mouse:
                return _mouse.TryCaptureControl(out control);
            case InputDeviceKind.Gamepad:
                return _gamepad.TryCaptureControl(out control);
            case InputDeviceKind.VRController:
                if (_vrRight.TryCaptureControl(out control))
                    return true;
                return _vrLeft.TryCaptureControl(out control);
            default:
                return false;
        }
    }

    // EDITING

    public void ClearBindings(InputAction action, params InputDeviceKind[] devices)
    {
        if (action == null || devices == null)
            return;
        foreach (var device in devices)
            action.ClearBindings(device);
        action.IsOverridden = true;
        BindingsChanged?.Invoke(action);
    }

    public void ResetToDefaults(InputAction action)
    {
        if (action == null)
            return;
        action.ClearBindings();
        InputBindingDefaults.ApplyTo(this, action);
        action.IsOverridden = false;
        BindingsChanged?.Invoke(action);
    }

    public void ResetAllToDefaults()
    {
        foreach (var action in AllActions())
        {
            action.ClearBindings();
            action.IsOverridden = false;
        }
        InputBindingDefaults.Apply(this);
        BindingsChanged?.Invoke(null);
    }
}

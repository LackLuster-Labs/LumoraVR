// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components;

// Abstract base for locomotion modes. Each concrete module is a Component
// attached to the user's root slot. The controller keeps them in registration
// order and the active one is a sticky choice (menu / persisted), not picked
// by priority. Override CanActivate to gate eligibility (permission, VR-only).
// Movement logic lives in OnModuleUpdate, which the controller only calls
// while this module is active. - xlinka
[ComponentCategory("Users/Locomotion")]
public abstract class LocomotionModule : Component
{
    protected LocomotionController Owner { get; private set; } = null!;

    public bool IsActive => Owner != null && Owner.ActiveModule == this;

    // Two different silences, and a module has to tell them apart.
    //
    // InputBlocked is the loud one: free-cam, body calibration, a desktop tool that has taken mouse and keys.
    // The user is not driving their body at all, so a module does nothing whatsoever. A focused text field is
    // not one of them: it takes the keyboard alone, so a stick still walks and turns while somebody types. Nor
    // is the context menu: in a headset it holds its own hand's stick in the action map, so a module reads that
    // one stick centred and everything else as usual.
    //
    // MovementBlocked adds the narrow one: a seat. Translation is off, but the user is still sitting there
    // looking around, so turn keeps being serviced and blink still gets to aim (it stands them up when the
    // hop commits). -xlinka
    protected bool InputBlocked
    {
        get
        {
            var state = Owner?.InputState;
            return (state?.FreeCamActive ?? false) || (state?.DesktopInputSuppressed ?? false);
        }
    }

    protected bool MovementBlocked => InputBlocked || (Owner?.InputState?.MovementSuppressed ?? false);

    // True when this module is eligible right now (e.g. VR-only when VR is live,
    // or permission-gated). The controller skips modules that return false.
    public virtual bool CanActivate() => true;

    // What the menus call it: its label, else, installed from somewhere, its slot's name, else its stock name.
    public readonly Sync<string> Label = new();
    public string DisplayName => !string.IsNullOrEmpty(Label.Value) ? Label.Value!
        : IsInstalled && !string.IsNullOrEmpty(Slot?.Name.Value) ? Slot!.Name.Value! : StockName;
    protected virtual string StockName => GetType().Name;

    // Installed from a set of locomotions rather than one of the user's own: it sits on a slot of its own.
    public bool IsInstalled => Slot != null && Slot.GetComponent<UserRoot>() == null;

    public virtual LocomotionKind Kind => LocomotionKind.Other;

    public void ActivateInternal(LocomotionController owner)
    {
        Owner = owner;
        OnActivated();
    }

    public void DeactivateInternal()
    {
        OnDeactivated();
        Owner = null!;
    }

    protected virtual void OnActivated() { }
    protected virtual void OnDeactivated() { }
    public abstract void OnModuleUpdate(float delta);

    public override void OnDestroy()
    {
        Owner?.UnregisterModule(this);
        base.OnDestroy();
    }
}

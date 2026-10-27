// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Touch;

// The one thing a non-authority toucher writes.
//
// A probe only runs on the machine of the user driving it, and it used to dispatch the contact straight
// into the target - which meant the TOUCHER's peer authored every write the control and its responders
// made. On world content those writes belong to the host, so the gate refused them: on the toucher's own
// client first (a joiner's gate keeps the view-only preset, because the world mode's preset is only ever
// applied on the authority), and again on the host if the client were talked past its own check. Guests
// could not work world buttons at all.
//
// So a remote touch travels as a REQUEST. The relay is minted in the toucher's own RefID namespace -
// the one thing a user is always allowed to write - and carries just enough to rebuild the contact: what
// is being touched, both phases, the probe kind and hand, and the tip geometry depth-driven controls
// measure travel from. The authority reads it, re-checks the control's own gates with the relay's owner
// as the toucher, and runs the reaction itself, so everything that comes out of a guest's touch is
// host-authored. -xlinka
[ComponentCategory("Interaction/Touch")]
public class TouchRelay : Component
{
    public readonly SyncRef<TouchControl> Target;

    // Both phases, the probe kind, the hand and the activation counter, packed into ONE field. They
    // share a field so a phase can never arrive without the counter that dates it, which is what makes
    // the authority's dedupe exact rather than approximate. See Pack.
    public readonly Sync<int> Signal;

    public readonly Sync<float3> Tip;

    public readonly Sync<float3> Point;

    public readonly Sync<float> Penetration;

    // A relay is the toucher's own object, so a hostile client can spin it as fast as it can send
    // packets. One contact per frame is all a probe can honestly produce; this is the ceiling the
    // authority acts on no matter what arrives.
    internal const int MaxActivationsPerSecond = 120;

    private const int PhaseBits = 7;
    private const int CounterMask = (1 << 24) - 1;

    private int _counter;
    private int _lastSignal;

    private double _windowStart;
    private int _windowCount;
    private bool _loggedFlood;

    // What the authority last handed to a control, so a toucher who leaves mid-press can be closed out.
    private TouchControl? _held;
    private TouchProbeKind _heldKind;
    private Chirality _heldHand;

    public TouchRelay()
    {
        Target = new SyncRef<TouchControl>(this);
        Signal = new Sync<int>(this, 0);
        Tip = new Sync<float3>(this, float3.Zero);
        Point = new Sync<float3>(this, float3.Zero);
        Penetration = new Sync<float>(this, 0f);
    }

    public override void OnStart()
    {
        base.OnStart();
        // Whatever is already in the field the first time we see this relay is history, not an
        // activation - a joiner must not replay the press that was in flight when it arrived.
        _lastSignal = Signal.Value;
        _windowStart = World?.Time.TotalTime ?? 0.0;
    }

    // Called on the toucher's own peer by its probe. Writes only this relay, which the toucher owns.
    public void Publish(TouchControl control, in TouchContact contact)
    {
        if (control == null || control.IsDestroyed)
            return;

        if (!ReferenceEquals(Target.Target, control))
            Target.Target = control;

        Set(Tip, contact.Tip);
        Set(Point, contact.Point);
        if (Penetration.Value != contact.Penetration)
            Penetration.Value = contact.Penetration;

        _counter = (_counter + 1) & CounterMask;
        Signal.Value = Pack(contact.Hover, contact.Contact, contact.Kind, contact.Hand, _counter);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (World?.IsAuthority != true)
            return;

        int signal = Signal.Value;
        if (signal == _lastSignal)
            return;

        _lastSignal = signal;
        if (TakeActivationSlot())
            Consume(signal);
    }

    // A toucher who leaves or is torn down mid-press takes its relay with it, and the control would sit
    // held forever with nothing left to release it. Close it out on the way down. -xlinka
    public override void OnDestroy()
    {
        if (World?.IsAuthority == true)
            ReleaseHeld(TouchUserFilterExtensions.ResolveOwner(this));
        else
            _held = null;

        base.OnDestroy();
    }

    private void Consume(int signal)
    {
        // The toucher is the user whose allocation byte minted this relay, never a field on it. An owner
        // reference is something the client writes; the byte is something the host assigned, and it is
        // the only one of the two a forged relay cannot change. -xlinka
        var toucher = TouchUserFilterExtensions.ResolveOwner(this);
        if (toucher == null || toucher.IsDestroyed)
            return;

        var control = Target.Target;
        if (control == null || control.IsDestroyed || control.Slot?.IsActive != true)
        {
            ReleaseHeld(toucher);
            return;
        }

        // A hand sweeping straight from one control onto the next produces the old one's End and the new
        // one's Begin in the SAME frame, and only the last of the two survives a field. Close the old one
        // out here or it stays pressed for the rest of the session. -xlinka
        if (_held != null && !ReferenceEquals(_held, control))
            ReleaseHeld(toucher);

        var kind = KindOf(signal);
        var hand = HandOf(signal);
        var hover = HoverOf(signal);
        var contactPhase = ContactOf(signal);

        // The authority-side gate. The toucher's own client checked this too, for responsiveness, but
        // that check is not what enforces it - this one is. A control that goes dead mid-press (disabled,
        // filter changed, edit mode ended) still owes the toucher their release.
        if (!control.CanTouch(toucher, kind))
        {
            ReleaseHeld(toucher);
            return;
        }

        _heldKind = kind;
        _heldHand = hand;
        _held = contactPhase == TouchPhase.Begin || contactPhase == TouchPhase.Stay ? control : null;

        control.RunTouch(new TouchContact(
            hover, contactPhase, Point.Value, Tip.Value, Penetration.Value, kind, hand, toucher));
    }

    private void ReleaseHeld(User? toucher)
    {
        var held = _held;
        _held = null;
        if (held == null || held.IsDestroyed)
            return;

        held.RunTouch(new TouchContact(
            TouchPhase.End, TouchPhase.End, Point.Value, Tip.Value, 0f, _heldKind, _heldHand, toucher));
    }

    private bool TakeActivationSlot()
    {
        double now = World?.Time.TotalTime ?? 0.0;
        if (now - _windowStart >= 1.0)
        {
            _windowStart = now;
            _windowCount = 0;
        }

        if (_windowCount >= MaxActivationsPerSecond)
        {
            if (!_loggedFlood)
            {
                _loggedFlood = true;
                LumoraLogger.Warn(
                    $"Touch relay {ParentHierarchyToString()} is activating faster than {MaxActivationsPerSecond}/s - capping it.");
            }
            return false;
        }

        _windowCount++;
        return true;
    }

    private void Set(Sync<float3> field, in float3 value)
    {
        if (!field.Value.Equals(value))
            field.Value = value;
    }

    // Layout, low bits up: hover phase (2), contact phase (2), probe kind (1), hand (2), counter.
    public static int Pack(TouchPhase hover, TouchPhase contact, TouchProbeKind kind, Chirality hand, int counter)
        => ((int)hover & 3)
           | (((int)contact & 3) << 2)
           | ((kind == TouchProbeKind.Remote ? 1 : 0) << 4)
           | (((int)hand & 3) << 5)
           | ((counter & CounterMask) << PhaseBits);

    public static TouchPhase HoverOf(int signal) => (TouchPhase)(signal & 3);

    public static TouchPhase ContactOf(int signal) => (TouchPhase)((signal >> 2) & 3);

    public static TouchProbeKind KindOf(int signal)
        => ((signal >> 4) & 1) != 0 ? TouchProbeKind.Remote : TouchProbeKind.Fingertip;

    public static Chirality HandOf(int signal) => (Chirality)((signal >> 5) & 3);
}

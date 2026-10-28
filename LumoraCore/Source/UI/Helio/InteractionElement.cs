// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Touch;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Helio.UI;

[ComponentCategory("UI/Helio/Interaction")]
public class InteractionElement : UIComponent, IUIInteractable
{
    public readonly Sync<bool> Interactable;
    public readonly Sync<color> BaseColor;
    // Transient interaction state - don't persist it, or a saved item can come back stuck on a hover/
    // press tint that nothing clears (the pointer that set it is long gone). -xlinka
    [NonPersistent]
    public readonly Sync<bool> IsHovering;
    [NonPersistent]
    public readonly Sync<bool> IsPressed;

    // Canvas units added to the hit area on every side, so a control drawn smaller than its cell still
    // answers in the gap to its neighbours. Where two grown areas overlap, the later-drawn one wins.
    public readonly Sync<float> HitOutset;

    // What a pointer resting here names, beside its cursor. Empty names nothing.
    public readonly Sync<string> Tip;

    public event Action<UIInteractionContext>? HoverEntered;
    public event Action<UIInteractionContext>? HoverExited;
    public event Action<UIInteractionContext>? HoverMoved;
    public event Action<UIInteractionContext>? Pressed;
    public event Action<UIInteractionContext>? Dragged;
    public event Action<UIInteractionContext>? Released;
    public event Action<UIInteractionContext>? Submitted;

    public InteractionElement()
    {
        Interactable = new Sync<bool>(this, true);
        BaseColor = new Sync<color>(this, color.White);
        IsHovering = new Sync<bool>(this, false);
        IsPressed = new Sync<bool>(this, false);
        IsHovering.MarkUiState();
        IsPressed.MarkUiState();
        HitOutset = new Sync<float>(this, 0f);
        Tip = new Sync<string>(this, string.Empty);
    }

    public bool CanInteract => !IsDestroyed && Enabled.Value && Interactable.Value && Slot != null && Slot.IsActive;

    // Is a pointer DOWN on this element right now. The press/submit events are edges - they answer "was
    // this clicked", never "is the finger still on it" - and a key that repeats while you hold it has to
    // ask the second question every frame. The state itself was already here for the tint driver; this
    // just stops every caller reaching into the sync field and getting a stale answer off a dead
    // element. -xlinka
    public bool IsPressedNow => !IsDestroyed && IsPressed.Value;
    public InteractionState CurrentInteractionState
    {
        get
        {
            if (!CanInteract) return InteractionState.Disabled;
            if (IsPressed.Value) return InteractionState.Pressed;
            if (IsHovering.Value) return InteractionState.Highlight;
            return InteractionState.Normal;
        }
    }

    public IEnumerable<ColorDriver> ColorDrivers => Slot?.GetComponents<ColorDriver>() ?? Array.Empty<ColorDriver>();

    // One driver per tint field. A ColorDriver's Target IS its drive link now, and a field takes exactly
    // one granted driver - a second driver aimed at the same tint would be refused and sit there inert
    // behind the first, so its colors would silently never show. Reconfigure the driver that already
    // holds the field instead of stacking a dead one behind it. -xlinka
    public ColorDriver AddColorDriver(IField<color> target, color? normalColor = null,
        InteractionColorMode mode = InteractionColorMode.Explicit)
    {
        ColorDriver? driver = null;
        foreach (var existing in Slot.GetComponents<ColorDriver>())
        {
            if (ReferenceEquals(existing.Target.Target, target))
            {
                driver = existing;
                break;
            }
        }

        driver ??= Slot.AttachComponent<ColorDriver>();
        driver.Interaction.Target = this;
        driver.Target.Target = target;
        driver.TintColorMode.Value = mode;
        driver.SetColors(normalColor ?? target.Value);
        driver.Apply(this);
        return driver;
    }

    public virtual bool IsPointInside(in float2 point)
    {
        var rectTransform = RectTransform;
        return rectTransform != null && HitRect(rectTransform.LocalComputeRect).Contains(point);
    }

    // The drawn rect grown by HitOutset.
    protected Rect HitRect(in Rect rect)
    {
        float outset = HitOutset.Value;
        if (!(outset > 0f))
            return rect;
        var grow = new float2(outset, outset);
        return Rect.FromMinMax(rect.Min - grow, rect.Max + grow);
    }

    // Drag arbitration. By default a pressed element keeps the drag for itself. Override to RELINQUISH it
    // to a scrolling ancestor once the pointer has clearly started dragging - e.g. a button or list item
    // inside a scroll view, so dragging across it scrolls the list instead of being swallowed. dragDelta is
    // in canvas-local units measured from the press point. A plain click never moves far, so it never passes.
    public const float DragPassThreshold = 10f;

    public virtual bool PassDragToParent(in float2 dragDelta) => false;

    // Whether this element does its job the moment a press lands on it. Off here: a slider, a scroll or a
    // field works while it is held. Buttons and toggles turn it on, and opt back out per element.
    protected virtual bool ActsOnPress => false;

    // A control whose press is only ever a click: it acts and is done, and holds or drags nothing while
    // down. Buttons and toggles say yes. A deeper squeeze of a trigger already held may let go of such a
    // press and land a fresh one (Canvas.StepDeepTrigger); anything that works while held keeps its press.
    protected virtual bool IsClickControl => false;

    internal bool ClickControl => IsClickControl;

    // A bare element with nothing hooked to its drag (a window body that only raises its window) holds
    // nothing either.
    internal bool PressIsClick => Dragged == null && (IsClickControl || GetType() == typeof(InteractionElement));

    // This press already acted, so letting go of it must not act again.
    private bool _pressActed;

    // Inside the content of a scroll, up to this element's canvas, that asks its rows to act on release.
    protected bool InReleaseList()
    {
        for (var slot = Slot?.Parent; slot != null; slot = slot.Parent)
        {
            if (slot.GetComponent<ScrollRect>() is { RowsActOnRelease.Value: true })
                return true;
            if (slot.GetComponent<Canvas>() != null)
                return false;
        }
        return false;
    }

    // The press now held came here from another element (see NotifyPressTakenOver), so OnPress is a
    // takeover, not somebody aiming at this element.
    protected bool PressHandedOn { get; private set; }

    public void NotifyHoverEnter(in UIInteractionContext context) => NotifyHoverEnter(in context, pointerArrived: false);

    // pointerArrived: the pointer moved in from outside, rather than this element turning up under a pointer
    // that sat still (a ring opening on the hand, a page swap, a list rebuilt under the beam). Only an
    // arrival ticks the hand.
    public void NotifyHoverEnter(in UIInteractionContext context, bool pointerArrived)
    {
        using var actorScope = EnterInteractionActor(in context);
        if (!CanInteract) return;
        IsHovering.Value = true;
        ApplyColorDrivers();
        if (pointerArrived)
            TickHover(in context);
        OnHoverEnter(in context);
        HoverEntered?.Invoke(context);
    }

    public void NotifyHoverExit(in UIInteractionContext context)
    {
        // Canvas pointer state can outlive the element (menu rebuild/close while
        // destructions are still queued) - nothing to clear on a dead element. - xlinka
        if (IsDestroyed) return;
        using var actorScope = EnterInteractionActor(in context);
        IsHovering.Value = false;
        ApplyColorDrivers();
        OnHoverExit(in context);
        HoverExited?.Invoke(context);
    }

    public void NotifyHoverMove(in UIInteractionContext context)
    {
        if (IsDestroyed || !CanInteract) return;
        using var actorScope = EnterInteractionActor(in context);
        OnHoverMove(in context);
        HoverMoved?.Invoke(context);
    }

    public void NotifyPress(in UIInteractionContext context) => BeginPress(in context, mayAct: true);

    // A press that was already down when it got here: handed on by an element that stepped aside under the
    // pointer, or by a list item giving its drag to the scroll. It holds and drags like any press, but it
    // was never aimed at this element, so it never acts.
    public void NotifyPressTakenOver(in UIInteractionContext context) => BeginPress(in context, mayAct: false);

    private void BeginPress(in UIInteractionContext context, bool mayAct)
    {
        using var actorScope = EnterInteractionActor(in context);
        _pressActed = false;
        PressHandedOn = !mayAct;
        if (!CanInteract) return;
        IsPressed.Value = true;
        ApplyColorDrivers();
        OnPress(in context);
        Pressed?.Invoke(context);
        if (mayAct && ActsOnPress && CanInteract)
        {
            _pressActed = true;
            Act(in context);
        }
    }

    public void NotifyDrag(in UIInteractionContext context)
    {
        using var actorScope = EnterInteractionActor(in context);
        if (!CanInteract) return;
        OnDrag(in context);
        Dragged?.Invoke(context);
    }

    public void NotifyRelease(in UIInteractionContext context)
    {
        if (IsDestroyed) return;
        using var actorScope = EnterInteractionActor(in context);
        IsPressed.Value = false;
        ApplyColorDrivers();
        OnRelease(in context);
        Released?.Invoke(context);
    }

    // The canvas calls this when a press lets go over the element it began on. One that acted as it landed
    // has already done its job.
    public void NotifySubmit(in UIInteractionContext context)
    {
        using var actorScope = EnterInteractionActor(in context);
        if (!CanInteract || _pressActed) return;
        Act(in context);
    }

    private void Act(in UIInteractionContext context)
    {
        Haptics.Pulse(context.Actor, PulseSide(in context), TouchHaptics.Click);
        OnSubmit(in context);
        Submitted?.Invoke(context);
    }

    // The hand a pointer belongs to, for its pulses. A mouse has none.
    private static Chirality PulseSide(in UIInteractionContext context) => context.Source switch
    {
        UIInteractionSource.VRLeft => Chirality.Left,
        UIInteractionSource.VRRight => Chirality.Right,
        UIInteractionSource.Touch => CanvasTouchSource.SideOf(context.PointerId),
        _ => Chirality.None,
    };

    // A sweep across a packed grid enters a new element every frame or two. Spaced out, it reads as ticks;
    // every frame, it reads as one long buzz.
    private const double HoverTickSpacing = 0.06;
    private static readonly (World? World, double Time)[] LastHoverTick = new (World?, double)[3];

    private void TickHover(in UIInteractionContext context)
    {
        var side = PulseSide(in context);
        if (side == Chirality.None || context.Actor is not { } user || user.World?.LocalUser != user)
            return;
        var world = Slot?.World;
        if (world == null)
            return;
        double now = world.Time.TotalTime;
        ref var last = ref LastHoverTick[(int)side];
        if (ReferenceEquals(last.World, world) && now >= last.Time && now - last.Time < HoverTickSpacing)
            return;
        last = (world, now);
        Haptics.Pulse(user, side, TouchHaptics.Light);
    }

    protected virtual void OnHoverEnter(in UIInteractionContext context) { }
    protected virtual void OnHoverExit(in UIInteractionContext context) { }
    protected virtual void OnHoverMove(in UIInteractionContext context) { }
    protected virtual void OnPress(in UIInteractionContext context) { }
    protected virtual void OnDrag(in UIInteractionContext context) { }
    protected virtual void OnRelease(in UIInteractionContext context) { }
    protected virtual void OnSubmit(in UIInteractionContext context) { }

    private IDisposable? EnterInteractionActor(in UIInteractionContext context)
    {
        var world = Slot?.World ?? context.Canvas?.World;
        return world?.DataModelPermissions.EnterActor(context.Actor);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        if (Slot != null)
            Slot.ActiveChanged += OnSlotActiveChanged;
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.ActiveChanged -= OnSlotActiveChanged;
        base.OnDestroy();
    }

    // CanInteract reads Slot.IsActive, so a driver's first Apply on anything built under a hidden panel
    // (every dash screen, the keyboard) paints the DISABLED colour, and that grey is brighter than any
    // dark fill: the whole panel came up looking hovered until each button was pointed at once. The
    // active flip is the moment the state actually changes, so it re-runs the drivers. ActiveChanged is
    // effective-active: it fires for an ancestor's flip too. -xlinka
    private void OnSlotActiveChanged(Slot slot)
    {
        if (IsDestroyed)
            return;
        ApplyColorDrivers();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        ApplyColorDrivers();
    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        ApplyColorDrivers();
    }

    public override void OnEnabled()
    {
        base.OnEnabled();
        ApplyColorDrivers();
    }

    protected void ApplyColorDrivers()
    {
        foreach (var driver in ColorDrivers)
        {
            driver.Apply(this);
        }
    }
}

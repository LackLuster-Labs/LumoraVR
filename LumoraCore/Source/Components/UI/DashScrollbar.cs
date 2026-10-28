// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The dash's draggable scrollbar: a track down the right edge and a handle sized to the fraction of the
// content you can see.
//
// One class instead of one per screen. The Session settings form grew this first and the permissions
// page needed the same thing, and a copy would have drifted: the "wait for a real viewport rect" retry,
// the clamp that stops a shrunk content resting past its own bottom and the hide-when-it-fits rule are
// all things you get wrong once and then only on one of the two pages. The two placements differ and
// nothing else does, so that is the only thing the two entry points disagree about. -xlinka
internal sealed class DashScrollbar
{
    public const float Width = 18f;

    // A freshly-built viewport sits at the RectTransform default (100x100) until the canvas runs a layout
    // pass. Sizing off that 100 makes maxScroll = content - 100 (huge) with a tiny thumb, so a short form
    // scrolls out of view into empty space. This sentinel rejects the un-laid-out rect; a real dash
    // viewport is several hundred units, so a legitimate size is never refused. -xlinka
    private const float LaidOutViewportFloor = 100f;
    private const float MinHandleHeight = 30f;
    private const float HandleInset = 2f;
    private const int MaxRetries = 30;
    // A held handle eases toward where the pointer puts it, per second. The handle is geared to the content
    // (ten pages of list move ten pages per handle length), so the shake of a hand held still would shake the
    // list by rows; eased, a held handle reads still and a moving one still keeps up.
    private const float HandleFollowRate = 18f;

    private readonly Slot _track;
    private readonly RectTransform? _trackRect;
    private readonly RectTransform _handle;

    private ScrollRect? _scroll;
    private RectTransform? _viewport;
    private RectTransform? _content;
    private Action<ScrollRect, float2>? _scrollChanged;
    private Action? _moved;
    private float _pressY;
    private float _pressScroll;
    private float _dragTarget;
    private bool _dragging;
    private int _retries;
    private bool _retryQueued;

    private DashScrollbar(Slot track, RectTransform handle)
    {
        _track = track;
        _trackRect = track.GetComponent<RectTransform>();
        _handle = handle;
    }

    // Sized by a LayoutElement, for a screen whose scroll area is a HorizontalLayout: hiding the track
    // then gives its width back to the viewport beside it.
    public static DashScrollbar InColumn(Slot area)
    {
        var track = area.AddSlot("Scrollbar");
        track.AttachComponent<RectTransform>();
        var element = track.AttachComponent<LayoutElement>();
        element.MinWidth.Value = Width;
        element.PreferredWidth.Value = Width;
        element.FlexibleWidth.Value = 0f;
        element.FlexibleHeight.Value = 1f;
        return Build(track);
    }

    // Anchored to the parent's right edge, for a page that places its viewport by hand. The caller keeps
    // the viewport clear of the track; the inset does not move when the track hides, because a row set
    // that reflows sideways every time the content crosses the fit threshold reads as a glitch.
    public static DashScrollbar OnRightEdge(Slot host, float right, float top, float bottom)
    {
        var track = SettingsUI.Child(host, "Scrollbar", new float2(1f, 0f), new float2(1f, 1f),
            new float2(-(right + Width), bottom), new float2(-right, -top));
        return Build(track);
    }

    private static DashScrollbar Build(Slot track)
    {
        SettingsUI.Panel(track, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);

        var handleSlot = track.AddSlot("Handle");
        var handle = handleSlot.AttachComponent<RectTransform>();
        handle.AnchorMin.Value = new float2(0f, 1f);
        handle.AnchorMax.Value = new float2(1f, 1f);
        handle.OffsetMin.Value = new float2(HandleInset, -(MinHandleHeight * 2f));
        handle.OffsetMax.Value = new float2(-HandleInset, 0f);
        var handlePanel = SettingsUI.Panel(handleSlot, DashTheme.Accent, color.Transparent, DashTheme.RadiusControl);

        var bar = new DashScrollbar(track, handle);

        // The handle is the draggable element (classic scrollbar feel); the viewport's own ScrollRect
        // already takes drag-on-content and the wheel.
        var interaction = CreateHandleInteraction(handleSlot, handlePanel.Color);
        interaction.Pressed += bar.OnPress;
        interaction.Dragged += bar.OnDrag;
        interaction.Released += bar.OnRelease;
        return bar;
    }

    // A track some earlier build left behind (a panel saved and loaded again): its handle's drag goes through
    // closures, which never travel with the slots, so they are hooked up again here. Null when the track is
    // not one of these.
    public static DashScrollbar? Adopt(Slot? track)
    {
        var handleSlot = track?.FindChild("Handle", recursive: false);
        var handle = handleSlot?.GetComponent<RectTransform>();
        var interaction = handleSlot?.GetComponent<InteractionElement>();
        if (track == null || handle == null || interaction == null)
            return null;
        var bar = new DashScrollbar(track, handle);
        interaction.Pressed += bar.OnPress;
        interaction.Dragged += bar.OnDrag;
        interaction.Released += bar.OnRelease;
        return bar;
    }

    // The vertical and horizontal bars use the same feedback while keeping their normal handle color.
    internal static InteractionElement CreateHandleInteraction(Slot handle, IField<color> tint)
    {
        var interaction = handle.AttachComponent<InteractionElement>();
        var driver = interaction.AddColorDriver(tint, tint.Value, InteractionColorMode.Direct);
        driver.HighlightColor.Value = DashTheme.AccentHover;
        driver.PressedColor.Value = DashTheme.AccentPressed;
        driver.DisabledColor.Value = DashTheme.TextMuted;
        driver.Apply(interaction);
        return interaction;
    }

    // Point at the scroll this bar drives. moved runs after the bar itself changes the scroll, for the
    // screen to dirty its canvas; the ScrollRect's own event covers every other way it can move.
    public void Bind(ScrollRect? scroll, RectTransform? viewport, RectTransform? content, Action? moved = null)
    {
        if (_scroll != null && _scrollChanged != null && !_scroll.IsDestroyed)
            _scroll.ScrollChanged -= _scrollChanged;

        _scroll = scroll;
        _viewport = viewport;
        _content = content;
        _moved = moved;
        _retries = 0;
        _retryQueued = false;

        if (_scroll == null)
            return;
        _scrollChanged = (_, _) => Refresh();
        _scroll.ScrollChanged += _scrollChanged;
    }

    private void OnPress(UIInteractionContext context)
    {
        _scroll?.StopScrolling();
        _pressY = context.LocalPoint.y;
        _pressScroll = _scroll?.AbsolutePosition.y ?? 0f;
        _dragTarget = _pressScroll;
        _dragging = false;
    }

    private void OnDrag(UIInteractionContext context)
    {
        if (!Measure(out float viewportHeight, out float contentHeight))
            return;
        float maxScroll = MathF.Max(0f, contentHeight - viewportHeight);
        if (maxScroll <= 0f)
            return;
        float track = TrackHeight(viewportHeight);
        float travel = track - HandleHeight(track, viewportHeight, contentHeight);
        if (travel <= 0f)
            return;

        // Dragging the handle down (local Y decreases) scrolls the content down.
        float deltaY = context.LocalPoint.y - _pressY;
        float scrolled = _pressScroll - deltaY * (maxScroll / travel);
        _dragTarget = System.Math.Clamp(scrolled, 0f, maxScroll);
        _dragging = true;
        float shown = Follow(_scroll!.AbsolutePosition.y, _dragTarget, _scroll.World);
        _scroll.AbsolutePosition = new float2(0f, System.Math.Clamp(shown, 0f, maxScroll));
        Refresh();
        _moved?.Invoke();
    }

    // Let go, the list lands where the last drag frame put the handle; the release point itself carries the
    // jolt of letting go.
    private void OnRelease(UIInteractionContext context)
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (_scroll == null || _scroll.IsDestroyed || !Measure(out float viewportHeight, out float contentHeight))
            return;
        float maxScroll = MathF.Max(0f, contentHeight - viewportHeight);
        _scroll.AbsolutePosition = new float2(0f, System.Math.Clamp(_dragTarget, 0f, maxScroll));
        Refresh();
        _moved?.Invoke();
    }

    // One step of the held handle's easing toward its target. A mouse holds still; only a headset eases.
    internal static float Follow(float shown, float target, World? world)
    {
        float dt = world?.Time.Delta ?? 0f;
        if (dt <= 0f || Engine.Current?.InputInterface?.IsVRActive != true)
            return target;
        return shown + (target - shown) * (1f - MathF.Exp(-HandleFollowRate * dt));
    }

    // Size and place the handle for whatever the content is now. Safe to call every frame and cheap when
    // nothing moved: every write underneath is equality-gated.
    public void Refresh()
    {
        if (_scroll == null || _track == null || _track.IsDestroyed || _handle == null || _handle.IsDestroyed)
            return;
        if (!Measure(out float viewportHeight, out float contentHeight))
        {
            // Layout has not produced the real rects yet. Retry next frame, bounded, so a genuinely tiny
            // or broken panel cannot churn forever. One retry in flight at a time: a screen that calls
            // this from its own tick would otherwise queue a fresh one every frame it waits. -xlinka
            if (!_retryQueued && _retries++ < MaxRetries)
            {
                _retryQueued = true;
                _track.World?.RunInUpdates(1, RetryRefresh);
            }
            return;
        }

        float maxScroll = MathF.Max(0f, contentHeight - viewportHeight);
        if (maxScroll <= 0.5f)
        {
            if (_track.ActiveSelf.Value)
                _track.ActiveSelf.Value = false;
            // Drop any stale scroll so the page sits at the top rather than at an offset it can no longer
            // reach.
            if (_scroll.NormalizedPosition.y != 0f)
                _scroll.NormalizedPosition = float2.Zero;
            return;
        }
        if (!_track.ActiveSelf.Value)
            _track.ActiveSelf.Value = true;
        // A track just shown has no rect of its own until the next layout pass.
        if ((_trackRect?.LocalComputeRect.height ?? 0f) <= MinHandleHeight)
        {
            if (!_retryQueued && _retries++ < MaxRetries)
            {
                _retryQueued = true;
                _track.World?.RunInUpdates(1, RetryRefresh);
            }
            return;
        }
        _retries = 0;

        // Clamp an existing scroll to the (possibly reduced) real range so it cannot rest past the bottom.
        if (_scroll.AbsolutePosition.y > maxScroll)
            _scroll.AbsolutePosition = new float2(0f, maxScroll);

        float track = TrackHeight(viewportHeight);
        float handleHeight = HandleHeight(track, viewportHeight, contentHeight);
        float fraction = System.Math.Clamp(_scroll.AbsolutePosition.y / maxScroll, 0f, 1f);
        float offset = fraction * (track - handleHeight);
        SettingsUI.SetOffsets(_handle, new float2(HandleInset, -(offset + handleHeight)),
            new float2(-HandleInset, -offset));
    }

    private void RetryRefresh()
    {
        _retryQueued = false;
        Refresh();
    }

    private bool Measure(out float viewportHeight, out float contentHeight)
    {
        viewportHeight = _viewport?.LocalComputeRect.height ?? 0f;
        contentHeight = _content?.LocalComputeRect.height ?? 0f;
        return _scroll != null && !_scroll.IsDestroyed
            && viewportHeight > LaidOutViewportFloor && contentHeight > 0f;
    }

    // The handle runs in the track, which a page may keep shorter than the viewport (the inspector puts its
    // jump-to-top and jump-to-bottom caps at the ends), so its size and travel are the track's. Sized off the
    // viewport it ran past the track's end onto the bottom cap. -xlinka
    private float TrackHeight(float viewportHeight)
    {
        float height = _trackRect?.LocalComputeRect.height ?? 0f;
        return height > MinHandleHeight ? height : viewportHeight;
    }

    private static float HandleHeight(float trackHeight, float viewportHeight, float contentHeight)
        => MathF.Min(trackHeight, MathF.Max(MinHandleHeight, trackHeight * (viewportHeight / contentHeight)));
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Math;

namespace Helio.UI;

[ComponentCategory("UI/Helio/Interaction")]
public sealed class ScrollRect : InteractionElement, IUIAxisActionReceiver
{
    public readonly SyncRef<RectTransform> Content;
    // Persisted scroll position is NORMALIZED 0..1 per axis - it holds its proportional place when the content
    // or viewport resizes, and a normalized value can never produce an out-of-range scroll against a wrong
    // content size. The Sync field name stays 'Scroll' for wire compatibility; work in pixels via
    // AbsolutePosition. -xlinka
    public readonly Sync<float2> Scroll;
    public readonly Sync<float2> ScrollSensitivity;

    // A sideways-only strip (the session strip in the world browser) has no vertical travel, so a wheel
    // over it would do nothing at all. Set this and the wheel's y notches drive x instead. Drag and
    // programmatic scrolling are untouched. -xlinka
    public readonly Sync<bool> WheelScrollsHorizontal;

    // A list you drag to scroll is mostly rows, so a drag mostly starts on one. Set this and every button and
    // toggle in the content acts when its press lets go, not when it lands, and never once the scroll has
    // taken the drag: starting a scroll on a row does not open, flip or remove it. Rows added later follow.
    public readonly Sync<bool> RowsActOnRelease;

    // A scroll can also sit on the content it moves: set Viewport to the rect it scrolls inside and leave Content
    // empty. The content is then placed against the viewport, not left where its layout put it: overflowing, a
    // scroll of zero shows its top and its right end and the scroll runs the rest of the way; smaller than the
    // viewport, it sits by the alignment below (Start is left and top). Drags move it with the pointer. -xlinka
    public readonly SyncRef<RectTransform> Viewport;
    public readonly Sync<LayoutAlignment> HorizontalAlignment;
    public readonly Sync<LayoutAlignment> VerticalAlignment;

    // A RectTransform sits at its 100x100 default until the canvas runs a layout pass. Applying scroll against
    // that default (excess = content - 100) could shove the content out of the also-stale clip rect. Reject a
    // viewport at/under this floor and wait for a real laid-out rect (the next cycle re-runs ApplyScroll). -xlinka
    private const float UnlaidViewportFloor = 100f;

    // Units one wheel notch moves, on either axis.
    private const float NotchStep = 24f;

    // Wheel notches and controller axes accumulate a destination; the displayed position follows it.
    // About 95% of the distance is covered in 120 ms, independent of the frame rate.
    private const float ScrollFollowRate = 25f;
    private const float ScrollSnapDistance = 0.1f;
    private float2 _scrollTarget;
    private bool _smoothing;
    private bool _writingScroll;

    private float2 _pressPoint;
    private float2 _pressAbsolute;

    // Last known scrollable excess (content - viewport, per axis). Only a fallback for the frames where the
    // rects cannot be read yet; ScrollableExcess() is what everything actually uses. -xlinka
    private float2 _excess;

    // Content height handed over by a virtualised list (see SetContentExtent). Negative means nobody told us
    // and the measured content rect is the only source. -xlinka
    private float _contentExtent = -1f;

    // The content's own graphic chunk. Render-offset scrolling moves THIS chunk (its mesh is baked once)
    // instead of re-tessellating, so big lists scroll cheaply. -xlinka
    private GraphicChunkRoot? _contentRoot;
    private bool _scrollSetupDone;

    public event Action<ScrollRect, float2>? ScrollChanged;

    public ScrollRect()
    {
        Content = new SyncRef<RectTransform>(this);
        Scroll = new Sync<float2>(this, float2.Zero);
        Scroll.MarkUiState();
        ScrollSensitivity = new Sync<float2>(this, float2.One);
        WheelScrollsHorizontal = new Sync<bool>(this, false);
        RowsActOnRelease = new Sync<bool>(this, false);
        Viewport = new SyncRef<RectTransform>(this);
        HorizontalAlignment = new Sync<LayoutAlignment>(this, LayoutAlignment.Start);
        VerticalAlignment = new Sync<LayoutAlignment>(this, LayoutAlignment.Start);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Scroll.OnChanged += _ => { if (!_writingScroll) StopScrolling(); };
        Content.OnChanged += _ => ResetScrollContent();
        Viewport.OnChanged += _ => ResetScrollContent();
        Slot.ActiveChanged += OnScrollActiveChanged;
    }

    public override void OnDestroy()
    {
        Slot.ActiveChanged -= OnScrollActiveChanged;
        base.OnDestroy();
    }

    public override void OnDisabled()
    {
        StopScrolling();
        base.OnDisabled();
    }

    private void OnScrollActiveChanged(Slot slot)
    {
        if (!slot.IsActive)
            StopScrolling();
    }

    private void ResetScrollContent()
    {
        StopScrolling();
        _contentRoot = null;
        _scrollSetupDone = false;
    }

    // Direct manipulation (including scrollbar presses) takes over exactly where the content is now.
    public void StopScrolling() => _smoothing = false;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!_smoothing)
            return;
        if (!CanInteract || MovingRect is not { IsDestroyed: false })
        {
            StopScrolling();
            return;
        }
        if (!(delta > 0f) || !float.IsFinite(delta))
            return;

        var excess = ScrollableExcess();
        var shown = Scroll.Value;
        // A page that now fits has no animation left on that axis. Keep the current proportional place.
        if (excess.x <= 0f) _scrollTarget.x = shown.x;
        if (excess.y <= 0f) _scrollTarget.y = shown.y;
        float follow = 1f - MathF.Exp(-ScrollFollowRate * delta);
        var next = shown + (_scrollTarget - shown) * follow;
        if (MathF.Abs((_scrollTarget.x - next.x) * excess.x) <= ScrollSnapDistance) next.x = _scrollTarget.x;
        if (MathF.Abs((_scrollTarget.y - next.y) * excess.y) <= ScrollSnapDistance) next.y = _scrollTarget.y;
        _smoothing = next != _scrollTarget;
        SetNormalized(next, animated: true);
    }

    // The rect the scroll frames and the one it moves: this slot's and Content, or Viewport and this slot's.
    private bool OnContent => Viewport.Target != null;
    private RectTransform? FrameRect => Viewport.Target ?? RectTransform;
    private RectTransform? MovingRect => Viewport.Target != null ? RectTransform : Content.Target;

    // How far the moving rect's chunk is slid for a scroll of this many units.
    private float2 ContentShift(in float2 absolute)
    {
        if (Viewport.Target is not { } frame || RectTransform is not { } moving)
            return new float2(-absolute.x, absolute.y);
        var view = frame.LocalComputeRect;
        var content = moving.LocalComputeRect;
        float spareX = content.width - view.width;
        float spareY = content.height - view.height;
        float leadX = spareX > 0f ? spareX : HorizontalAlignment.Value switch
        {
            LayoutAlignment.Start => 0f,
            LayoutAlignment.End => spareX,
            _ => spareX * 0.5f,
        };
        float leadY = spareY > 0f ? spareY : VerticalAlignment.Value switch
        {
            LayoutAlignment.Start => spareY,
            LayoutAlignment.End => 0f,
            _ => spareY * 0.5f,
        };
        return new float2(view.xMin - leadX + absolute.x - content.xMin, view.yMin - leadY + absolute.y - content.yMin);
    }

    // normalized 0..1 scroll position (the persisted value), clamped on set
    public float2 NormalizedPosition
    {
        get => Scroll.Value;
        set => SetNormalized(new float2(Clamp01(value.x), Clamp01(value.y)));
    }

    // pixels = normalized * scrollable excess. setting converts back to normalized against the
    // live excess (a zero-excess axis keeps its value, so no divide-by-zero).
    public float2 AbsolutePosition
    {
        get
        {
            var excess = ScrollableExcess();
            return new float2(Scroll.Value.x * excess.x, Scroll.Value.y * excess.y);
        }
        set
        {
            var excess = ScrollableExcess();
            SetNormalized(new float2(
                excess.x > 0f ? Clamp01(value.x / excess.x) : Scroll.Value.x,
                excess.y > 0f ? Clamp01(value.y / excess.y) : Scroll.Value.y));
        }
    }

    // A list that pins its own content height tells us the number here.
    //
    // It knows the page height a full frame before any layout pass can measure the rect, and it is the only
    // writer of that rect's size, so when it has spoken it beats the measured height. Without this the range
    // and the drawn offset both wait on a pass that a scoped relayout never runs, and a page whose height
    // changed under a parked scroll ends up drawn at one offset while its rows were realized for another -
    // which reads as a band of the viewport going blank. -xlinka
    public void SetContentExtent(float height)
    {
        if (height < 0f)
            height = 0f;
        if (_contentExtent == height)
            return;
        _contentExtent = height;

        // The same normalized position over a different range is a different PIXEL position, so the content
        // has to be re-drawn where the freshly realized rows expect to find it.
        var before = _excess;
        if (ScrollableExcess() != before)
            ApplyScrollNow();
    }

    // Scrollable excess read from the LIVE rects, not from whenever the canvas last ran a full layout pass.
    //
    // This number used to be written only inside ApplyScroll, which the canvas calls from the FULL layout
    // branch alone. A virtualised list sets its page height by writing the content rect, and that write
    // reaches the canvas on the SCOPED relayout path (the scrolled content is its own graphic chunk), which
    // re-anchors the rect but never re-runs the scroll pass. So a page taller than the previous one kept the
    // previous page's range - zero when you came from a page that fit - the wheel clamped against it, and
    // nothing could break the deadlock, because a scroll that cannot move dirties no layout. -xlinka
    private float2 ScrollableExcess()
    {
        var viewport = FrameRect;
        var content = MovingRect;
        if (viewport == null || content == null)
            return _excess;

        var viewportRect = viewport.LocalComputeRect;
        var contentRect = content.LocalComputeRect;
        if (viewportRect.IsEmpty || contentRect.IsEmpty)
            return _excess;
        // Same un-laid-out guard ApplyScroll uses - see the field doc on UnlaidViewportFloor.
        if (viewportRect.width <= UnlaidViewportFloor || viewportRect.height <= UnlaidViewportFloor)
            return _excess;

        float contentHeight = _contentExtent >= 0f ? _contentExtent : contentRect.height;
        _excess = new float2(
            MathF.Max(0f, contentRect.width - viewportRect.width),
            MathF.Max(0f, contentHeight - viewportRect.height));
        return _excess;
    }

    public override void OnChanges()
    {
        base.OnChanges();
        if (!CanInteract)
            StopScrolling();
        ApplyScrollNow();
    }

    // Apply the current scroll position by sliding the content chunk's clip_offset (no rebuild, no
    // re-tessellation). Only forces a layout pass when the content chunk isn't built yet (the very first scroll),
    // so the chunk gets baked and ApplyScroll can position it. -xlinka
    private void ApplyScrollNow()
    {
        var canvas = FindCanvas();
        if (canvas == null)
            return;
        EnsureScrollSetup();
        // Content slides left/up as you scroll right/down (same sign as the rebuild path in ApplyScroll).
        var absolute = AbsolutePosition;
        if (_contentRoot != null && canvas.ApplyScrollOffset(_contentRoot, ContentShift(absolute)))
            return;
        canvas.MarkLayoutDirty();
    }

    // One-time: give the content its own graphic chunk so render-offset scrolling can move it as a unit.
    private void EnsureScrollSetup()
    {
        if (_scrollSetupDone)
            return;
        var contentSlot = MovingRect?.Slot;
        if (contentSlot == null)
            return;
        _contentRoot = contentSlot.GetComponent<GraphicChunkRoot>() ?? contentSlot.AttachComponent<GraphicChunkRoot>();
        _contentRoot.ScrollContent = true;
        _scrollSetupDone = true;
    }

    private Canvas? FindCanvas()
    {
        for (var slot = Slot; slot != null; slot = slot.Parent)
        {
            var canvas = slot.GetComponent<Canvas>();
            if (canvas != null)
                return canvas;
        }
        return null;
    }

    protected override void OnPress(in UIInteractionContext context)
    {
        StopScrolling();
        // Viewport space, not raw canvas space: for a scroll nested in another scroll the outer offset can
        // move mid-gesture (wheel, second pointer), and a raw press anchor would make this drag jump by the
        // outer delta. Same space on both ends, so the delta is pure pointer motion. -xlinka
        _pressPoint = context.PointIn(DragSpace);
        _pressAbsolute = AbsolutePosition;
    }

    // A scroll that sits on its own content rides its own offset. Measured on this slot, the pointer has
    // the scroll so far taken back off every frame, and the content flips between the pointer and the
    // press point. The parent keeps every outer scroll and drops only this one.
    private Slot? DragSpace => OnContent ? Slot?.Parent ?? Slot : Slot;

    protected override void OnDrag(in UIInteractionContext context)
    {
        var delta = context.PointIn(DragSpace) - _pressPoint;
        var sensitivity = ScrollSensitivity.Value;
        if (OnContent)
        {
            AbsolutePosition = new float2(_pressAbsolute.x + delta.x * sensitivity.x, _pressAbsolute.y + delta.y * sensitivity.y);
            return;
        }
        // Canvas y runs up and the scroll runs down the page, so a pointer moving down takes the scroll back
        // toward the top: the content stays under the pointer.
        AbsolutePosition = new float2(
            _pressAbsolute.x - delta.x * sensitivity.x,
            _pressAbsolute.y + delta.y * sensitivity.y);
    }

    public bool WantsAxis(in UIInteractionContext context)
    {
        var excess = ScrollableExcess();
        return excess.x > 0f || excess.y > 0f;
    }

    public bool ProcessAxis(in UIInteractionContext context, in float2 axis)
    {
        if (!CanInteract || axis == float2.Zero || !float.IsFinite(axis.x) || !float.IsFinite(axis.y))
        {
            return false;
        }

        var sensitivity = ScrollSensitivity.Value;
        // Up and right on the axis show what is above and to the right. A scroll on its content counts x from
        // the right end, so its x runs the other way.
        float stepX = axis.x * sensitivity.x * NotchStep;
        float stepY = axis.y * sensitivity.y * NotchStep;
        float2 offset;
        if (OnContent)
            offset = new float2(-stepX, -stepY);
        else if (WheelScrollsHorizontal.Value)
        {
            // The same sign as the vertical path, so a wheel-down that would scroll a list further in scrolls
            // a strip further right.
            offset = new float2(stepX - stepY, 0f);
        }
        else
            offset = new float2(stepX, -stepY);

        var excess = ScrollableExcess();
        var shown = Scroll.Value;
        var before = _smoothing ? _scrollTarget : shown;
        _scrollTarget = new float2(
            AxisTarget(shown.x, before.x, offset.x, excess.x),
            AxisTarget(shown.y, before.y, offset.y, excess.y));
        _smoothing = _scrollTarget != shown;
        // Keep the wheel in this list while it is still approaching the requested edge. Once it lands,
        // returning false lets a containing scroll handle further movement in that direction.
        return _scrollTarget != before
            || offset.x * (_scrollTarget.x - shown.x) > 0f
            || offset.y * (_scrollTarget.y - shown.y) > 0f;
    }

    private static float AxisTarget(float shown, float target, float offset, float excess)
    {
        if (excess <= 0f)
            return shown;
        // Reversing the wheel reverses movement immediately instead of first finishing queued notches.
        if (offset * (target - shown) < 0f)
            target = shown;
        return Clamp01(target + offset / excess);
    }

    internal bool ApplyScroll(out RectTransform? content)
    {
        content = MovingRect;
        var viewport = FrameRect;
        if (viewport == null || content == null)
        {
            return false;
        }

        var viewportRect = viewport.LocalComputeRect;
        var contentRect = content.LocalComputeRect;
        if (viewportRect.IsEmpty || contentRect.IsEmpty)
        {
            return false;
        }

        // Don't apply scroll against an un-laid-out viewport (still the 100x100 default) - see field doc.
        if (viewportRect.width <= UnlaidViewportFloor || viewportRect.height <= UnlaidViewportFloor)
        {
            return false;
        }

        var excess = ScrollableExcess();

        var norm = new float2(Clamp01(Scroll.Value.x), Clamp01(Scroll.Value.y));
        if (norm != Scroll.Value)
        {
            Scroll.Value = norm;
        }

        var absolute = new float2(norm.x * excess.x, norm.y * excess.y);

        // Position lives in the content's mesh-chunk offset, NEVER in its rect. We only store the current offset
        // here (a structural rebuild just baked the chunk); live scrolling moves it via ApplyScrollOffset. The
        // content rect is owned solely by its ContentSizeFitter (size), so the two never fight. Always return false
        // - the caller never re-lays-out the content for a scroll. -xlinka
        EnsureScrollSetup();
        if (_contentRoot != null)
            _contentRoot.RenderOffset = ContentShift(absolute);
        return false;
    }

    private static float Clamp01(float value)
    {
        if (value < 0f) return 0f;
        if (value > 1f) return 1f;
        return value;
    }

    private void SetNormalized(float2 normalized, bool animated = false)
    {
        if (!animated)
            StopScrolling();
        if (Scroll.Value == normalized) return;

        _writingScroll = true;
        try
        {
            Scroll.Value = normalized;
        }
        finally
        {
            _writingScroll = false;
        }
        ScrollChanged?.Invoke(this, AbsolutePosition);
        // Move the content chunk (render-offset) or dirty the canvas (rect-mutation fallback).
        ApplyScrollNow();
    }
}

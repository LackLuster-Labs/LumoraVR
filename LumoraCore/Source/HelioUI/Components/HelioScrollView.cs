using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio scroll view component.
/// Scrollable content container with optional scrollbars.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioScrollView : Component, IHelioInteractable
{
    // ===== CONFIGURATION =====

    /// <summary>
    /// Enable horizontal scrolling.
    /// </summary>
    public Sync<bool> Horizontal { get; private set; }

    /// <summary>
    /// Enable vertical scrolling.
    /// </summary>
    public Sync<bool> Vertical { get; private set; }

    /// <summary>
    /// Current scroll position (0-1 normalized).
    /// </summary>
    public Sync<float2> ScrollPosition { get; private set; }

    /// <summary>
    /// Scroll sensitivity multiplier.
    /// </summary>
    public Sync<float> ScrollSensitivity { get; private set; }

    /// <summary>
    /// Enable inertia scrolling.
    /// </summary>
    public Sync<bool> Inertia { get; private set; }

    /// <summary>
    /// Deceleration rate for inertia (0-1).
    /// </summary>
    public Sync<float> DecelerationRate { get; private set; }

    /// <summary>
    /// Whether scrolling is enabled.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    // ===== REFERENCES =====

    /// <summary>
    /// The content slot that will be scrolled.
    /// </summary>
    public SyncRef<Slot> Content { get; private set; }

    /// <summary>
    /// Optional horizontal scrollbar slider.
    /// </summary>
    public SyncRef<HelioSlider> HorizontalScrollbar { get; private set; }

    /// <summary>
    /// Optional vertical scrollbar slider.
    /// </summary>
    public SyncRef<HelioSlider> VerticalScrollbar { get; private set; }

    /// <summary>
    /// Viewport mask slot.
    /// </summary>
    public SyncRef<Slot> Viewport { get; private set; }

    // ===== STATE =====

    private bool _isHovered;
    private bool _isPressed;
    private bool _isDragging;
    private float2 _velocity;
    private float2 _dragStart;
    private float2 _contentSize;
    private float2 _viewportSize;

    public bool IsInteractable => Interactable?.Value ?? true;
    public bool IsHovered => _isHovered;
    public bool IsPressed => _isPressed;

    // ===== IHelioInteractable EVENTS =====

    public event Action<HelioPointerEventData> OnPointerEnter;
    public event Action<HelioPointerEventData> OnPointerExit;
    public event Action<HelioPointerEventData> OnPointerDown;
    public event Action<HelioPointerEventData> OnPointerUp;
    public event Action<HelioPointerEventData> OnPointerClick;

    /// <summary>
    /// Fired when scroll position changes.
    /// </summary>
    public event Action<float2> OnScrollChanged;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        Horizontal = new Sync<bool>(this, false);
        Vertical = new Sync<bool>(this, true);
        ScrollPosition = new Sync<float2>(this, float2.Zero);
        ScrollSensitivity = new Sync<float>(this, 1f);
        Inertia = new Sync<bool>(this, true);
        DecelerationRate = new Sync<float>(this, 0.135f);
        Interactable = new Sync<bool>(this, true);

        Content = new SyncRef<Slot>(this);
        HorizontalScrollbar = new SyncRef<HelioSlider>(this);
        VerticalScrollbar = new SyncRef<HelioSlider>(this);
        Viewport = new SyncRef<Slot>(this);

        // Sync scrollbars with scroll position
        ScrollPosition.OnChanged += _ => UpdateScrollbars();
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Apply inertia
        if (Inertia.Value && !_isDragging && _velocity.LengthSquared > 0.0001f)
        {
            float decel = DecelerationRate.Value;
            _velocity *= (1f - decel);

            var newPos = ScrollPosition.Value + _velocity * delta;
            SetScrollPosition(newPos);

            if (_velocity.LengthSquared < 0.0001f)
                _velocity = float2.Zero;
        }

        // Update content position
        UpdateContentPosition();
    }

    // ===== POINTER HANDLERS =====

    public void HandlePointerEnter(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;
        _isHovered = true;
        OnPointerEnter?.Invoke(eventData);
    }

    public void HandlePointerExit(HelioPointerEventData eventData)
    {
        _isHovered = false;
        if (!_isDragging)
            _isPressed = false;
        OnPointerExit?.Invoke(eventData);
    }

    public void HandlePointerDown(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;
        _isPressed = true;
        _isDragging = true;
        _dragStart = eventData.Position;
        _velocity = float2.Zero;
        OnPointerDown?.Invoke(eventData);
    }

    public void HandlePointerUp(HelioPointerEventData eventData)
    {
        _isPressed = false;
        _isDragging = false;
        OnPointerUp?.Invoke(eventData);
        OnPointerClick?.Invoke(eventData);
    }

    /// <summary>
    /// Handle drag for scrolling.
    /// </summary>
    public void HandlePointerDrag(HelioPointerEventData eventData)
    {
        if (!_isDragging || !IsInteractable) return;

        float2 delta = eventData.Position - _dragStart;
        _dragStart = eventData.Position;

        // Calculate scroll delta based on viewport/content ratio
        float2 scrollDelta = float2.Zero;
        if (Horizontal.Value && _contentSize.x > _viewportSize.x)
        {
            scrollDelta.x = -delta.x / (_contentSize.x - _viewportSize.x) * ScrollSensitivity.Value;
        }
        if (Vertical.Value && _contentSize.y > _viewportSize.y)
        {
            scrollDelta.y = delta.y / (_contentSize.y - _viewportSize.y) * ScrollSensitivity.Value;
        }

        var newPos = ScrollPosition.Value + scrollDelta;
        SetScrollPosition(newPos);

        // Track velocity for inertia
        _velocity = scrollDelta / 0.016f; // Approximate 60fps
    }

    /// <summary>
    /// Handle scroll wheel input.
    /// </summary>
    public void HandleScrollWheel(float delta)
    {
        if (!IsInteractable) return;

        float2 scrollDelta = float2.Zero;
        if (Vertical.Value)
            scrollDelta.y = delta * 0.1f * ScrollSensitivity.Value;
        else if (Horizontal.Value)
            scrollDelta.x = delta * 0.1f * ScrollSensitivity.Value;

        var newPos = ScrollPosition.Value + scrollDelta;
        SetScrollPosition(newPos);
    }

    // ===== SCROLLING =====

    /// <summary>
    /// Set scroll position (clamped to 0-1).
    /// </summary>
    public void SetScrollPosition(float2 position)
    {
        position.x = System.Math.Clamp(position.x, 0f, 1f);
        position.y = System.Math.Clamp(position.y, 0f, 1f);

        if (!Horizontal.Value) position.x = 0f;
        if (!Vertical.Value) position.y = 0f;

        if (!position.Equals(ScrollPosition.Value))
        {
            ScrollPosition.Value = position;
            OnScrollChanged?.Invoke(position);
        }
    }

    /// <summary>
    /// Scroll to show a specific child element.
    /// </summary>
    public void ScrollToChild(Slot child)
    {
        var contentSlot = Content?.Target;
        if (contentSlot == null || child == null) return;

        var childRect = child.GetComponent<HelioRectTransform>();
        var contentRect = contentSlot.GetComponent<HelioRectTransform>();
        if (childRect == null || contentRect == null) return;

        // Calculate normalized position to center the child
        var childCenter = childRect.Rect.Min + childRect.Rect.Size * 0.5f;
        var contentSize = contentRect.Rect.Size;

        float2 targetPos = float2.Zero;
        if (_contentSize.x > _viewportSize.x)
            targetPos.x = childCenter.x / contentSize.x;
        if (_contentSize.y > _viewportSize.y)
            targetPos.y = childCenter.y / contentSize.y;

        SetScrollPosition(targetPos);
    }

    /// <summary>
    /// Scroll to top.
    /// </summary>
    public void ScrollToTop()
    {
        SetScrollPosition(new float2(ScrollPosition.Value.x, 0f));
    }

    /// <summary>
    /// Scroll to bottom.
    /// </summary>
    public void ScrollToBottom()
    {
        SetScrollPosition(new float2(ScrollPosition.Value.x, 1f));
    }

    // ===== INTERNAL =====

    private void UpdateContentPosition()
    {
        var contentSlot = Content?.Target;
        var viewportSlot = Viewport?.Target;
        if (contentSlot == null) return;

        var contentRect = contentSlot.GetComponent<HelioRectTransform>();
        var viewportRect = viewportSlot?.GetComponent<HelioRectTransform>()
            ?? Slot.GetComponent<HelioRectTransform>();

        if (contentRect == null || viewportRect == null) return;

        _contentSize = contentRect.Rect.Size;
        _viewportSize = viewportRect.Rect.Size;

        // Calculate offset based on scroll position
        float2 offset = float2.Zero;
        if (_contentSize.x > _viewportSize.x)
            offset.x = -ScrollPosition.Value.x * (_contentSize.x - _viewportSize.x);
        if (_contentSize.y > _viewportSize.y)
            offset.y = ScrollPosition.Value.y * (_contentSize.y - _viewportSize.y);

        contentRect.OffsetMin.Value = offset;
        contentRect.OffsetMax.Value = offset + _contentSize;
    }

    private void UpdateScrollbars()
    {
        var hBar = HorizontalScrollbar?.Target;
        if (hBar != null)
        {
            hBar.SetNormalizedValue(ScrollPosition.Value.x);
        }

        var vBar = VerticalScrollbar?.Target;
        if (vBar != null)
        {
            vBar.SetNormalizedValue(ScrollPosition.Value.y);
        }
    }

    /// <summary>
    /// Get the scrollable range (content size - viewport size).
    /// </summary>
    public float2 GetScrollableSize()
    {
        return new float2(
            System.Math.Max(0f, _contentSize.x - _viewportSize.x),
            System.Math.Max(0f, _contentSize.y - _viewportSize.y)
        );
    }
}

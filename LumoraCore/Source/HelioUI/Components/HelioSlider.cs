using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Slider direction options.
/// </summary>
public enum SliderDirection
{
    LeftToRight,
    RightToLeft,
    TopToBottom,
    BottomToTop
}

/// <summary>
/// Helio slider component.
/// Value range selection with visual fill and handle.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioSlider : Component, IHelioInteractable
{
    // ===== VALUE =====

    /// <summary>
    /// Current slider value.
    /// </summary>
    public Sync<float> Value { get; private set; }

    /// <summary>
    /// Minimum value.
    /// </summary>
    public Sync<float> MinValue { get; private set; }

    /// <summary>
    /// Maximum value.
    /// </summary>
    public Sync<float> MaxValue { get; private set; }

    /// <summary>
    /// Whether to snap to whole numbers.
    /// </summary>
    public Sync<bool> WholeNumbers { get; private set; }

    /// <summary>
    /// Slide direction.
    /// </summary>
    public Sync<SliderDirection> Direction { get; private set; }

    // ===== VISUAL PROPERTIES =====

    /// <summary>
    /// Whether the slider accepts input.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    /// <summary>
    /// Reference to the fill image (progress indicator).
    /// </summary>
    public SyncRef<HelioImage> FillImage { get; private set; }

    /// <summary>
    /// Reference to the handle image.
    /// </summary>
    public SyncRef<HelioImage> HandleImage { get; private set; }

    /// <summary>
    /// Reference to the background track.
    /// </summary>
    public SyncRef<HelioPanel> Track { get; private set; }

    // ===== EVENTS =====

    /// <summary>
    /// Invoked when the value changes with the new value.
    /// </summary>
    public SyncDelegate<Action<float>> OnValueChanged { get; private set; }

    // ===== INTERACTION STATE =====

    private bool _isHovered;
    private bool _isPressed;
    private bool _isDragging;

    public bool IsInteractable => Interactable?.Value ?? true;
    public bool IsHovered => _isHovered;
    public bool IsPressed => _isPressed;

    // ===== IHelioInteractable EVENTS =====

    public event Action<HelioPointerEventData> OnPointerEnter;
    public event Action<HelioPointerEventData> OnPointerExit;
    public event Action<HelioPointerEventData> OnPointerDown;
    public event Action<HelioPointerEventData> OnPointerUp;
    public event Action<HelioPointerEventData> OnPointerClick;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        Value = new Sync<float>(this, 0f);
        MinValue = new Sync<float>(this, 0f);
        MaxValue = new Sync<float>(this, 1f);
        WholeNumbers = new Sync<bool>(this, false);
        Direction = new Sync<SliderDirection>(this, SliderDirection.LeftToRight);
        Interactable = new Sync<bool>(this, true);

        FillImage = new SyncRef<HelioImage>(this);
        HandleImage = new SyncRef<HelioImage>(this);
        Track = new SyncRef<HelioPanel>(this);
        OnValueChanged = new SyncDelegate<Action<float>>(this);

        // Update visuals when value changes
        Value.OnChanged += _ =>
        {
            UpdateVisuals();
            OnValueChanged?.Invoke(Value.Value);
        };
        MinValue.OnChanged += _ => UpdateVisuals();
        MaxValue.OnChanged += _ => UpdateVisuals();
    }

    // ===== POINTER EVENT HANDLERS =====

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
        UpdateValueFromPointer(eventData.Position);
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
    /// Called during drag to update value based on pointer position.
    /// </summary>
    public void HandlePointerDrag(HelioPointerEventData eventData)
    {
        if (!_isDragging || !IsInteractable) return;
        UpdateValueFromPointer(eventData.Position);
    }

    // ===== VALUE CALCULATION =====

    private void UpdateValueFromPointer(float2 canvasPosition)
    {
        // Get our rect transform
        var rect = Slot.GetComponent<HelioRectTransform>();
        if (rect == null) return;

        var bounds = rect.Rect;
        float normalizedValue = 0f;

        switch (Direction.Value)
        {
            case SliderDirection.LeftToRight:
                normalizedValue = (canvasPosition.x - bounds.Min.x) / bounds.Size.x;
                break;
            case SliderDirection.RightToLeft:
                normalizedValue = 1f - (canvasPosition.x - bounds.Min.x) / bounds.Size.x;
                break;
            case SliderDirection.BottomToTop:
                normalizedValue = (canvasPosition.y - bounds.Min.y) / bounds.Size.y;
                break;
            case SliderDirection.TopToBottom:
                normalizedValue = 1f - (canvasPosition.y - bounds.Min.y) / bounds.Size.y;
                break;
        }

        normalizedValue = System.Math.Clamp(normalizedValue, 0f, 1f);
        float newValue = MinValue.Value + normalizedValue * (MaxValue.Value - MinValue.Value);

        if (WholeNumbers.Value)
        {
            newValue = (float)System.Math.Round(newValue);
        }

        Value.Value = System.Math.Clamp(newValue, MinValue.Value, MaxValue.Value);
    }

    /// <summary>
    /// Get the normalized value (0-1).
    /// </summary>
    public float GetNormalizedValue()
    {
        float range = MaxValue.Value - MinValue.Value;
        if (range < 0.0001f) return 0f;
        return (Value.Value - MinValue.Value) / range;
    }

    // ===== VISUALS =====

    private void UpdateVisuals()
    {
        float normalized = GetNormalizedValue();

        // Update fill if assigned (scale based on direction)
        // This would typically be done by the rendering hook

        // Update handle position
        // This would typically be done by the rendering hook
    }

    /// <summary>
    /// Set the value programmatically.
    /// </summary>
    public void SetValue(float value)
    {
        if (WholeNumbers.Value)
        {
            value = (float)System.Math.Round(value);
        }
        Value.Value = System.Math.Clamp(value, MinValue.Value, MaxValue.Value);
    }

    /// <summary>
    /// Set the value as a normalized 0-1 value.
    /// </summary>
    public void SetNormalizedValue(float normalized)
    {
        normalized = System.Math.Clamp(normalized, 0f, 1f);
        SetValue(MinValue.Value + normalized * (MaxValue.Value - MinValue.Value));
    }
}

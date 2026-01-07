using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio toggle (checkbox) component.
/// Boolean toggle control with visual checkmark indicator.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioToggle : Component, IHelioInteractable
{
    // ===== VALUE =====

    /// <summary>
    /// Current toggle state.
    /// </summary>
    public Sync<bool> Value { get; private set; }

    // ===== VISUAL PROPERTIES =====

    /// <summary>
    /// Whether the toggle accepts input.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    /// <summary>
    /// Color when idle and unchecked.
    /// </summary>
    public Sync<color> NormalColor { get; private set; }

    /// <summary>
    /// Color when hovered.
    /// </summary>
    public Sync<color> HoveredColor { get; private set; }

    /// <summary>
    /// Color when checked.
    /// </summary>
    public Sync<color> CheckedColor { get; private set; }

    /// <summary>
    /// Color when disabled.
    /// </summary>
    public Sync<color> DisabledColor { get; private set; }

    /// <summary>
    /// Reference to the checkmark image.
    /// </summary>
    public SyncRef<HelioImage> Checkmark { get; private set; }

    /// <summary>
    /// Reference to a text label.
    /// </summary>
    public SyncRef<HelioText> Label { get; private set; }

    /// <summary>
    /// Reference to background panel.
    /// </summary>
    public SyncRef<HelioPanel> Background { get; private set; }

    // ===== EVENTS =====

    /// <summary>
    /// Invoked when the value changes with the new value.
    /// </summary>
    public SyncDelegate<Action<bool>> OnValueChanged { get; private set; }

    // ===== INTERACTION STATE =====

    private bool _isHovered;
    private bool _isPressed;

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

        Value = new Sync<bool>(this, false);
        Interactable = new Sync<bool>(this, true);
        NormalColor = new Sync<color>(this, new color(0.3f, 0.3f, 0.3f, 1f));
        HoveredColor = new Sync<color>(this, new color(0.4f, 0.4f, 0.4f, 1f));
        CheckedColor = new Sync<color>(this, new color(0.2f, 0.6f, 1f, 1f));
        DisabledColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.2f, 0.5f));

        Checkmark = new SyncRef<HelioImage>(this);
        Label = new SyncRef<HelioText>(this);
        Background = new SyncRef<HelioPanel>(this);
        OnValueChanged = new SyncDelegate<Action<bool>>(this);

        // Update visuals when value changes
        Value.OnChanged += _ =>
        {
            UpdateVisuals();
            OnValueChanged?.Invoke(Value.Value);
        };
        Interactable.OnChanged += _ => UpdateVisuals();
    }

    // ===== POINTER EVENT HANDLERS =====

    public void HandlePointerEnter(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;
        _isHovered = true;
        UpdateVisuals();
        OnPointerEnter?.Invoke(eventData);
    }

    public void HandlePointerExit(HelioPointerEventData eventData)
    {
        _isHovered = false;
        _isPressed = false;
        UpdateVisuals();
        OnPointerExit?.Invoke(eventData);
    }

    public void HandlePointerDown(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;
        _isPressed = true;
        UpdateVisuals();
        OnPointerDown?.Invoke(eventData);
    }

    public void HandlePointerUp(HelioPointerEventData eventData)
    {
        bool wasPressed = _isPressed;
        _isPressed = false;
        UpdateVisuals();
        OnPointerUp?.Invoke(eventData);

        // Toggle on click
        if (wasPressed && _isHovered && IsInteractable)
        {
            Value.Value = !Value.Value;
            OnPointerClick?.Invoke(eventData);
        }
    }

    // ===== VISUALS =====

    private void UpdateVisuals()
    {
        // Update checkmark visibility
        var checkmark = Checkmark?.Target;
        if (checkmark != null)
        {
            // Show/hide by setting alpha
            var tint = checkmark.Tint.Value;
            tint.a = Value.Value ? 1f : 0f;
            checkmark.Tint.Value = tint;
        }

        // Update background color
        var background = Background?.Target;
        if (background != null)
        {
            color bgColor;
            if (!IsInteractable)
                bgColor = DisabledColor?.Value ?? new color(0.2f, 0.2f, 0.2f, 0.5f);
            else if (Value.Value)
                bgColor = CheckedColor?.Value ?? new color(0.2f, 0.6f, 1f, 1f);
            else if (_isHovered)
                bgColor = HoveredColor?.Value ?? new color(0.4f, 0.4f, 0.4f, 1f);
            else
                bgColor = NormalColor?.Value ?? new color(0.3f, 0.3f, 0.3f, 1f);

            background.BackgroundColor.Value = bgColor;
        }
    }

    /// <summary>
    /// Set the toggle value programmatically.
    /// </summary>
    public void SetValue(bool value)
    {
        Value.Value = value;
    }

    /// <summary>
    /// Toggle the current value.
    /// </summary>
    public void Toggle()
    {
        Value.Value = !Value.Value;
    }
}

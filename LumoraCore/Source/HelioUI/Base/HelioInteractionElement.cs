using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for interactive HelioUI elements (buttons, sliders, checkboxes, etc.).
/// Provides common interaction state and color driving functionality.
/// </summary>
[ComponentCategory("HelioUI")]
public abstract class HelioInteractionElement : HelioUIController, IHelioInteractable
{
    // ===== INTERACTION SETTINGS =====

    /// <summary>
    /// Whether this element accepts input.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    /// <summary>
    /// Whether to require lock-in for interaction (prevents accidental touches).
    /// </summary>
    public Sync<bool> RequireLockIn { get; private set; }

    /// <summary>
    /// Lock state for touch exit (prevents accidental exit).
    /// </summary>
    public Sync<bool> TouchExitLock { get; private set; }

    /// <summary>
    /// Lock state for touch enter.
    /// </summary>
    public Sync<bool> TouchEnterLock { get; private set; }

    // ===== COLOR DRIVING =====

    /// <summary>
    /// Normal/idle color.
    /// </summary>
    public Sync<color> NormalColor { get; private set; }

    /// <summary>
    /// Color when hovered.
    /// </summary>
    public Sync<color> HighlightColor { get; private set; }

    /// <summary>
    /// Color when pressed.
    /// </summary>
    public Sync<color> PressColor { get; private set; }

    /// <summary>
    /// Color when disabled.
    /// </summary>
    public Sync<color> DisabledColor { get; private set; }

    /// <summary>
    /// Duration of color transitions.
    /// </summary>
    public Sync<float> ColorTransitionDuration { get; private set; }

    // ===== VIBRATION =====

    /// <summary>
    /// Vibration intensity on hover (0-1).
    /// </summary>
    public Sync<float> HoverVibrate { get; private set; }

    /// <summary>
    /// Vibration intensity on press (0-1).
    /// </summary>
    public Sync<float> PressVibrate { get; private set; }

    // ===== INTERACTION STATE =====

    protected bool _isHovered;
    protected bool _isPressed;
    protected color _currentColor;
    protected color _targetColor;
    protected float _colorTransitionProgress = 1f;

    public bool IsInteractable => Interactable?.Value ?? true;
    public bool IsHovered => _isHovered;
    public bool IsPressed => _isPressed;

    // ===== EVENTS =====

    public event Action<HelioPointerEventData> OnPointerEnter;
    public event Action<HelioPointerEventData> OnPointerExit;
    public event Action<HelioPointerEventData> OnPointerDown;
    public event Action<HelioPointerEventData> OnPointerUp;
    public event Action<HelioPointerEventData> OnPointerClick;

    /// <summary>
    /// Invoked while hovering (each frame).
    /// </summary>
    public event Action<HelioPointerEventData> OnPointerStay;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        // Interaction settings
        Interactable = new Sync<bool>(this, true);
        RequireLockIn = new Sync<bool>(this, false);
        TouchExitLock = new Sync<bool>(this, false);
        TouchEnterLock = new Sync<bool>(this, false);

        // Colors
        NormalColor = new Sync<color>(this, new color(0.3f, 0.3f, 0.3f, 1f));
        HighlightColor = new Sync<color>(this, new color(0.4f, 0.4f, 0.4f, 1f));
        PressColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.2f, 1f));
        DisabledColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.2f, 0.5f));
        ColorTransitionDuration = new Sync<float>(this, 0.1f);

        // Vibration
        HoverVibrate = new Sync<float>(this, 0.1f);
        PressVibrate = new Sync<float>(this, 0.3f);

        // Initial color
        _currentColor = NormalColor.Value;
        _targetColor = NormalColor.Value;

        // React to interactable changes
        Interactable.OnChanged += _ => UpdateVisualState();
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Color transition
        if (_colorTransitionProgress < 1f)
        {
            float duration = ColorTransitionDuration?.Value ?? 0.1f;
            if (duration > 0f)
            {
                _colorTransitionProgress += delta / duration;
                if (_colorTransitionProgress >= 1f)
                    _colorTransitionProgress = 1f;

                _currentColor = color.Lerp(_currentColor, _targetColor, _colorTransitionProgress);
                ApplyColor(_currentColor);
            }
        }
    }

    // ===== POINTER HANDLERS =====

    /// <summary>
    /// Process interaction event. Override to handle specific interactions.
    /// </summary>
    public virtual void ProcessEvent(HelioPointerEventData eventData)
    {
        // Default implementation routes to appropriate handler
    }

    public virtual void HandlePointerEnter(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        _isHovered = true;
        UpdateVisualState();

        // Vibration feedback
        if (HoverVibrate.Value > 0f)
        {
            // TODO: Trigger haptic feedback
        }

        OnPointerEnter?.Invoke(eventData);
    }

    public virtual void HandlePointerExit(HelioPointerEventData eventData)
    {
        _isHovered = false;
        _isPressed = false;
        UpdateVisualState();
        OnPointerExit?.Invoke(eventData);
    }

    public virtual void HandlePointerDown(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        _isPressed = true;
        UpdateVisualState();

        // Vibration feedback
        if (PressVibrate.Value > 0f)
        {
            // TODO: Trigger haptic feedback
        }

        OnPointerDown?.Invoke(eventData);
    }

    public virtual void HandlePointerUp(HelioPointerEventData eventData)
    {
        bool wasPressed = _isPressed;
        _isPressed = false;
        UpdateVisualState();
        OnPointerUp?.Invoke(eventData);

        // Fire click if released while hovered and was pressed
        if (wasPressed && _isHovered && IsInteractable)
        {
            HandleClick(eventData);
            OnPointerClick?.Invoke(eventData);
        }
    }

    public virtual void HandlePointerStay(HelioPointerEventData eventData)
    {
        OnPointerStay?.Invoke(eventData);
    }

    /// <summary>
    /// Called when a click is completed. Override for click behavior.
    /// </summary>
    protected virtual void HandleClick(HelioPointerEventData eventData)
    {
    }

    // ===== VISUAL STATE =====

    /// <summary>
    /// Update visual state based on current interaction state.
    /// </summary>
    protected virtual void UpdateVisualState()
    {
        color newColor;

        if (!IsInteractable)
            newColor = DisabledColor?.Value ?? new color(0.2f, 0.2f, 0.2f, 0.5f);
        else if (_isPressed)
            newColor = PressColor?.Value ?? new color(0.2f, 0.2f, 0.2f, 1f);
        else if (_isHovered)
            newColor = HighlightColor?.Value ?? new color(0.4f, 0.4f, 0.4f, 1f);
        else
            newColor = NormalColor?.Value ?? new color(0.3f, 0.3f, 0.3f, 1f);

        if (!newColor.Equals(_targetColor))
        {
            _targetColor = newColor;
            _colorTransitionProgress = 0f;
        }
    }

    /// <summary>
    /// Apply color to visual elements. Override to target specific graphics.
    /// </summary>
    protected virtual void ApplyColor(color c)
    {
        // Override in derived classes to apply to specific targets
    }

    /// <summary>
    /// Get the current display color.
    /// </summary>
    public color GetCurrentColor() => _currentColor;
}

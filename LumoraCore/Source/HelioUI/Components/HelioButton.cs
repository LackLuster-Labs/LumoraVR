using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio button component.
/// Clickable button with visual states and event callbacks.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioButton : Component, IHelioInteractable
{
    // ===== VISUAL PROPERTIES =====

    /// <summary>
    /// Whether the button accepts input.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    /// <summary>
    /// Color when idle.
    /// </summary>
    public Sync<color> NormalColor { get; private set; }

    /// <summary>
    /// Color when hovered.
    /// </summary>
    public Sync<color> HoveredColor { get; private set; }

    /// <summary>
    /// Color when pressed.
    /// </summary>
    public Sync<color> PressedColor { get; private set; }

    /// <summary>
    /// Color when disabled (Interactable = false).
    /// </summary>
    public Sync<color> DisabledColor { get; private set; }

    /// <summary>
    /// Duration of color transitions in seconds.
    /// </summary>
    public Sync<float> TransitionDuration { get; private set; }

    /// <summary>
    /// Reference to a HelioText for the button label.
    /// </summary>
    public SyncRef<HelioText> Label { get; private set; }

    /// <summary>
    /// Reference to a HelioPanel for the button background.
    /// </summary>
    public SyncRef<HelioPanel> Background { get; private set; }

    // ===== EVENTS =====

    /// <summary>
    /// Invoked when the button is clicked.
    /// </summary>
    public SyncDelegate<Action> OnClick { get; private set; }

    // ===== INTERACTION STATE =====

    private bool _isHovered;
    private bool _isPressed;
    private float _transitionProgress;
    private color _currentColor;
    private color _targetColor;

    /// <summary>
    /// Whether this button can be interacted with.
    /// </summary>
    public bool IsInteractable => Interactable?.Value ?? true;

    /// <summary>
    /// Whether a pointer is currently hovering over this button.
    /// </summary>
    public bool IsHovered => _isHovered;

    /// <summary>
    /// Whether this button is currently being pressed.
    /// </summary>
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

        // Initialize sync fields
        Interactable = new Sync<bool>(this, true);
        NormalColor = new Sync<color>(this, HelioUITheme.ButtonNormal);
        HoveredColor = new Sync<color>(this, HelioUITheme.ButtonHovered);
        PressedColor = new Sync<color>(this, HelioUITheme.ButtonPressed);
        DisabledColor = new Sync<color>(this, HelioUITheme.ButtonDisabled);
        TransitionDuration = new Sync<float>(this, 0.1f);

        Label = new SyncRef<HelioText>(this);
        Background = new SyncRef<HelioPanel>(this);
        OnClick = new SyncDelegate<Action>(this);

        _currentColor = NormalColor.Value;
        _targetColor = NormalColor.Value;

        // React to interactable changes
        Interactable.OnChanged += _ => UpdateVisualState();
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Animate color transition
        if (_transitionProgress < 1f)
        {
            float duration = TransitionDuration?.Value ?? 0.1f;
            if (duration > 0f)
            {
                _transitionProgress += delta / duration;
                if (_transitionProgress >= 1f)
                    _transitionProgress = 1f;

                _currentColor = color.Lerp(_currentColor, _targetColor, _transitionProgress);
                ApplyColorToBackground();
            }
        }
    }

    // ===== POINTER EVENT HANDLERS =====

    /// <summary>
    /// Called by input system when pointer enters.
    /// </summary>
    public void HandlePointerEnter(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        _isHovered = true;
        UpdateVisualState();
        OnPointerEnter?.Invoke(eventData);
    }

    /// <summary>
    /// Called by input system when pointer exits.
    /// </summary>
    public void HandlePointerExit(HelioPointerEventData eventData)
    {
        _isHovered = false;
        _isPressed = false;
        UpdateVisualState();
        OnPointerExit?.Invoke(eventData);
    }

    /// <summary>
    /// Called by input system when pointer is pressed.
    /// </summary>
    public void HandlePointerDown(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        _isPressed = true;
        UpdateVisualState();
        OnPointerDown?.Invoke(eventData);
    }

    /// <summary>
    /// Called by input system when pointer is released.
    /// </summary>
    public void HandlePointerUp(HelioPointerEventData eventData)
    {
        bool wasPressed = _isPressed;
        _isPressed = false;
        UpdateVisualState();
        OnPointerUp?.Invoke(eventData);

        // Click if released while hovered and was pressed
        if (wasPressed && _isHovered && IsInteractable)
        {
            OnPointerClick?.Invoke(eventData);
            OnClick?.Invoke();
        }
    }

    // ===== VISUAL STATE =====

    private void UpdateVisualState()
    {
        color newColor;

        if (!IsInteractable)
            newColor = DisabledColor?.Value ?? new color(0.2f, 0.2f, 0.2f, 0.5f);
        else if (_isPressed)
            newColor = PressedColor?.Value ?? new color(0.2f, 0.2f, 0.2f, 1f);
        else if (_isHovered)
            newColor = HoveredColor?.Value ?? new color(0.4f, 0.4f, 0.4f, 1f);
        else
            newColor = NormalColor?.Value ?? new color(0.3f, 0.3f, 0.3f, 1f);

        if (!newColor.Equals(_targetColor))
        {
            _targetColor = newColor;
            _transitionProgress = 0f;
        }
    }

    private void ApplyColorToBackground()
    {
        var panel = Background?.Target;
        if (panel != null)
        {
            panel.BackgroundColor.Value = _currentColor;
        }
    }

    /// <summary>
    /// Set the button label text.
    /// </summary>
    public void SetLabel(string text)
    {
        var label = Label?.Target;
        if (label != null)
        {
            label.Content.Value = text;
        }
    }

    /// <summary>
    /// Get the current display color (accounting for transition).
    /// </summary>
    public color GetCurrentColor() => _currentColor;
}

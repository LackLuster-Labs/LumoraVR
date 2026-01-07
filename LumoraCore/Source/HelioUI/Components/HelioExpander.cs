using System;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio expander component.
/// Expandable/collapsible section with header and content areas.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioExpander : Component, IHelioInteractable
{
    // ===== STATE =====

    /// <summary>
    /// Whether the expander is currently expanded.
    /// </summary>
    public Sync<bool> IsExpanded { get; private set; }

    // ===== SLOT REFERENCES =====

    /// <summary>
    /// Reference to the header slot (always visible).
    /// </summary>
    public SyncRef<Slot> HeaderSlot { get; private set; }

    /// <summary>
    /// Reference to the content slot (shown/hidden based on IsExpanded).
    /// </summary>
    public SyncRef<Slot> ContentSlot { get; private set; }

    // ===== ANIMATION PROPERTIES =====

    /// <summary>
    /// Duration of expand/collapse animation in seconds.
    /// Set to 0 for instant toggle.
    /// </summary>
    public Sync<float> AnimationDuration { get; private set; }

    // ===== INTERACTION PROPERTIES =====

    /// <summary>
    /// Whether the expander accepts input.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    // ===== EVENTS =====

    /// <summary>
    /// Invoked when the expander is expanded.
    /// </summary>
    public SyncDelegate<Action> OnExpanded { get; private set; }

    /// <summary>
    /// Invoked when the expander is collapsed.
    /// </summary>
    public SyncDelegate<Action> OnCollapsed { get; private set; }

    // ===== INTERACTION STATE =====

    private bool _isHovered;
    private bool _isPressed;
    private float _animationProgress = 1f; // 0 = collapsed, 1 = expanded
    private bool _targetExpanded;

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

        // Initialize sync fields
        IsExpanded = new Sync<bool>(this, false);
        Interactable = new Sync<bool>(this, true);
        AnimationDuration = new Sync<float>(this, 0.2f);

        HeaderSlot = new SyncRef<Slot>(this);
        ContentSlot = new SyncRef<Slot>(this);

        OnExpanded = new SyncDelegate<Action>(this);
        OnCollapsed = new SyncDelegate<Action>(this);

        // Initialize animation state
        _targetExpanded = IsExpanded.Value;
        _animationProgress = IsExpanded.Value ? 1f : 0f;

        // React to expansion state changes
        IsExpanded.OnChanged += OnExpandedChanged;

        // Initialize content visibility
        UpdateContentVisibility();
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Animate expansion/collapse
        if (_animationProgress < 1f)
        {
            float duration = AnimationDuration?.Value ?? 0.2f;
            if (duration > 0f)
            {
                _animationProgress += delta / duration;
                if (_animationProgress >= 1f)
                {
                    _animationProgress = 1f;
                    UpdateContentVisibility();
                }
                else
                {
                    UpdateContentVisibility();
                }
            }
            else
            {
                // Instant toggle
                _animationProgress = 1f;
                UpdateContentVisibility();
            }
        }
    }

    // ===== EXPANSION STATE =====

    private void OnExpandedChanged(bool newValue)
    {
        bool newState = newValue;
        if (_targetExpanded != newState)
        {
            _targetExpanded = newState;
            _animationProgress = 0f;

            // Fire events
            if (newState)
                OnExpanded?.Invoke();
            else
                OnCollapsed?.Invoke();
        }
    }

    private void UpdateContentVisibility()
    {
        var content = ContentSlot?.Target;
        if (content == null) return;

        // Calculate visibility based on animation progress
        bool shouldBeVisible;
        if (AnimationDuration?.Value > 0f && _animationProgress < 1f)
        {
            // During animation, keep visible but potentially scaled/faded
            shouldBeVisible = true;
        }
        else
        {
            // After animation, set final state
            shouldBeVisible = _targetExpanded;
        }

        // Update content slot active state
        content.ActiveSelf.Value = shouldBeVisible;
    }

    // ===== POINTER EVENT HANDLERS =====

    /// <summary>
    /// Called by input system when pointer enters the header.
    /// </summary>
    public void HandlePointerEnter(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        _isHovered = true;
        OnPointerEnter?.Invoke(eventData);
    }

    /// <summary>
    /// Called by input system when pointer exits the header.
    /// </summary>
    public void HandlePointerExit(HelioPointerEventData eventData)
    {
        _isHovered = false;
        _isPressed = false;
        OnPointerExit?.Invoke(eventData);
    }

    /// <summary>
    /// Called by input system when pointer is pressed on the header.
    /// </summary>
    public void HandlePointerDown(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        _isPressed = true;
        OnPointerDown?.Invoke(eventData);
    }

    /// <summary>
    /// Called by input system when pointer is released on the header.
    /// </summary>
    public void HandlePointerUp(HelioPointerEventData eventData)
    {
        bool wasPressed = _isPressed;
        _isPressed = false;
        OnPointerUp?.Invoke(eventData);

        // Toggle if clicked
        if (wasPressed && _isHovered && IsInteractable)
        {
            Toggle();
            OnPointerClick?.Invoke(eventData);
        }
    }

    // ===== PUBLIC METHODS =====

    /// <summary>
    /// Toggle the expanded/collapsed state.
    /// </summary>
    public void Toggle()
    {
        IsExpanded.Value = !IsExpanded.Value;
    }

    /// <summary>
    /// Set the expanded state directly.
    /// </summary>
    public void SetExpanded(bool expanded)
    {
        IsExpanded.Value = expanded;
    }

    /// <summary>
    /// Expand the content section.
    /// </summary>
    public void Expand()
    {
        IsExpanded.Value = true;
    }

    /// <summary>
    /// Collapse the content section.
    /// </summary>
    public void Collapse()
    {
        IsExpanded.Value = false;
    }

    /// <summary>
    /// Get the current animation progress (0 = collapsed, 1 = expanded).
    /// </summary>
    public float GetAnimationProgress()
    {
        return _targetExpanded ? _animationProgress : (1f - _animationProgress);
    }
}

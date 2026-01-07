using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio checkbox component for boolean value selection.
/// </summary>
[ComponentCategory("HelioUI/Interaction")]
public class HelioCheckbox : Component, IHelioInteractable
{
    /// <summary>
    /// Internal state of the checkbox.
    /// </summary>
    public Sync<bool> State { get; private set; }

    /// <summary>
    /// Optional target field to drive.
    /// </summary>
    public SyncRef<Sync<bool>> TargetState { get; private set; }

    /// <summary>
    /// Reference to the check mark visual.
    /// </summary>
    public SyncRef<Slot> CheckVisual { get; private set; }

    /// <summary>
    /// Whether the checkbox is interactable.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    /// <summary>
    /// Background color when unchecked.
    /// </summary>
    public Sync<color> UncheckedColor { get; private set; }

    /// <summary>
    /// Background color when checked.
    /// </summary>
    public Sync<color> CheckedColor { get; private set; }

    /// <summary>
    /// Check mark color.
    /// </summary>
    public Sync<color> CheckMarkColor { get; private set; }

    /// <summary>
    /// Event fired when state changes.
    /// </summary>
    public event Action<bool> OnStateChanged;

    // Interaction state
    private bool _isHovered;
    private bool _isPressed;

    public bool IsInteractable => Interactable?.Value ?? true;
    public bool IsHovered => _isHovered;
    public bool IsPressed => _isPressed;

    // IHelioInteractable events
    public event Action<HelioPointerEventData> OnPointerEnter;
    public event Action<HelioPointerEventData> OnPointerExit;
    public event Action<HelioPointerEventData> OnPointerDown;
    public event Action<HelioPointerEventData> OnPointerUp;
    public event Action<HelioPointerEventData> OnPointerClick;

    /// <summary>
    /// Get or set the checked state.
    /// </summary>
    public bool IsChecked
    {
        get => TargetState.Target?.Value ?? State.Value;
        set
        {
            if (TargetState.Target != null)
            {
                TargetState.Target.Value = value;
            }
            else
            {
                State.Value = value;
            }
        }
    }

    public override void OnAwake()
    {
        base.OnAwake();

        State = new Sync<bool>(this, false);
        TargetState = new SyncRef<Sync<bool>>(this);
        CheckVisual = new SyncRef<Slot>(this);
        Interactable = new Sync<bool>(this, true);
        UncheckedColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.2f, 1f));
        CheckedColor = new Sync<color>(this, new color(0.3f, 0.6f, 1f, 1f));
        CheckMarkColor = new Sync<color>(this, new color(1f, 1f, 1f, 1f));

        State.OnChanged += _ => UpdateVisuals();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Sync with target state if bound
        if (TargetState.Target != null)
        {
            UpdateVisuals();
        }
    }

    /// <summary>
    /// Toggle the checkbox state.
    /// </summary>
    public void Toggle()
    {
        IsChecked = !IsChecked;
        OnStateChanged?.Invoke(IsChecked);
    }

    private void UpdateVisuals()
    {
        bool isChecked = IsChecked;

        // Update background panel color
        var panel = Slot.GetComponent<HelioPanel>();
        if (panel != null)
        {
            panel.BackgroundColor.Value = isChecked ? CheckedColor.Value : UncheckedColor.Value;
        }

        // Update check visual visibility
        var checkSlot = CheckVisual.Target;
        if (checkSlot != null)
        {
            checkSlot.ActiveSelf.Value = isChecked;
        }
    }

    // IHelioInteractable implementation
    public void HandlePointerEnter(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;
        _isHovered = true;
        OnPointerEnter?.Invoke(eventData);
    }

    public void HandlePointerExit(HelioPointerEventData eventData)
    {
        _isHovered = false;
        _isPressed = false;
        OnPointerExit?.Invoke(eventData);
    }

    public void HandlePointerDown(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;
        _isPressed = true;
        OnPointerDown?.Invoke(eventData);
    }

    public void HandlePointerUp(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        if (_isPressed && _isHovered)
        {
            Toggle();
            OnPointerClick?.Invoke(eventData);
        }

        _isPressed = false;
        OnPointerUp?.Invoke(eventData);
    }

    /// <summary>
    /// Setup the checkbox visuals.
    /// </summary>
    public void SetupVisuals(float size = 24f)
    {
        // Add background panel
        var panel = Slot.GetComponent<HelioPanel>() ?? Slot.AttachComponent<HelioPanel>();
        panel.BackgroundColor.Value = UncheckedColor.Value;

        // Create check mark slot
        var checkSlot = Slot.AddSlot("CheckMark");
        var checkRect = checkSlot.AttachComponent<HelioRectTransform>();
        checkRect.AnchorMin.Value = new float2(0.2f, 0.2f);
        checkRect.AnchorMax.Value = new float2(0.8f, 0.8f);

        var checkPanel = checkSlot.AttachComponent<HelioPanel>();
        checkPanel.BackgroundColor.Value = CheckMarkColor.Value;

        CheckVisual.Target = checkSlot;
        checkSlot.ActiveSelf.Value = false;

        UpdateVisuals();
    }
}

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio dropdown component.
/// Selection from a list of options with expandable menu.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioDropdown : Component, IHelioInteractable
{
    // ===== VALUE =====

    /// <summary>
    /// Currently selected option index.
    /// </summary>
    public Sync<int> SelectedIndex { get; private set; }

    /// <summary>
    /// List of available options.
    /// </summary>
    public SyncList<string> Options { get; private set; }

    // ===== PROPERTIES =====

    /// <summary>
    /// Whether the dropdown accepts input.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    /// <summary>
    /// Whether the dropdown menu is currently open.
    /// </summary>
    public Sync<bool> IsExpanded { get; private set; }

    /// <summary>
    /// Reference to the label showing current selection.
    /// </summary>
    public SyncRef<HelioText> LabelText { get; private set; }

    /// <summary>
    /// Reference to the dropdown arrow image.
    /// </summary>
    public SyncRef<HelioImage> ArrowImage { get; private set; }

    /// <summary>
    /// Reference to the background panel.
    /// </summary>
    public SyncRef<HelioPanel> Background { get; private set; }

    /// <summary>
    /// Reference to the dropdown content slot (menu container).
    /// </summary>
    public SyncRef<Slot> DropdownContent { get; private set; }

    /// <summary>
    /// Color when idle.
    /// </summary>
    public Sync<color> NormalColor { get; private set; }

    /// <summary>
    /// Color when hovered.
    /// </summary>
    public Sync<color> HoveredColor { get; private set; }

    /// <summary>
    /// Color when expanded.
    /// </summary>
    public Sync<color> ExpandedColor { get; private set; }

    // ===== EVENTS =====

    /// <summary>
    /// Invoked when selection changes with the new index.
    /// </summary>
    public SyncDelegate<Action<int>> OnValueChanged { get; private set; }

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

        SelectedIndex = new Sync<int>(this, 0);
        Options = new SyncList<string>(this);
        Interactable = new Sync<bool>(this, true);
        IsExpanded = new Sync<bool>(this, false);

        LabelText = new SyncRef<HelioText>(this);
        ArrowImage = new SyncRef<HelioImage>(this);
        Background = new SyncRef<HelioPanel>(this);
        DropdownContent = new SyncRef<Slot>(this);

        NormalColor = new Sync<color>(this, new color(0.25f, 0.25f, 0.25f, 1f));
        HoveredColor = new Sync<color>(this, new color(0.35f, 0.35f, 0.35f, 1f));
        ExpandedColor = new Sync<color>(this, new color(0.3f, 0.3f, 0.35f, 1f));

        OnValueChanged = new SyncDelegate<Action<int>>(this);

        // Update display when selection changes
        SelectedIndex.OnChanged += _ =>
        {
            UpdateDisplay();
            OnValueChanged?.Invoke(SelectedIndex.Value);
        };
        IsExpanded.OnChanged += _ => UpdateDropdownVisibility();
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
        OnPointerDown?.Invoke(eventData);
    }

    public void HandlePointerUp(HelioPointerEventData eventData)
    {
        bool wasPressed = _isPressed;
        _isPressed = false;
        OnPointerUp?.Invoke(eventData);

        // Toggle dropdown on click
        if (wasPressed && _isHovered && IsInteractable)
        {
            IsExpanded.Value = !IsExpanded.Value;
            OnPointerClick?.Invoke(eventData);
        }
    }

    // ===== SELECTION =====

    /// <summary>
    /// Select an option by index.
    /// </summary>
    public void Select(int index)
    {
        if (index >= 0 && index < Options.Count)
        {
            SelectedIndex.Value = index;
            IsExpanded.Value = false;
        }
    }

    /// <summary>
    /// Get the currently selected option text.
    /// </summary>
    public string GetSelectedOption()
    {
        int idx = SelectedIndex.Value;
        if (idx >= 0 && idx < Options.Count)
            return Options[idx];
        return "";
    }

    /// <summary>
    /// Add an option to the dropdown.
    /// </summary>
    public void AddOption(string option)
    {
        Options.Add(option);
        UpdateDisplay();
    }

    /// <summary>
    /// Clear all options.
    /// </summary>
    public void ClearOptions()
    {
        Options.Clear();
        SelectedIndex.Value = 0;
        UpdateDisplay();
    }

    /// <summary>
    /// Set options from an array.
    /// </summary>
    public void SetOptions(IEnumerable<string> options)
    {
        Options.Clear();
        foreach (var opt in options)
            Options.Add(opt);
        SelectedIndex.Value = 0;
        UpdateDisplay();
    }

    // ===== VISUALS =====

    private void UpdateDisplay()
    {
        var label = LabelText?.Target;
        if (label != null)
        {
            label.Content.Value = GetSelectedOption();
        }
    }

    private void UpdateVisuals()
    {
        var background = Background?.Target;
        if (background != null)
        {
            color bgColor;
            if (IsExpanded.Value)
                bgColor = ExpandedColor?.Value ?? new color(0.3f, 0.3f, 0.35f, 1f);
            else if (_isHovered)
                bgColor = HoveredColor?.Value ?? new color(0.35f, 0.35f, 0.35f, 1f);
            else
                bgColor = NormalColor?.Value ?? new color(0.25f, 0.25f, 0.25f, 1f);

            background.BackgroundColor.Value = bgColor;
        }
    }

    private void UpdateDropdownVisibility()
    {
        var content = DropdownContent?.Target;
        if (content != null)
        {
            content.ActiveSelf.Value = IsExpanded.Value;
        }
        UpdateVisuals();
    }

    /// <summary>
    /// Close the dropdown menu.
    /// </summary>
    public void Close()
    {
        IsExpanded.Value = false;
    }

    /// <summary>
    /// Open the dropdown menu.
    /// </summary>
    public void Open()
    {
        if (IsInteractable)
            IsExpanded.Value = true;
    }
}

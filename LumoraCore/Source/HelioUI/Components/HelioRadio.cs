using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio radio button component for exclusive selection within groups.
/// Supports mutual exclusivity via group names.
/// </summary>
[ComponentCategory("HelioUI/Interaction")]
public class HelioRadio : Component, IHelioInteractable
{
    // ===== STATE =====

    /// <summary>
    /// Whether this radio button is currently selected.
    /// </summary>
    public Sync<bool> IsSelected { get; private set; }

    /// <summary>
    /// Group name for mutual exclusivity.
    /// All radios with the same group name in the same hierarchy are mutually exclusive.
    /// </summary>
    public Sync<string> GroupName { get; private set; }

    /// <summary>
    /// The value this radio button represents.
    /// When selected, this value can be used to update a target field.
    /// </summary>
    public Sync<int> TargetValue { get; private set; }

    /// <summary>
    /// Optional target field to drive with TargetValue when selected.
    /// </summary>
    public SyncRef<Sync<int>> TargetField { get; private set; }

    // ===== VISUAL PROPERTIES =====

    /// <summary>
    /// Whether the radio button accepts input.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    /// <summary>
    /// Color when idle and unselected.
    /// </summary>
    public Sync<color> NormalColor { get; private set; }

    /// <summary>
    /// Color when hovered.
    /// </summary>
    public Sync<color> HoveredColor { get; private set; }

    /// <summary>
    /// Color when selected.
    /// </summary>
    public Sync<color> SelectedColor { get; private set; }

    /// <summary>
    /// Color when disabled.
    /// </summary>
    public Sync<color> DisabledColor { get; private set; }

    /// <summary>
    /// Reference to the selection indicator visual (typically a circle/dot inside the radio).
    /// </summary>
    public SyncRef<Slot> SelectionIndicator { get; private set; }

    /// <summary>
    /// Reference to the background panel.
    /// </summary>
    public SyncRef<HelioPanel> Background { get; private set; }

    /// <summary>
    /// Reference to a text label.
    /// </summary>
    public SyncRef<HelioText> Label { get; private set; }

    // ===== EVENTS =====

    /// <summary>
    /// Invoked when this radio button is selected (with the TargetValue).
    /// </summary>
    public SyncDelegate<Action<int>> OnSelected { get; private set; }

    /// <summary>
    /// Invoked when this radio button is deselected.
    /// </summary>
    public SyncDelegate<Action> OnDeselected { get; private set; }

    /// <summary>
    /// Invoked when the selection state changes (with the new state).
    /// </summary>
    public SyncDelegate<Action<bool>> OnSelectionChanged { get; private set; }

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

        // Initialize state
        IsSelected = new Sync<bool>(this, false);
        GroupName = new Sync<string>(this, "default");
        TargetValue = new Sync<int>(this, 0);
        TargetField = new SyncRef<Sync<int>>(this);

        // Initialize visual properties
        Interactable = new Sync<bool>(this, true);
        NormalColor = new Sync<color>(this, new color(0.3f, 0.3f, 0.3f, 1f));
        HoveredColor = new Sync<color>(this, new color(0.4f, 0.4f, 0.4f, 1f));
        SelectedColor = new Sync<color>(this, new color(0.3f, 0.6f, 1f, 1f));
        DisabledColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.2f, 0.5f));

        // Initialize references
        SelectionIndicator = new SyncRef<Slot>(this);
        Background = new SyncRef<HelioPanel>(this);
        Label = new SyncRef<HelioText>(this);

        // Initialize events
        OnSelected = new SyncDelegate<Action<int>>(this);
        OnDeselected = new SyncDelegate<Action>(this);
        OnSelectionChanged = new SyncDelegate<Action<bool>>(this);

        // React to selection changes
        IsSelected.OnChanged += HandleSelectionChanged;
        Interactable.OnChanged += _ => UpdateVisuals();
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Sync with target field if bound
        if (TargetField.Target != null && IsSelected.Value)
        {
            // Drive the target field with our value when selected
            TargetField.Target.Value = TargetValue.Value;
        }
    }

    // ===== SELECTION LOGIC =====

    /// <summary>
    /// Select this radio button (and deselect others in the same group).
    /// </summary>
    public void Select()
    {
        if (IsSelected.Value)
            return; // Already selected

        IsSelected.Value = true;
    }

    /// <summary>
    /// Deselect this radio button.
    /// Note: Typically called internally when another radio in the group is selected.
    /// </summary>
    public void Deselect()
    {
        if (!IsSelected.Value)
            return; // Already deselected

        IsSelected.Value = false;
    }

    /// <summary>
    /// Handle selection state changes.
    /// </summary>
    private void HandleSelectionChanged(bool selected)
    {
        if (selected)
        {
            // Deselect other radios in the same group
            DeselectOthersInGroup();

            // Update target field
            if (TargetField.Target != null)
            {
                TargetField.Target.Value = TargetValue.Value;
            }

            // Fire selection event
            OnSelected?.Invoke(TargetValue.Value);
        }
        else
        {
            // Fire deselection event
            OnDeselected?.Invoke();
        }

        // Fire general state change event
        OnSelectionChanged?.Invoke(selected);

        // Update visuals
        UpdateVisuals();
    }

    /// <summary>
    /// Deselect all other radio buttons in the same group.
    /// Searches up the hierarchy to find a common root, then searches down for radios.
    /// </summary>
    private void DeselectOthersInGroup()
    {
        if (string.IsNullOrEmpty(GroupName.Value))
            return;

        // Find the root slot to search from (go up to the world root or a reasonable parent)
        Slot searchRoot = FindRadioGroupRoot();
        if (searchRoot == null)
            return;

        // Find all radios in the same group
        var radiosInGroup = FindRadiosInGroup(searchRoot, GroupName.Value);

        // Deselect all others
        foreach (var radio in radiosInGroup)
        {
            if (radio != this && radio.IsSelected.Value)
            {
                radio.IsSelected.Value = false;
            }
        }
    }

    /// <summary>
    /// Find the root slot to search for radio group members.
    /// Searches up the hierarchy until finding a suitable container or reaching world root.
    /// </summary>
    private Slot FindRadioGroupRoot()
    {
        Slot current = Slot;
        Slot lastValid = current;

        // Go up the hierarchy to find a reasonable search root
        // Stop at world root or after going up a reasonable number of levels
        int levelsUp = 0;
        const int maxLevelsUp = 10; // Prevent infinite loops and limit search scope

        while (current != null && levelsUp < maxLevelsUp)
        {
            lastValid = current;
            current = current.Parent;
            levelsUp++;

            // If we hit world root, use it
            if (current == null || current.Parent == null)
                break;
        }

        return lastValid;
    }

    /// <summary>
    /// Find all HelioRadio components with the specified group name in the hierarchy.
    /// </summary>
    private List<HelioRadio> FindRadiosInGroup(Slot root, string groupName)
    {
        var radios = new List<HelioRadio>();

        if (root == null || string.IsNullOrEmpty(groupName))
            return radios;

        // Recursively search the hierarchy
        SearchSlotForRadios(root, groupName, radios);

        return radios;
    }

    /// <summary>
    /// Recursively search a slot and its children for radio buttons in the specified group.
    /// </summary>
    private void SearchSlotForRadios(Slot slot, string groupName, List<HelioRadio> results)
    {
        if (slot == null)
            return;

        // Check this slot's components
        var components = slot.GetComponents<HelioRadio>();
        foreach (var radio in components)
        {
            if (radio.GroupName.Value == groupName)
            {
                results.Add(radio);
            }
        }

        // Search children
        foreach (var child in slot.Children)
        {
            SearchSlotForRadios(child, groupName, results);
        }
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

        // Select on click (only if not already selected)
        if (wasPressed && _isHovered && IsInteractable && !IsSelected.Value)
        {
            Select();
            OnPointerClick?.Invoke(eventData);
        }
    }

    // ===== VISUALS =====

    /// <summary>
    /// Update the visual state based on current selection and interaction state.
    /// </summary>
    private void UpdateVisuals()
    {
        // Update selection indicator visibility
        var indicator = SelectionIndicator?.Target;
        if (indicator != null)
        {
            indicator.ActiveSelf.Value = IsSelected.Value;
        }

        // Update background color
        var background = Background?.Target;
        if (background != null)
        {
            color bgColor;

            if (!IsInteractable)
                bgColor = DisabledColor?.Value ?? new color(0.2f, 0.2f, 0.2f, 0.5f);
            else if (IsSelected.Value)
                bgColor = SelectedColor?.Value ?? new color(0.3f, 0.6f, 1f, 1f);
            else if (_isHovered)
                bgColor = HoveredColor?.Value ?? new color(0.4f, 0.4f, 0.4f, 1f);
            else
                bgColor = NormalColor?.Value ?? new color(0.3f, 0.3f, 0.3f, 1f);

            background.BackgroundColor.Value = bgColor;
        }
    }

    /// <summary>
    /// Setup the radio button visuals with a circular appearance.
    /// </summary>
    public void SetupVisuals(float size = 24f)
    {
        // Add background panel (outer circle)
        var panel = Slot.GetComponent<HelioPanel>() ?? Slot.AttachComponent<HelioPanel>();
        panel.BackgroundColor.Value = NormalColor.Value;
        Background.Target = panel;

        // Create selection indicator slot (inner dot)
        var indicatorSlot = Slot.AddSlot("SelectionIndicator");
        var indicatorRect = indicatorSlot.AttachComponent<HelioRectTransform>();
        indicatorRect.AnchorMin.Value = new float2(0.25f, 0.25f);
        indicatorRect.AnchorMax.Value = new float2(0.75f, 0.75f);

        var indicatorPanel = indicatorSlot.AttachComponent<HelioPanel>();
        indicatorPanel.BackgroundColor.Value = new color(1f, 1f, 1f, 1f);

        SelectionIndicator.Target = indicatorSlot;
        indicatorSlot.ActiveSelf.Value = false;

        UpdateVisuals();
    }

    /// <summary>
    /// Set the label text for this radio button.
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
    /// Set the value this radio button represents.
    /// </summary>
    public void SetValue(int value)
    {
        TargetValue.Value = value;
    }
}

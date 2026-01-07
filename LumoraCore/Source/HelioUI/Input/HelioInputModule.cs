using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio input module.
/// Processes pointer input and dispatches events to UI elements.
/// Manages focus state for text fields and other selectable elements.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioInputModule : Component
{
    // ===== REFERENCES =====

    /// <summary>
    /// The canvas this input module processes input for.
    /// </summary>
    public SyncRef<HelioCanvas> TargetCanvas { get; private set; }

    // ===== STATE =====

    private IHelioInteractable _hoveredElement;
    private IHelioInteractable _pressedElement;
    private IHelioInteractable _selectedElement;
    private float2 _lastPointerPosition;
    private bool _wasPressed;

    /// <summary>
    /// Currently hovered element.
    /// </summary>
    public IHelioInteractable HoveredElement => _hoveredElement;

    /// <summary>
    /// Currently pressed element.
    /// </summary>
    public IHelioInteractable PressedElement => _pressedElement;

    /// <summary>
    /// Currently selected/focused element (for text input).
    /// </summary>
    public IHelioInteractable SelectedElement => _selectedElement;

    // ===== EVENTS =====

    /// <summary>
    /// Fired when selection changes.
    /// </summary>
    public event Action<IHelioInteractable> OnSelectionChanged;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();
        TargetCanvas = new SyncRef<HelioCanvas>(this);
    }

    // ===== INPUT PROCESSING =====

    /// <summary>
    /// Process pointer input for a frame.
    /// Call this each frame with the current pointer state.
    /// </summary>
    public void ProcessPointer(HelioPointerEventData pointerData)
    {
        if (TargetCanvas?.Target == null) return;

        // Find element at pointer position
        var hitElement = RaycastUI(pointerData.Position);

        // Handle hover transitions
        if (hitElement != _hoveredElement)
        {
            // Exit previous
            if (_hoveredElement != null)
            {
                InvokePointerExit(_hoveredElement, pointerData);
            }

            // Enter new
            _hoveredElement = hitElement;
            if (_hoveredElement != null)
            {
                InvokePointerEnter(_hoveredElement, pointerData);
            }
        }

        // Handle press/release
        bool isPressed = pointerData.IsPressed;

        if (isPressed && !_wasPressed)
        {
            // Pointer down
            _pressedElement = _hoveredElement;
            if (_pressedElement != null)
            {
                InvokePointerDown(_pressedElement, pointerData);
            }

            // Deselect if clicking outside selected element
            if (_selectedElement != null && _selectedElement != _hoveredElement)
            {
                Deselect();
            }
        }
        else if (!isPressed && _wasPressed)
        {
            // Pointer up
            if (_pressedElement != null)
            {
                InvokePointerUp(_pressedElement, pointerData);

                // Click if released on same element
                if (_pressedElement == _hoveredElement)
                {
                    InvokePointerClick(_pressedElement, pointerData);
                }

                _pressedElement = null;
            }
        }

        // Handle drag
        if (isPressed && _pressedElement != null)
        {
            pointerData.Delta = pointerData.Position - _lastPointerPosition;

            // Special handling for sliders
            if (_pressedElement is HelioSlider slider)
            {
                slider.HandlePointerDrag(pointerData);
            }
        }

        _wasPressed = isPressed;
        _lastPointerPosition = pointerData.Position;
    }

    /// <summary>
    /// Raycast against UI elements at canvas position.
    /// Returns the topmost interactable element.
    /// </summary>
    private IHelioInteractable RaycastUI(float2 canvasPosition)
    {
        var canvas = TargetCanvas?.Target;
        if (canvas == null) return null;

        // Find all interactables under the pointer
        return RaycastSlot(canvas.Slot, canvasPosition);
    }

    private IHelioInteractable RaycastSlot(Slot slot, float2 position)
    {
        if (!slot.ActiveSelf.Value) return null;

        // Check children first (reverse order for correct z-order)
        var children = slot.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var result = RaycastSlot(children[i], position);
            if (result != null) return result;
        }

        // Check this slot
        var rect = slot.GetComponent<HelioRectTransform>();
        if (rect == null) return null;

        var bounds = rect.Rect;
        if (!bounds.Contains(position)) return null;

        // Find interactable component
        var interactable = slot.GetComponent<HelioButton>() as IHelioInteractable
            ?? slot.GetComponent<HelioToggle>() as IHelioInteractable
            ?? slot.GetComponent<HelioSlider>() as IHelioInteractable
            ?? slot.GetComponent<HelioTextField>() as IHelioInteractable
            ?? slot.GetComponent<HelioDropdown>() as IHelioInteractable
            ?? slot.GetComponent<HelioScrollView>() as IHelioInteractable;

        if (interactable != null && interactable.IsInteractable)
            return interactable;

        return null;
    }

    // ===== PUBLIC POINTER EVENT METHODS =====
    // Used by external input systems (VR controllers, etc.)

    /// <summary>
    /// Process pointer enter event from external source.
    /// </summary>
    public void ProcessPointerEnter(IHelioInteractable element, HelioPointerEventData data)
    {
        if (element == null) return;
        _hoveredElement = element;
        InvokePointerEnter(element, data);
    }

    /// <summary>
    /// Process pointer exit event from external source.
    /// </summary>
    public void ProcessPointerExit(IHelioInteractable element, HelioPointerEventData data)
    {
        if (element == null) return;
        if (_hoveredElement == element)
            _hoveredElement = null;
        InvokePointerExit(element, data);
    }

    /// <summary>
    /// Process pointer down event from external source.
    /// </summary>
    public void ProcessPointerDown(IHelioInteractable element, HelioPointerEventData data)
    {
        if (element == null) return;
        _pressedElement = element;

        // Deselect if clicking outside selected element
        if (_selectedElement != null && _selectedElement != element)
        {
            Deselect();
        }

        InvokePointerDown(element, data);
    }

    /// <summary>
    /// Process pointer up event from external source.
    /// </summary>
    public void ProcessPointerUp(IHelioInteractable element, HelioPointerEventData data)
    {
        if (element == null) return;

        InvokePointerUp(element, data);

        // Click if released on same element that was pressed
        if (_pressedElement == element)
        {
            InvokePointerClick(element, data);
        }

        _pressedElement = null;
    }

    // ===== EVENT INVOCATION =====

    private void InvokePointerEnter(IHelioInteractable element, HelioPointerEventData data)
    {
        switch (element)
        {
            case HelioButton btn: btn.HandlePointerEnter(data); break;
            case HelioToggle toggle: toggle.HandlePointerEnter(data); break;
            case HelioSlider slider: slider.HandlePointerEnter(data); break;
            case HelioTextField field: field.HandlePointerEnter(data); break;
            case HelioDropdown dropdown: dropdown.HandlePointerEnter(data); break;
            case HelioScrollView scroll: scroll.HandlePointerEnter(data); break;
        }
    }

    private void InvokePointerExit(IHelioInteractable element, HelioPointerEventData data)
    {
        switch (element)
        {
            case HelioButton btn: btn.HandlePointerExit(data); break;
            case HelioToggle toggle: toggle.HandlePointerExit(data); break;
            case HelioSlider slider: slider.HandlePointerExit(data); break;
            case HelioTextField field: field.HandlePointerExit(data); break;
            case HelioDropdown dropdown: dropdown.HandlePointerExit(data); break;
            case HelioScrollView scroll: scroll.HandlePointerExit(data); break;
        }
    }

    private void InvokePointerDown(IHelioInteractable element, HelioPointerEventData data)
    {
        switch (element)
        {
            case HelioButton btn: btn.HandlePointerDown(data); break;
            case HelioToggle toggle: toggle.HandlePointerDown(data); break;
            case HelioSlider slider: slider.HandlePointerDown(data); break;
            case HelioTextField field: field.HandlePointerDown(data); break;
            case HelioDropdown dropdown: dropdown.HandlePointerDown(data); break;
            case HelioScrollView scroll: scroll.HandlePointerDown(data); break;
        }
    }

    private void InvokePointerUp(IHelioInteractable element, HelioPointerEventData data)
    {
        switch (element)
        {
            case HelioButton btn: btn.HandlePointerUp(data); break;
            case HelioToggle toggle: toggle.HandlePointerUp(data); break;
            case HelioSlider slider: slider.HandlePointerUp(data); break;
            case HelioTextField field: field.HandlePointerUp(data); break;
            case HelioDropdown dropdown: dropdown.HandlePointerUp(data); break;
            case HelioScrollView scroll: scroll.HandlePointerUp(data); break;
        }
    }

    private void InvokePointerClick(IHelioInteractable element, HelioPointerEventData data)
    {
        // Handle selection for text fields
        if (element is HelioTextField textField)
        {
            Select(element);
        }
    }

    // ===== SELECTION/FOCUS =====

    /// <summary>
    /// Select/focus an element (for keyboard input).
    /// </summary>
    public void Select(IHelioInteractable element)
    {
        if (_selectedElement == element) return;

        // Deselect previous
        if (_selectedElement is HelioTextField prevField)
        {
            prevField.Unfocus();
        }

        _selectedElement = element;

        // Select new
        if (_selectedElement is HelioTextField newField)
        {
            newField.Focus();
        }

        OnSelectionChanged?.Invoke(_selectedElement);
    }

    /// <summary>
    /// Clear current selection.
    /// </summary>
    public void Deselect()
    {
        if (_selectedElement == null) return;

        if (_selectedElement is HelioTextField field)
        {
            field.Unfocus();
        }

        _selectedElement = null;
        OnSelectionChanged?.Invoke(null);
    }

    /// <summary>
    /// Process keyboard input for the selected element.
    /// </summary>
    public void ProcessKeyboardInput(char character)
    {
        if (_selectedElement is HelioTextField field)
        {
            field.InsertCharacter(character);
        }
    }

    /// <summary>
    /// Process special key input (backspace, delete, arrows, enter).
    /// </summary>
    public void ProcessSpecialKey(SpecialKey key)
    {
        if (_selectedElement is HelioTextField field)
        {
            switch (key)
            {
                case SpecialKey.Backspace:
                    field.Backspace();
                    break;
                case SpecialKey.Delete:
                    field.Delete();
                    break;
                case SpecialKey.Left:
                    field.MoveCaretLeft();
                    break;
                case SpecialKey.Right:
                    field.MoveCaretRight();
                    break;
                case SpecialKey.Enter:
                    field.Submit();
                    break;
                case SpecialKey.Escape:
                    Deselect();
                    break;
            }
        }
    }
}

/// <summary>
/// Special keyboard keys for text input.
/// </summary>
public enum SpecialKey
{
    Backspace,
    Delete,
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    Enter,
    Tab,
    Escape
}

using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio modal overlay component.
/// Creates a full-screen overlay with content slot that blocks interaction with elements behind it.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioModalOverlay : Component, IHelioInteractable
{
    // ===== STATE =====

    /// <summary>
    /// Whether the modal is currently open and visible.
    /// </summary>
    public Sync<bool> IsOpen { get; private set; }

    /// <summary>
    /// Background overlay color (typically semi-transparent dark).
    /// </summary>
    public Sync<color> OverlayColor { get; private set; }

    /// <summary>
    /// Reference to the slot containing modal content.
    /// This slot will be shown/hidden based on IsOpen state.
    /// </summary>
    public SyncRef<Slot> ContentSlot { get; private set; }

    /// <summary>
    /// Whether clicking on the overlay background (outside content) should close the modal.
    /// </summary>
    public Sync<bool> CloseOnOverlayClick { get; private set; }

    // ===== EVENTS =====

    /// <summary>
    /// Invoked when the modal is opened.
    /// </summary>
    public SyncDelegate<Action> OnOpened { get; private set; }

    /// <summary>
    /// Invoked when the modal is closed.
    /// </summary>
    public SyncDelegate<Action> OnClosed { get; private set; }

    // ===== INTERNAL STATE =====

    private HelioPanel _overlayPanel;
    private HelioRectTransform _overlayRect;
    private bool _wasOpen = false;
    private bool _isHovered = false;
    private bool _isPressed = false;

    // ===== IHelioInteractable IMPLEMENTATION =====

    /// <summary>
    /// Modal overlay is always interactable when open (to block background clicks).
    /// </summary>
    public bool IsInteractable => IsOpen?.Value ?? false;

    /// <summary>
    /// Whether a pointer is currently hovering over the overlay.
    /// </summary>
    public bool IsHovered => _isHovered;

    /// <summary>
    /// Whether the overlay is currently being pressed.
    /// </summary>
    public bool IsPressed => _isPressed;

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
        IsOpen = new Sync<bool>(this, false);
        OverlayColor = new Sync<color>(this, new color(0f, 0f, 0f, 0.7f));
        ContentSlot = new SyncRef<Slot>(this);
        CloseOnOverlayClick = new Sync<bool>(this, true);

        OnOpened = new SyncDelegate<Action>(this);
        OnClosed = new SyncDelegate<Action>(this);

		// React to state changes
		IsOpen.OnChanged += OnIsOpenChanged;
		OverlayColor.OnChanged += OnOverlayColorChanged;
		ContentSlot.Changed += OnContentSlotChanged;
	}

    public override void OnStart()
    {
        base.OnStart();

        // Ensure the overlay has a full-screen rect transform
        _overlayRect = Slot.GetComponent<HelioRectTransform>() ?? Slot.AttachComponent<HelioRectTransform>();
        _overlayRect.AnchorMin.Value = float2.Zero;
        _overlayRect.AnchorMax.Value = float2.One;
        _overlayRect.OffsetMin.Value = float2.Zero;
        _overlayRect.OffsetMax.Value = float2.Zero;

        // Create overlay panel for background
        _overlayPanel = Slot.GetComponent<HelioPanel>() ?? Slot.AttachComponent<HelioPanel>();
        _overlayPanel.BackgroundColor.Value = OverlayColor.Value;

        // Set initial visibility
        UpdateVisibility();
    }

    // ===== PUBLIC METHODS =====

    /// <summary>
    /// Show the modal overlay.
    /// </summary>
    public void Show()
    {
        if (!IsOpen.Value)
        {
            IsOpen.Value = true;
        }
    }

    /// <summary>
    /// Hide the modal overlay.
    /// </summary>
    public void Hide()
    {
        if (IsOpen.Value)
        {
            IsOpen.Value = false;
        }
    }

    // ===== CHANGE HANDLERS =====

    private void OnIsOpenChanged(bool isOpen)
    {
        UpdateVisibility();

        // Fire open/close events
        if (isOpen && !_wasOpen)
        {
            _wasOpen = true;
            OnOpened?.Invoke();
        }
        else if (!isOpen && _wasOpen)
        {
            _wasOpen = false;
            OnClosed?.Invoke();
        }
    }

    private void OnOverlayColorChanged(color newColor)
    {
        if (_overlayPanel != null)
        {
            _overlayPanel.BackgroundColor.Value = newColor;
        }
    }

    private void OnContentSlotChanged(IChangeable changed)
    {
        UpdateVisibility();
    }

    // ===== VISIBILITY MANAGEMENT =====

    private void UpdateVisibility()
    {
        bool isOpen = IsOpen?.Value ?? false;

        // Show/hide the overlay slot itself
        if (Slot != null)
        {
            Slot.ActiveSelf.Value = isOpen;
        }

        // Show/hide content slot
        var content = ContentSlot?.Target;
        if (content != null)
        {
            content.ActiveSelf.Value = isOpen;
        }
    }

    // ===== INTERACTION HANDLERS =====

    /// <summary>
    /// Called when pointer enters the overlay.
    /// </summary>
    public void HandlePointerEnter(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        _isHovered = true;
        OnPointerEnter?.Invoke(eventData);
    }

    /// <summary>
    /// Called when pointer exits the overlay.
    /// </summary>
    public void HandlePointerExit(HelioPointerEventData eventData)
    {
        _isHovered = false;
        _isPressed = false;
        OnPointerExit?.Invoke(eventData);
    }

    /// <summary>
    /// Called when pointer is pressed on the overlay.
    /// </summary>
    public void HandlePointerDown(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;

        _isPressed = true;
        OnPointerDown?.Invoke(eventData);
    }

    /// <summary>
    /// Called when pointer is released on the overlay.
    /// </summary>
    public void HandlePointerUp(HelioPointerEventData eventData)
    {
        bool wasPressed = _isPressed;
        _isPressed = false;
        OnPointerUp?.Invoke(eventData);

        // Check if click should close the modal
        if (wasPressed && _isHovered && IsInteractable && CloseOnOverlayClick.Value)
        {
            // Only close if the click was on the overlay itself, not on content
            if (!IsClickOnContent(eventData))
            {
                Hide();
            }
        }

        OnPointerClick?.Invoke(eventData);
    }

    /// <summary>
    /// Checks if a click occurred within the content slot's area.
    /// </summary>
    private bool IsClickOnContent(HelioPointerEventData eventData)
    {
        var content = ContentSlot?.Target;
        if (content == null) return false;

        var contentRect = content.GetComponent<HelioRectTransform>();
        if (contentRect == null) return false;

        // Check if the click position is within the content rect
        return contentRect.Rect.Contains(eventData.Position);
    }

    // ===== CLEANUP =====

    public override void OnDestroy()
    {
        // Clean up event subscriptions
        OnPointerEnter = null;
        OnPointerExit = null;
        OnPointerDown = null;
        OnPointerUp = null;
        OnPointerClick = null;

        base.OnDestroy();
    }
}

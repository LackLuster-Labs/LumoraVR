using System;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base interface for all interactive Helio UI elements.
/// Provides pointer event handling for VR laser, mouse, and touch input.
/// </summary>
public interface IHelioInteractable
{
    /// <summary>
    /// Whether this element can currently receive input.
    /// </summary>
    bool IsInteractable { get; }

    /// <summary>
    /// Whether a pointer is currently over this element.
    /// </summary>
    bool IsHovered { get; }

    /// <summary>
    /// Whether this element is currently being pressed.
    /// </summary>
    bool IsPressed { get; }

    /// <summary>
    /// Called when a pointer enters this element.
    /// </summary>
    event Action<HelioPointerEventData> OnPointerEnter;

    /// <summary>
    /// Called when a pointer exits this element.
    /// </summary>
    event Action<HelioPointerEventData> OnPointerExit;

    /// <summary>
    /// Called when a pointer is pressed on this element.
    /// </summary>
    event Action<HelioPointerEventData> OnPointerDown;

    /// <summary>
    /// Called when a pointer is released on this element.
    /// </summary>
    event Action<HelioPointerEventData> OnPointerUp;

    /// <summary>
    /// Called when a pointer click is completed on this element.
    /// </summary>
    event Action<HelioPointerEventData> OnPointerClick;
}

using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// Interface for elements that can be linked/driven by other elements.
/// Used for field drives, value hooks, and other linking mechanisms.
/// </summary>
public interface ILinkable : IWorldElement
{
    /// <summary>
    /// Whether this element is currently linked to another element.
    /// </summary>
    bool IsLinked { get; }

    /// <summary>
    /// Whether this element is being driven (value is controlled by another element).
    /// </summary>
    bool IsDriven { get; }

    /// <summary>
    /// Whether this element is hooked (has a callback intercepting value changes).
    /// </summary>
    bool IsHooked { get; }

    /// <summary>
    /// The currently active link reference.
    /// </summary>
    ILinkRef ActiveLink { get; }

    /// <summary>
    /// The direct link reference (not inherited from parent).
    /// </summary>
    ILinkRef DirectLink { get; }

    /// <summary>
    /// The inherited link reference (from parent element).
    /// </summary>
    ILinkRef InheritedLink { get; }

    /// <summary>
    /// Children elements that can be linked.
    /// </summary>
    IEnumerable<ILinkable> LinkableChildren { get; }

    /// <summary>
    /// Establish a direct link to this element.
    /// </summary>
    /// <param name="link">The link reference to establish</param>
    void Link(ILinkRef link);

    /// <summary>
    /// Establish an inherited link to this element.
    /// </summary>
    /// <param name="link">The link reference to inherit</param>
    void InheritLink(ILinkRef link);

    /// <summary>
    /// Release a direct link from this element.
    /// </summary>
    /// <param name="link">The link reference to release</param>
    void ReleaseLink(ILinkRef link);

    /// <summary>
    /// Release an inherited link from this element.
    /// </summary>
    /// <param name="link">The link reference to release</param>
    void ReleaseInheritedLink(ILinkRef link);
}

/// <summary>
/// Interface for a reference that can link to an ILinkable element.
/// </summary>
public interface ILinkRef : IWorldElement
{
    /// <summary>
    /// The target element being linked to.
    /// </summary>
    ILinkable Target { get; }

    /// <summary>
    /// Whether the link is currently valid and active.
    /// </summary>
    bool IsLinkValid { get; }

    /// <summary>
    /// Whether the link was granted by the target.
    /// </summary>
    bool WasLinkGranted { get; }

    /// <summary>
    /// Whether this link is driving the target's value.
    /// </summary>
    bool IsDriving { get; }

    /// <summary>
    /// Whether this link is hooking the target's value changes.
    /// </summary>
    bool IsHooking { get; }

    /// <summary>
    /// Whether modifications are allowed from this link.
    /// </summary>
    bool IsModificationAllowed { get; }

    /// <summary>
    /// Release the link to the target.
    /// </summary>
    /// <param name="undoable">Whether this should be an undoable operation</param>
    void ReleaseLink(bool undoable = false);

    /// <summary>
    /// Grant the link permission to the target.
    /// </summary>
    void GrantLink();
}

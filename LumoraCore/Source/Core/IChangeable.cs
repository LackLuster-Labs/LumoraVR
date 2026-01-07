using System;

namespace Lumora.Core;

/// <summary>
/// Interface for elements that support change tracking and notification.
/// Provides reactive updates and change propagation.
/// </summary>
public interface IChangeable : IWorldElement
{
    /// <summary>
    /// Event fired when this element changes.
    /// Used for reactive updates and change propagation.
    /// </summary>
    event Action<IChangeable> Changed;
}

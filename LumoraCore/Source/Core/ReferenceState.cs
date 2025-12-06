namespace Lumora.Core;

/// <summary>
/// State of a reference to a world element.
/// Tracks the lifecycle of reference resolution.
/// </summary>
public enum ReferenceState
{
    /// <summary>
    /// Reference is null/unset (RefID.Null).
    /// </summary>
    Null,

    /// <summary>
    /// RefID is set but object is not yet available.
    /// Waiting for async resolution or network sync.
    /// </summary>
    Waiting,

    /// <summary>
    /// Object has been resolved and is available.
    /// </summary>
    Available,

    /// <summary>
    /// RefID doesn't resolve to the expected type.
    /// The object exists but is wrong type.
    /// </summary>
    Invalid,

    /// <summary>
    /// Referenced object has been destroyed/removed.
    /// </summary>
    Removed
}

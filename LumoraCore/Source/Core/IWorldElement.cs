
namespace Lumora.Core;

/// <summary>
/// Base interface for all elements that exist within a World.
/// </summary>
public interface IWorldElement
{
	/// <summary>
	/// The World this element belongs to.
	/// </summary>
	World World { get; }

	/// <summary>
	/// Strongly-typed reference identifier for this element.
	/// </summary>
	RefID ReferenceID { get; }

	/// <summary>
	/// Whether this element has been initialized.
	/// </summary>
	bool IsInitialized { get; }

	/// <summary>
	/// Whether this element has been destroyed.
	/// </summary>
	bool IsDestroyed { get; }

	/// <summary>
	/// Whether this element belongs to the local-only allocation space.
	/// </summary>
	bool IsLocalElement { get; }

	/// <summary>
	/// Whether this element should persist in saves.
	/// </summary>
	bool IsPersistent { get; }

	/// <summary>
	/// Get a human-readable path describing this element's hierarchy.
	/// </summary>
	string ParentHierarchyToString();
}

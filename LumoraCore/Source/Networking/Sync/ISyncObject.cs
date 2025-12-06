using System.Collections.Generic;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Interface for objects that contain sync members.
/// </summary>
public interface ISyncObject
{
	/// <summary>
	/// All sync members discovered via reflection.
	/// </summary>
	List<ISyncMember> SyncMembers { get; }

	/// <summary>
	/// Strongly-typed identifier for this sync object.
	/// Used in network messages to target updates.
	/// </summary>
	RefID ReferenceID { get; }

	/// <summary>
	/// Whether this object is owned/controlled by authority.
	/// </summary>
	bool IsAuthority { get; }
}

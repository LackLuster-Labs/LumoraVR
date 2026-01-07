using System;
using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Network bag for Users.
/// When a FullBatch is received, this creates User objects that don't exist yet.
/// </summary>
public class UserBag : SyncRefIDBagBase<User>
{
    public UserBag()
    {
        // Hook into element added to register users with world
        OnElementAdded += HandleUserAdded;
        OnElementRemoved += HandleUserRemoved;
    }

    /// <summary>
    /// Encode user data to the stream.
    /// User's sync members are handled separately via the SyncElement system.
    /// </summary>
    protected override void EncodeElement(BinaryWriter writer, User element)
    {
        // Intentionally empty - user's sync members (UserName, AllocationID, etc.)
        // are encoded as separate SyncElements
    }

    /// <summary>
    /// Decode element - not used directly, we override CreateElementWithKey instead.
    /// </summary>
    protected override User DecodeElement(BinaryReader reader)
    {
        // This shouldn't be called - we override CreateElementWithKey
        throw new InvalidOperationException("UserBag requires CreateElementWithKey - DecodeElement should not be called");
    }

    /// <summary>
    /// Create a User with the given RefID key.
    /// Users need their RefID at construction time.
    /// </summary>
    protected override User CreateElementWithKey(RefID key, BinaryReader reader)
    {
        return new User();
    }

    /// <summary>
    /// Handle user addition - register with World.
    /// </summary>
    private void HandleUserAdded(ReplicatedDictionary<RefID, User> dict, RefID key, User user, bool isNew)
    {
        if (World == null || user == null) return;

        if (user.World == null)
        {
            user.InitializeFromBag(World, key);
        }

        // Initialize first to set LocalUser before registration
        // User.Initialize() checks if RefID matches Session.LocalUserRefIDToInit
        // This MUST happen before RegisterUser so that:
        // 1. World.LocalUser is set before OnUserJoined events fire
        // 2. UserRoot.OnChanges() can detect the local user correctly
        user.Initialize();

        // Keep sync members in loading state until their values
        // are decoded from FullBatch. This prevents them from being marked dirty
        // if the world transitions to Running before all values are decoded.
        foreach (var member in user.SyncMembers)
        {
            if (member is SyncElement syncElement)
            {
                syncElement.IsLoading = true;
            }
        }

        // Now register user with world's user tracking
        World.RegisterUser(user);
    }

    /// <summary>
    /// Handle user removal - unregister from World.
    /// </summary>
    private void HandleUserRemoved(ReplicatedDictionary<RefID, User> dict, RefID key, User user)
    {
        if (World == null || user == null) return;

        // Unregister user from world
        World.UnregisterUser(user);
    }

    public override void Dispose()
    {
        OnElementAdded -= HandleUserAdded;
        OnElementRemoved -= HandleUserRemoved;
        base.Dispose();
    }
}

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Spawns users at this slot's position when they join the world.
/// Creates UserRoot hierarchy and avatar.
///
/// Flow:
/// 1. TrackedDevicePositioner reads device input and creates AvatarObjectSlot
/// 2. AvatarPoseNode on skeleton bones equips to AvatarObjectSlot
/// 3. AvatarPoseNode directly drives bone transforms
/// </summary>
[ComponentCategory("Users")]
public class SimpleUserSpawn : Component, IWorldEventReceiver
{
    // Track spawned users to prevent duplicates
    private Dictionary<User, Slot> _userSlots = new Dictionary<User, Slot>();

    public override void OnAwake()
    {
        base.OnAwake();
        World?.RegisterEventReceiver(this);
    }

    public override void OnDestroy()
    {
        World?.UnregisterEventReceiver(this);
        base.OnDestroy();
    }

    public bool HasEventHandler(World.WorldEvent eventType)
    {
        return eventType == World.WorldEvent.OnUserJoined ||
               eventType == World.WorldEvent.OnUserLeft;
    }

    public void OnUserJoined(User user)
    {
        if (user == null) return;

        // CRITICAL: Only authority spawns users!
        // Clients receive the spawned slots via network sync.
        if (!World.IsAuthority)
        {
            AquaLogger.Log($"SimpleUserSpawn: Client ignoring OnUserJoined for '{user.UserName.Value}' - authority will spawn and sync");
            return;
        }

        // Prevent duplicate spawns
        if (_userSlots.ContainsKey(user))
        {
            AquaLogger.Warn($"SimpleUserSpawn: User '{user.UserName.Value}' already spawned, ignoring duplicate");
            return;
        }

        AquaLogger.Log($"SimpleUserSpawn: [Authority] Spawning user '{user.UserName.Value ?? "(null)"}', RefID={user.ReferenceID}");

        try
        {
            // Create user slot at spawn position
            var userName = user.UserName.Value;
            if (string.IsNullOrEmpty(userName))
            {
                userName = $"User_{user.ReferenceID}";
            }

            var userSlot = World.RootSlot.AddSlot($"User {userName}");
            userSlot.Persistent.Value = false;
            userSlot.LocalPosition.Value = Slot.LocalPosition.Value;
            userSlot.LocalRotation.Value = Slot.LocalRotation.Value;

            // Track the user slot
            _userSlots.Add(user, userSlot);

            // Use DefaultAVI to spawn user with full skeleton avatar
            DefaultAVI.SpawnWithDefaultAvatar(userSlot, user);

            AquaLogger.Log($"SimpleUserSpawn: [Authority] Spawned user '{userName}' - slots will sync to clients");
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SimpleUserSpawn: Failed to spawn '{user.UserName.Value}': {ex.Message}\n{ex.StackTrace}");
        }
    }

    public void OnUserLeft(User user)
    {
        if (user == null) return;

        // Only authority manages user slots
        if (!World.IsAuthority) return;

        if (_userSlots.TryGetValue(user, out var slot))
        {
            slot.Destroy();
            _userSlots.Remove(user);
        }
    }

    // Unused interface methods
    public void OnFocusChanged(World.WorldFocus focus) { }
    public void OnWorldDestroy() { }
}

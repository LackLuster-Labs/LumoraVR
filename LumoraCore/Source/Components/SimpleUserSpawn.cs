using System;
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

        AquaLogger.Log($"SimpleUserSpawn: OnUserJoined - UserName='{user.UserName.Value ?? "(null)"}', UserID='{user.UserID.Value ?? "(null)"}', RefID={user.ReferenceID}");
        AquaLogger.Log($"SimpleUserSpawn: IsLocalUser={user == World?.LocalUser}, World.LocalUser='{World?.LocalUser?.UserName?.Value ?? "(null)"}'");

        try
        {
            // Create user slot at spawn position
            var userName = user.UserName.Value;
            if (string.IsNullOrEmpty(userName))
            {
                userName = $"User_{user.ReferenceID}";
                AquaLogger.Warn($"SimpleUserSpawn: Username was empty, using fallback: {userName}");
            }

            var userSlot = World.RootSlot.AddSlot($"User {userName}");
            userSlot.Persistent.Value = false;
            userSlot.LocalPosition.Value = Slot.LocalPosition.Value;
            userSlot.LocalRotation.Value = Slot.LocalRotation.Value;

            // Use DefaultAVI to spawn user with full skeleton avatar
            DefaultAVI.SpawnWithDefaultAvatar(userSlot, user);

            AquaLogger.Log($"SimpleUserSpawn: Spawned user '{userName}' with default avatar");
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SimpleUserSpawn: Failed to spawn '{user.UserName.Value}': {ex.Message}\n{ex.StackTrace}");
        }
    }

    public void OnUserLeft(User user)
    {
        if (user?.Root?.Slot == null) return;
        user.Root.Slot.Destroy();
        user.Root = null;
    }

    // Unused interface methods
    public void OnFocusChanged(World.WorldFocus focus) { }
    public void OnWorldDestroy() { }
}

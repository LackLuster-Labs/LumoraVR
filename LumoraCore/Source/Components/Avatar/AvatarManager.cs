using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// Manages avatar equipping and swapping for a user.
/// </summary>
[ComponentCategory("Avatar")]
public class AvatarManager : ImplementableComponent
{
    /// <summary>
    /// Reference to the current equipped avatar root.
    /// </summary>
    public SyncRef<Slot> CurrentAvatar { get; private set; }

    /// <summary>
    /// Reference to the user root this manager belongs to.
    /// </summary>
    public SyncRef<UserRoot> UserRoot { get; private set; }

    /// <summary>
    /// Default avatar slot (fallback when no custom avatar).
    /// </summary>
    public SyncRef<Slot> DefaultAvatarSlot { get; private set; }

    /// <summary>
    /// Whether the default avatar is currently equipped.
    /// </summary>
    public Sync<bool> IsUsingDefaultAvatar { get; private set; }

    /// <summary>
    /// List of available imported avatars.
    /// </summary>
    public SyncRefList<Slot> AvailableAvatars { get; private set; }

    /// <summary>
    /// Event fired when avatar changes.
    /// </summary>
    public event Action<Slot> OnAvatarChanged;

    public override void OnAwake()
    {
        base.OnAwake();

        CurrentAvatar = new SyncRef<Slot>(this, null);
        UserRoot = new SyncRef<UserRoot>(this, null);
        DefaultAvatarSlot = new SyncRef<Slot>(this, null);
        IsUsingDefaultAvatar = new Sync<bool>(this, true);
        AvailableAvatars = new SyncRefList<Slot>(this);

        Logger.Log($"AvatarManager: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();

        // Try to find UserRoot in parent hierarchy
        if (UserRoot.Target == null)
        {
            var ur = Slot.GetComponentInParent<UserRoot>();
            if (ur != null)
            {
                UserRoot.Target = ur;
            }
        }
    }

    /// <summary>
    /// Equip an avatar from a slot.
    /// </summary>
    public bool EquipAvatar(Slot avatarSlot)
    {
        if (avatarSlot == null)
        {
            Logger.Warn("AvatarManager: Cannot equip null avatar");
            return false;
        }

        Logger.Log($"AvatarManager: Equipping avatar from slot '{avatarSlot.SlotName.Value}'");

        // Dequip current avatar first
        DequipCurrentAvatar();

        // Find the skeleton in the avatar
        var skeleton = avatarSlot.GetComponentInChildren<SkeletonBuilder>();
        if (skeleton == null)
        {
            Logger.Warn("AvatarManager: Avatar has no skeleton");
        }

        // Find IK component
        var ikAvatar = avatarSlot.GetComponentInChildren<GodotIKAvatar>();
        if (ikAvatar == null && skeleton != null)
        {
            // Create IK for the avatar
            var ikSlot = avatarSlot.AddSlot("IK");
            ikAvatar = ikSlot.AttachComponent<GodotIKAvatar>();
            ikAvatar.Skeleton.Target = skeleton;

            // Connect to user root tracking
            if (UserRoot.Target != null)
            {
                ikAvatar.SetupTracking(UserRoot.Target);
            }
        }

        // Parent avatar to our slot
        avatarSlot.SetParent(Slot);
        avatarSlot.LocalPosition.Value = float3.Zero;
        avatarSlot.LocalRotation.Value = floatQ.Identity;

        // Set as current avatar
        CurrentAvatar.Target = avatarSlot;
        IsUsingDefaultAvatar.Value = false;

        // Add to available avatars if not already there
        if (!AvailableAvatars.Contains(avatarSlot))
        {
            AvailableAvatars.Add(avatarSlot);
        }

        Logger.Log($"AvatarManager: Avatar equipped successfully");
        OnAvatarChanged?.Invoke(avatarSlot);

        return true;
    }

    /// <summary>
    /// Equip the default avatar.
    /// </summary>
    public void EquipDefaultAvatar()
    {
        Logger.Log("AvatarManager: Equipping default avatar");

        DequipCurrentAvatar();

        if (DefaultAvatarSlot.Target != null)
        {
            DefaultAvatarSlot.Target.ActiveSelf.Value = true;
            CurrentAvatar.Target = DefaultAvatarSlot.Target;
        }
        else
        {
            // Create default avatar
            var avatarSlot = Slot.AddSlot("DefaultAvatar");
            DefaultAVI.CreateDefaultAvatar(avatarSlot, UserRoot.Target);
            DefaultAvatarSlot.Target = avatarSlot;
            CurrentAvatar.Target = avatarSlot;
        }

        IsUsingDefaultAvatar.Value = true;
        OnAvatarChanged?.Invoke(CurrentAvatar.Target);
    }

    /// <summary>
    /// Dequip the current avatar (hide it but don't destroy).
    /// </summary>
    public void DequipCurrentAvatar()
    {
        if (CurrentAvatar.Target == null)
            return;

        Logger.Log($"AvatarManager: Dequipping avatar '{CurrentAvatar.Target.SlotName.Value}'");

        // Just hide the current avatar
        CurrentAvatar.Target.ActiveSelf.Value = false;
        CurrentAvatar.Target = null;
    }

    /// <summary>
    /// Remove an avatar from available avatars and destroy it.
    /// </summary>
    public void RemoveAvatar(Slot avatarSlot)
    {
        if (avatarSlot == null)
            return;

        // If it's the current avatar, switch to default
        if (CurrentAvatar.Target == avatarSlot)
        {
            EquipDefaultAvatar();
        }

        // Don't destroy the default avatar
        if (avatarSlot == DefaultAvatarSlot.Target)
            return;

        AvailableAvatars.Remove(avatarSlot);
        avatarSlot.Destroy();

        Logger.Log($"AvatarManager: Removed avatar");
    }

    /// <summary>
    /// Get the index of an avatar in the available list.
    /// </summary>
    public int GetAvatarIndex(Slot avatarSlot)
    {
        for (int i = 0; i < AvailableAvatars.Count; i++)
        {
            if (AvailableAvatars[i] == avatarSlot)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Cycle to the next available avatar.
    /// </summary>
    public void CycleNextAvatar()
    {
        if (AvailableAvatars.Count == 0)
        {
            EquipDefaultAvatar();
            return;
        }

        int currentIndex = GetAvatarIndex(CurrentAvatar.Target);
        int nextIndex = (currentIndex + 1) % AvailableAvatars.Count;

        EquipAvatar(AvailableAvatars[nextIndex]);
    }

    /// <summary>
    /// Cycle to the previous available avatar.
    /// </summary>
    public void CyclePreviousAvatar()
    {
        if (AvailableAvatars.Count == 0)
        {
            EquipDefaultAvatar();
            return;
        }

        int currentIndex = GetAvatarIndex(CurrentAvatar.Target);
        int prevIndex = currentIndex - 1;
        if (prevIndex < 0) prevIndex = AvailableAvatars.Count - 1;

        EquipAvatar(AvailableAvatars[prevIndex]);
    }

    /// <summary>
    /// Import and equip an avatar from a file path.
    /// </summary>
    public async void ImportAndEquipAvatar(string filePath, LocalDB localDB = null)
    {
        Logger.Log($"AvatarManager: Importing avatar from '{filePath}'");

        try
        {
            // Create a temporary slot for the import
            var importSlot = Slot.AddSlot("ImportedAvatar");

            var result = await ModelImporter.ImportAvatarAsync(filePath, importSlot, localDB);

            if (result.Success && result.RootSlot != null)
            {
                // The avatar is now loaded, equip it
                EquipAvatar(result.RootSlot);
                Logger.Log("AvatarManager: Avatar imported and equipped");
            }
            else
            {
                Logger.Error($"AvatarManager: Failed to import avatar: {result.ErrorMessage}");
                importSlot.Destroy();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"AvatarManager: Exception importing avatar: {ex.Message}");
        }
    }
}

using System;
using System.Collections.Generic;
using Lumora.Core.Networking.Sync;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

/// <summary>
/// Head output device types.
/// </summary>
public enum HeadOutputDevice
{
    Server,
    Screen,
    VR,
    Camera
}

/// <summary>
/// Platform types.
/// </summary>
public enum Platform
{
    Windows,
    Linux,
    Android,
    Other
}

/// <summary>
/// Represents a user in the world.
/// </summary>
public class User : ISyncObject, IWorldElement, IDisposable
{
    private RefID _referenceID;
    private List<ISyncMember> _syncMembers;

    // Sync members - auto-discovered
    public readonly Sync<string> UserName = new();
    public readonly Sync<string> UserID = new();
    public readonly Sync<string> MachineID = new();
    public readonly Sync<ulong> AllocationIDStart = new();
    public readonly Sync<ulong> AllocationIDEnd = new();
    public readonly Sync<byte> AllocationID = new();
    public readonly Sync<int> Ping = new();
    public readonly Sync<bool> IsPresent = new();
    public readonly Sync<bool> IsSilenced = new();
    public readonly Sync<bool> IsMuted = new();
    public readonly Sync<bool> VRActive = new();
    public readonly Sync<bool> PresentInWorld = new();
    public readonly Sync<bool> PresentInHeadset = new();
    public readonly Sync<bool> EditMode = new();
    public readonly Sync<float> FPS = new();
    public readonly Sync<HeadOutputDevice> HeadDevice = new();
    public readonly Sync<Platform> UserPlatform = new();
    public readonly Sync<float> DownloadSpeed = new();
    public readonly Sync<float> UploadSpeed = new();
    public readonly Sync<ulong> DownloadedBytes = new();
    public readonly Sync<ulong> UploadedBytes = new();

    // Network statistics (non-synced)
    public ulong SentBytes { get; set; }
    public ulong ReceivedBytes { get; set; }
    public DateTime LastSyncMessage { get; set; }
    public int ImmediateDeltaCount { get; set; }
    public int ImmediateControlCount { get; set; }
    public int ImmediateStreamCount { get; set; }

    // ISyncObject implementation
    public List<ISyncMember> SyncMembers => _syncMembers;
    public RefID ReferenceID => _referenceID;
    public ulong RefIdNumeric => (ulong)_referenceID;
    public bool IsAuthority => World?.IsAuthority ?? false;

    // IWorldElement implementation
    public World World { get; private set; }
    public bool IsDestroyed { get; private set; }
    public bool IsInitialized { get; private set; } = true;
    public bool IsLocalElement => ReferenceID.IsLocalID;
    public bool IsPersistent => true;

    /// <summary>
    /// Hierarchy path for debugging.
    /// </summary>
    public string ParentHierarchyToString() => $"User:{UserName.Value ?? UserID.Value}";

    public bool IsHost => World?.IsAuthority == true && World.LocalUser == this;
    public bool IsDisposed { get; private set; }

    public void Destroy()
    {
        Dispose();
    }

    // Stream control
    public bool ReceiveStreams { get; set; } = true;
    public Lumora.Core.Networking.Sync.UserStreamBag StreamBag { get; private set; } = new();

    // UserRoot reference
    public Slot UserRootSlot { get; set; }

    public User(World world, RefID refID)
    {
        World = world;
        _referenceID = refID;

        // Discover sync members
        _syncMembers = SyncMemberDiscovery.DiscoverSyncMembers(this);

        AquaLogger.Debug($"User created with {_syncMembers.Count} sync members");
    }

    /// <summary>
    /// Set user name (authority or local user only).
    /// </summary>
    public void SetUserName(string name)
    {
        if (IsAuthority || World.LocalUser == this)
        {
            UserName.Value = name;
        }
        else
        {
            AquaLogger.Warn("Non-authority user cannot change username");
        }
    }

    /// <summary>
    /// Get all dirty sync members that need to be sent over network.
    /// </summary>
    public List<ISyncMember> GetDirtySyncMembers()
    {
        return SyncMemberDiscovery.GetDirtySyncMembers(_syncMembers);
    }

    /// <summary>
    /// Clear dirty flags after sync.
    /// </summary>
    public void ClearDirtyFlags()
    {
        SyncMemberDiscovery.ClearDirtyFlags(_syncMembers);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        IsDestroyed = true;
        World = null;
    }

    public override string ToString()
    {
        return $"User({UserName.Value ?? UserID.Value}, ID={ReferenceID})";
    }
}

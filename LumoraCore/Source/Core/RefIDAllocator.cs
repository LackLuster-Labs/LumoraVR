using System;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

/// <summary>
/// Manages RefID range allocation for users.
/// Each user gets a unique byte (1-253) for their ID domain.
/// Authority uses byte 0, local objects use byte 254.
/// </summary>
public class RefIDAllocator
{
    private byte _nextUserByte = RefIDConstants.FIRST_USER_BYTE;
    private readonly World _world;
    private readonly object _lock = new object();

    // Track latest position for each user byte (for save/load)
    private readonly ulong[] _latestUserPosition = new ulong[256];

    // Track which user bytes are allocated and to whom
    private readonly Dictionary<byte, User> _userByteToUser = new Dictionary<byte, User>();

    // Statistics
    private long _totalAllocations;
    private long _authorityAllocations;
    private long _localAllocations;

    public RefIDAllocator(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));

        for (int i = 0; i < 256; i++)
        {
            _latestUserPosition[i] = 1;
        }
    }

    #region Allocation

    /// <summary>
    /// Allocate an ID range for a new user.
    /// </summary>
    public RefIDRange AllocateUserIDRange(User user = null)
    {
        lock (_lock)
        {
            if (_nextUserByte > RefIDConstants.MAX_USER_BYTE)
            {
                throw new InvalidOperationException(
                    $"Ran out of user allocation bytes! Maximum {RefIDConstants.MAX_USER_BYTE} users supported.");
            }

            byte userByte = _nextUserByte++;
            var range = RefIDRange.ForUserByte(userByte);

            if (user != null)
            {
                _userByteToUser[userByte] = user;
            }

            AquaLogger.Log($"Allocated ID range for user byte {userByte}: {range}");

            return range;
        }
    }

    /// <summary>
    /// Allocate an ID range for a new user (tuple version for compatibility).
    /// </summary>
    public (RefID start, RefID end) AllocateUserIDRangeTuple(User user = null)
    {
        var range = AllocateUserIDRange(user);
        return (range.Start, range.End);
    }

    /// <summary>
    /// Get the authority's ID range (always uses byte 0).
    /// </summary>
    public RefIDRange GetAuthorityIDRange()
    {
        return RefIDRange.Authority;
    }

    /// <summary>
    /// Get the local allocation ID range.
    /// </summary>
    public RefIDRange GetLocalIDRange()
    {
        return RefIDRange.Local;
    }

    /// <summary>
    /// Release a user's allocation when they leave.
    /// Note: IDs are not actually reclaimed, but tracking is updated.
    /// </summary>
    public void ReleaseUserAllocation(byte userByte)
    {
        lock (_lock)
        {
            _userByteToUser.Remove(userByte);
            AquaLogger.Log($"Released allocation for user byte {userByte}");
        }
    }

    #endregion

    #region Position Tracking

    /// <summary>
    /// Update latest position tracking for a user byte.
    /// Called after allocation to track highest used position.
    /// </summary>
    public void UpdateLatestPosition(RefID id)
    {
        byte userByte = id.GetUserByte();
        ulong position = id.GetPosition();

        lock (_lock)
        {
            if (position > _latestUserPosition[userByte])
            {
                _latestUserPosition[userByte] = position;
            }

            // Update statistics
            _totalAllocations++;
            if (userByte == RefIDConstants.AUTHORITY_BYTE)
                _authorityAllocations++;
            else if (userByte == RefIDConstants.LOCAL_BYTE)
                _localAllocations++;
        }
    }

    /// <summary>
    /// Get the latest position for a user byte.
    /// Used when resuming allocation after load.
    /// </summary>
    public ulong GetLatestPosition(byte userByte)
    {
        lock (_lock)
        {
            return _latestUserPosition[userByte];
        }
    }

    /// <summary>
    /// Set the latest position for a user byte.
    /// Used when loading world state.
    /// </summary>
    public void SetLatestPosition(byte userByte, ulong position)
    {
        lock (_lock)
        {
            _latestUserPosition[userByte] = position;
        }
    }

    /// <summary>
    /// Get the next ID that would be allocated for a user byte.
    /// </summary>
    public RefID PeekNextID(byte userByte)
    {
        lock (_lock)
        {
            return RefID.Construct(userByte, _latestUserPosition[userByte] + 1);
        }
    }

    #endregion

    #region Query Methods

    /// <summary>
    /// Get the number of user bytes already allocated.
    /// </summary>
    public int AllocatedUserCount
    {
        get
        {
            lock (_lock)
            {
                return _nextUserByte - RefIDConstants.FIRST_USER_BYTE;
            }
        }
    }

    /// <summary>
    /// Get the maximum number of users supported.
    /// </summary>
    public int MaxUserCount => RefIDConstants.MAX_USERS;

    /// <summary>
    /// Get the number of remaining user slots.
    /// </summary>
    public int RemainingUserSlots => MaxUserCount - AllocatedUserCount;

    /// <summary>
    /// Check if more users can be allocated.
    /// </summary>
    public bool CanAllocateMoreUsers => RemainingUserSlots > 0;

    /// <summary>
    /// Get the user associated with a user byte.
    /// </summary>
    public User GetUserForByte(byte userByte)
    {
        lock (_lock)
        {
            return _userByteToUser.TryGetValue(userByte, out var user) ? user : null;
        }
    }

    /// <summary>
    /// Get the user that owns a specific RefID.
    /// </summary>
    public User GetOwner(RefID refId)
    {
        if (refId.IsNull || refId.IsLocalID || refId.IsAuthorityID)
            return null;

        return GetUserForByte(refId.GetUserByte());
    }

    /// <summary>
    /// Check if a RefID belongs to a specific user.
    /// </summary>
    public bool BelongsToUser(RefID refId, User user)
    {
        if (user == null || refId.IsNull)
            return false;

        byte userByte = refId.GetUserByte();
        lock (_lock)
        {
            return _userByteToUser.TryGetValue(userByte, out var owner) && owner == user;
        }
    }

    /// <summary>
    /// Get IDs allocated count for a specific user byte.
    /// </summary>
    public ulong GetAllocationCount(byte userByte)
    {
        lock (_lock)
        {
            return _latestUserPosition[userByte];
        }
    }

    /// <summary>
    /// Get the utilization percentage for a user byte's allocation space.
    /// </summary>
    public double GetUtilization(byte userByte)
    {
        lock (_lock)
        {
            return (double)_latestUserPosition[userByte] / RefID.MaxPosition * 100.0;
        }
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Total RefIDs allocated across all domains.
    /// </summary>
    public long TotalAllocations
    {
        get { lock (_lock) return _totalAllocations; }
    }

    /// <summary>
    /// RefIDs allocated in authority domain.
    /// </summary>
    public long AuthorityAllocations
    {
        get { lock (_lock) return _authorityAllocations; }
    }

    /// <summary>
    /// RefIDs allocated in local domain.
    /// </summary>
    public long LocalAllocations
    {
        get { lock (_lock) return _localAllocations; }
    }

    /// <summary>
    /// Get diagnostic information about the allocator.
    /// </summary>
    public string GetDiagnostics()
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"RefIDAllocator Diagnostics:");
            sb.AppendLine($"  Users allocated: {AllocatedUserCount}/{MaxUserCount}");
            sb.AppendLine($"  Total allocations: {_totalAllocations}");
            sb.AppendLine($"  Authority allocations: {_authorityAllocations}");
            sb.AppendLine($"  Local allocations: {_localAllocations}");
            sb.AppendLine($"  Authority position: {_latestUserPosition[RefIDConstants.AUTHORITY_BYTE]}");
            sb.AppendLine($"  Local position: {_latestUserPosition[RefIDConstants.LOCAL_BYTE]}");

            foreach (var kvp in _userByteToUser)
            {
                sb.AppendLine($"  User byte {kvp.Key}: {kvp.Value?.UserName ?? "Unknown"} (pos: {_latestUserPosition[kvp.Key]})");
            }

            return sb.ToString();
        }
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Reset the allocator to initial state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _nextUserByte = RefIDConstants.FIRST_USER_BYTE;
            _userByteToUser.Clear();
            _totalAllocations = 0;
            _authorityAllocations = 0;
            _localAllocations = 0;

            for (int i = 0; i < 256; i++)
            {
                _latestUserPosition[i] = 1;
            }

            AquaLogger.Log("RefIDAllocator reset");
        }
    }

    #endregion
}

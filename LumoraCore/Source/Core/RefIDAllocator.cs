using System;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

/// <summary>
/// Manages RefID range allocation for users.
/// Allocation block management is handled by ReferenceController.
/// </summary>
public class RefIDAllocator
{
    private byte _nextUserByte = RefIDConstants.FIRST_USER_BYTE;
    private readonly World _world;

    // Track latest position for each user byte (for save/load)
    private readonly ulong[] _latestUserPosition = new ulong[256];

    public RefIDAllocator(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));

        for (int i = 0; i < 256; i++)
        {
            _latestUserPosition[i] = 1;
        }
    }

    /// <summary>
    /// Allocate an ID range for a new user.
    /// </summary>
    public (RefID start, RefID end) AllocateUserIDRange()
    {
        if (_nextUserByte > RefIDConstants.MAX_USER_BYTE)
        {
            throw new InvalidOperationException(
                $"Ran out of user allocation bytes! Maximum {RefIDConstants.MAX_USER_BYTE} users supported.");
        }

        byte userByte = _nextUserByte++;

        RefID start = RefID.Construct(userByte, 1);
        RefID end = CalculateIDRangeEnd(userByte);

        AquaLogger.Log($"Allocated ID range for user byte {userByte}: {start} - {end}");

        return (start, end);
    }

    /// <summary>
    /// Get the authority's ID range (always uses byte 0).
    /// </summary>
    public (RefID start, RefID end) GetAuthorityIDRange()
    {
        RefID start = RefID.Construct(RefIDConstants.AUTHORITY_BYTE, 1);
        RefID end = CalculateIDRangeEnd(RefIDConstants.AUTHORITY_BYTE);
        return (start, end);
    }

    /// <summary>
    /// Get the local allocation ID range.
    /// </summary>
    public (RefID start, RefID end) GetLocalIDRange()
    {
        RefID start = RefID.Construct(RefIDConstants.LOCAL_BYTE, 1);
        RefID end = CalculateIDRangeEnd(RefIDConstants.LOCAL_BYTE);
        return (start, end);
    }

    /// <summary>
    /// Calculate the ending ID (exclusive) for a given user byte.
    /// </summary>
    private static RefID CalculateIDRangeEnd(byte userByte)
    {
        // Handle overflow for max byte value
        if (userByte >= 255)
        {
            return new RefID(ulong.MaxValue);
        }
        return RefID.Construct((byte)(userByte + 1), 0);
    }

    /// <summary>
    /// Update latest position tracking for a user byte.
    /// Called after allocation to track highest used position.
    /// </summary>
    public void UpdateLatestPosition(RefID id)
    {
        byte userByte = id.GetUserByte();
        ulong position = id.GetPosition();

        if (position > _latestUserPosition[userByte])
        {
            _latestUserPosition[userByte] = position;
        }
    }

    /// <summary>
    /// Get the latest position for a user byte.
    /// Used when resuming allocation after load.
    /// </summary>
    public ulong GetLatestPosition(byte userByte)
    {
        return _latestUserPosition[userByte];
    }

    /// <summary>
    /// Set the latest position for a user byte.
    /// Used when loading world state.
    /// </summary>
    public void SetLatestPosition(byte userByte, ulong position)
    {
        _latestUserPosition[userByte] = position;
    }

    /// <summary>
    /// Reset the allocator.
    /// </summary>
    public void Reset()
    {
        _nextUserByte = RefIDConstants.FIRST_USER_BYTE;

        for (int i = 0; i < 256; i++)
        {
            _latestUserPosition[i] = 1;
        }

        AquaLogger.Log("RefIDAllocator reset");
    }

    /// <summary>
    /// Get the number of user bytes already allocated.
    /// </summary>
    public int GetAllocatedUserCount()
    {
        return _nextUserByte - RefIDConstants.FIRST_USER_BYTE;
    }

    /// <summary>
    /// Get the maximum number of users supported.
    /// </summary>
    public int GetMaxUserCount()
    {
        return RefIDConstants.MAX_USER_BYTE;
    }
}

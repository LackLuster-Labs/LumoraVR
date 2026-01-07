using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

/// <summary>
/// Central controller for world element references.
/// Handles registration, lookup, async resolution, and allocation context management.
/// Supports allocation blocking during decode/init.
/// </summary>
public class ReferenceController : IDisposable
{
    /// <summary>
    /// If true, new RefID allocations are blocked to prevent wrong-context creation.
    /// </summary>
    public bool BlockAllocations { get; set; }

    /// <summary>
    /// Nesting counter for allocation blocking.
    /// Allocations are blocked when this is > 0.
    /// </summary>
    private int _allocationBlockCount;

    /// <summary>
    /// Whether allocations are hard-blocked (will throw).
    /// Soft blocks during OnAwake only warn.
    /// </summary>
    public bool AllocationsBlocked => BlockAllocations;

    /// <summary>
    /// Whether we're in a soft-block context (OnAwake) that will warn on allocation.
    /// </summary>
    public bool InSoftBlockContext => _allocationBlockCount > 0;

    /// <summary>
    /// Whether the world is currently decoding a batch (slots/components should defer full init).
    /// </summary>
    public bool IsDecodingBatch { get; private set; }

    /// <summary>
    /// Begin blocking allocations (can be nested).
    /// Used during OnAwake to prevent components from creating new objects.
    /// </summary>
    public void BlockAllocationsStart()
    {
        _allocationBlockCount++;
    }

    /// <summary>
    /// End blocking allocations (can be nested).
    /// </summary>
    public void BlockAllocationsEnd()
    {
        if (_allocationBlockCount > 0)
            _allocationBlockCount--;
    }

    /// <summary>
    /// Begin batch decode mode. Slots/components should defer full initialization.
    /// </summary>
    public void BeginBatchDecode()
    {
        IsDecodingBatch = true;
    }

    /// <summary>
    /// End batch decode mode.
    /// </summary>
    public void EndBatchDecode()
    {
        IsDecodingBatch = false;
    }

    // Object registry
    private readonly Dictionary<RefID, IWorldElement> _objects = new();
    
    // Pending reference requests (for objects not yet created)
    private readonly Dictionary<RefID, List<IWorldElementReceiver>> _pendingRequests = new();
    
    // Allocation context stack
    private readonly Stack<AllocationContext> _allocationStack = new();
    private AllocationContext _currentAllocation;
    
    // Local allocation tracking
    private ulong _localAllocationPosition = 1;
    private int _localAllocationDepth;
    
    // Trash bin integration (for restore from trash)
    private readonly Dictionary<RefID, TrashEntry> _trashedObjects = new();
    
    /// <summary>
    /// The world this controller belongs to.
    /// </summary>
    public World World { get; }
    
    /// <summary>
    /// All registered objects.
    /// </summary>
    public IEnumerable<KeyValuePair<RefID, IWorldElement>> AllObjects => _objects;
    
    /// <summary>
    /// Number of registered objects.
    /// </summary>
    public int ObjectCount => _objects.Count;
    
    /// <summary>
    /// Number of pending reference requests.
    /// </summary>
    public int PendingRequestCount => _pendingRequests.Count;
    
    /// <summary>
    /// Current allocation context info (for debugging).
    /// </summary>
    public string AllocationContextInfo => 
        $"User:{_currentAllocation.UserByte} Pos:{_currentAllocation.Position} Depth:{_allocationStack.Count}";
    
    public ReferenceController(World world)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        
        // Initialize with authority allocation context
        _currentAllocation = new AllocationContext
        {
            UserByte = RefIDConstants.AUTHORITY_BYTE,
            Position = 1,
            NestedDepth = 0
        };
    }
    
    #region Object Registration
    
    /// <summary>
    /// Register a world element with the controller.
    /// Called during element initialization.
    /// </summary>
    public void RegisterObject(IWorldElement element)
    {
        if (element == null)
            throw new ArgumentNullException(nameof(element));
        
        RefID id = element.ReferenceID;
        
        if (id.IsNull)
            throw new ArgumentException("Cannot register element with null RefID", nameof(element));
        
        if (_objects.ContainsKey(id))
        {
            var existing = _objects[id];
            throw new InvalidOperationException(
                $"RefID collision! {id} already registered to {existing.GetType().Name}, " +
                $"cannot register {element.GetType().Name}");
        }
        
        _objects[id] = element;
        
        // Process any pending requests for this ID
        if (_pendingRequests.TryGetValue(id, out var receivers))
        {
            _pendingRequests.Remove(id);
            foreach (var receiver in receivers)
            {
                try
                {
                    receiver.OnWorldElementAvailable(element);
                }
                catch (Exception ex)
                {
                    AquaLogger.Error($"Exception in OnWorldElementAvailable: {ex}");
                }
            }
            // Return list to pool if using pooling
        }
    }
    
    /// <summary>
    /// Unregister a world element from the controller.
    /// Called during element destruction.
    /// </summary>
    public void UnregisterObject(IWorldElement element)
    {
        if (element == null) return;
        _objects.Remove(element.ReferenceID);
    }
    
    /// <summary>
    /// Unregister by RefID directly.
    /// </summary>
    public void UnregisterObject(in RefID id)
    {
        _objects.Remove(id);
    }
    
    #endregion
    
    #region Object Lookup
    
    /// <summary>
    /// Get an object by RefID, or null if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IWorldElement GetObjectOrNull(in RefID id)
    {
        _objects.TryGetValue(id, out var element);
        return element;
    }
    
    /// <summary>
    /// Get an object by RefID, or throw if not found.
    /// </summary>
    public IWorldElement GetObjectOrThrow(in RefID id)
    {
        if (_objects.TryGetValue(id, out var element))
            return element;
        
        throw new KeyNotFoundException($"No object found with RefID {id}");
    }
    
    /// <summary>
    /// Try to get an object of specific type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetObject<T>(in RefID id, out T element) where T : class, IWorldElement
    {
        if (_objects.TryGetValue(id, out var obj) && obj is T typed)
        {
            element = typed;
            return true;
        }
        element = null;
        return false;
    }
    
    /// <summary>
    /// Check if an object exists with the given RefID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsObject(in RefID id)
    {
        return _objects.ContainsKey(id);
    }
    
    #endregion
    
    #region Async Reference Resolution
    
    /// <summary>
    /// Request an object by RefID. If available, callback is invoked immediately.
    /// If not yet available, callback is invoked when object is registered.
    /// </summary>
    public void RequestObject(in RefID id, IWorldElementReceiver receiver)
    {
        if (id.IsNull)
            return;
        
        if (receiver == null)
            throw new ArgumentNullException(nameof(receiver));
        
        // Check if already available
        if (_objects.TryGetValue(id, out var element))
        {
            receiver.OnWorldElementAvailable(element);
            return;
        }
        
        // Queue for later
        if (!_pendingRequests.TryGetValue(id, out var list))
        {
            list = new List<IWorldElementReceiver>();
            _pendingRequests[id] = list;
        }
        list.Add(receiver);
    }
    
    /// <summary>
    /// Cancel a pending request.
    /// </summary>
    public void CancelRequest(in RefID id, IWorldElementReceiver receiver)
    {
        if (_pendingRequests.TryGetValue(id, out var list))
        {
            list.Remove(receiver);
            if (list.Count == 0)
            {
                _pendingRequests.Remove(id);
            }
        }
    }
    
    #endregion
    
    #region Allocation Block Management
    
    /// <summary>
    /// Begin an allocation block at a specific RefID.
    /// Used when receiving objects from network or loading from save.
    /// </summary>
    public void AllocationBlockBegin(in RefID id)
    {
        CheckAllocation();
        byte userByte = id.GetUserByte();
        ulong position = id.GetPosition();

        if (_currentAllocation.UserByte == userByte && _currentAllocation.Position == position)
        {
            _currentAllocation.NestedDepth++;
            return;
        }

        _allocationStack.Push(_currentAllocation);

        _currentAllocation = new AllocationContext
        {
            UserByte = userByte,
            Position = position,
            NestedDepth = 0
        };
    }
    
    /// <summary>
    /// End the current allocation block and restore previous context.
    /// </summary>
    public void AllocationBlockEnd()
    {
        if (_currentAllocation.NestedDepth > 0)
        {
            _currentAllocation.NestedDepth--;
            return;
        }

        if (_allocationStack.Count == 0)
        {
            throw new InvalidOperationException("Allocation stack is empty, cannot end block!");
        }
        
        _currentAllocation = _allocationStack.Pop();
    }
    
    /// <summary>
    /// Begin a local allocation block for non-networked objects.
    /// </summary>
    public void LocalAllocationBlockBegin()
    {
        CheckAllocation();
        if (_currentAllocation.UserByte == RefIDConstants.LOCAL_BYTE)
        {
            _localAllocationDepth++;
            return;
        }

        _allocationStack.Push(_currentAllocation);
        _currentAllocation = new AllocationContext
        {
            UserByte = RefIDConstants.LOCAL_BYTE,
            Position = _localAllocationPosition,
            NestedDepth = 0
        };
        _localAllocationDepth = 1;
    }
    
    /// <summary>
    /// End local allocation block.
    /// </summary>
    public void LocalAllocationBlockEnd()
    {
        if (_currentAllocation.UserByte != RefIDConstants.LOCAL_BYTE)
        {
            throw new InvalidOperationException("Not in local allocation block!");
        }

        if (_localAllocationDepth > 1)
        {
            _localAllocationDepth--;
            return;
        }

        _localAllocationDepth = 0;

        // Save local position for next local block
        _localAllocationPosition = _currentAllocation.Position;

        if (_allocationStack.Count == 0)
        {
            throw new InvalidOperationException("Allocation stack is empty!");
        }

        _currentAllocation = _allocationStack.Pop();
    }
    
    /// <summary>
    /// Allocate the next RefID from current context.
    /// </summary>
    public RefID AllocateID()
    {
        CheckAllocation();
        RefID id = RefID.Construct(_currentAllocation.UserByte, _currentAllocation.Position);
        _currentAllocation.Position++;
        return id;
    }
    
    /// <summary>
    /// Peek at the next RefID without allocating.
    /// </summary>
    public RefID PeekID()
    {
        CheckAllocation();
        return RefID.Construct(_currentAllocation.UserByte, _currentAllocation.Position);
    }
    
    /// <summary>
    /// Set the allocation context directly.
    /// Used by RefIDAllocator for user range management.
    /// </summary>
    public void SetAllocationContext(byte userByte, ulong position)
    {
        CheckAllocation();
        _currentAllocation = new AllocationContext
        {
            UserByte = userByte,
            Position = position,
            NestedDepth = 0
        };
    }
    
    /// <summary>
    /// Get current allocation user byte.
    /// </summary>
    public byte CurrentUserByte => _currentAllocation.UserByte;
    
    /// <summary>
    /// Get current allocation position.
    /// </summary>
    public ulong CurrentPosition => _currentAllocation.Position;
    
    /// <summary>
    /// Whether currently in a local allocation block.
    /// </summary>
    public bool IsInLocalAllocation => _currentAllocation.UserByte == RefIDConstants.LOCAL_BYTE;
    
    #endregion
    
    #region Trash Integration
    
    /// <summary>
    /// Try to retrieve an object from trash during sync.
    /// Used when receiving delete confirmations that need rollback.
    /// </summary>
    public IWorldElement TryRetrieveFromTrash(ulong tick, RefID id)
    {
        if (_trashedObjects.TryGetValue(id, out var entry) && entry.TrashTick <= tick)
        {
            _trashedObjects.Remove(id);
            return entry.Element;
        }
        return null;
    }
    
    /// <summary>
    /// Move an object to trash (pending deletion confirmation).
    /// </summary>
    public void MoveToTrash(IWorldElement element, ulong tick)
    {
        if (element == null) return;
        
        _trashedObjects[element.ReferenceID] = new TrashEntry
        {
            Element = element,
            TrashTick = tick
        };
        
        UnregisterObject(element);
    }
    
    /// <summary>
    /// Permanently delete from trash.
    /// </summary>
    public void DeleteFromTrash(RefID id)
    {
        _trashedObjects.Remove(id);
    }
    
    /// <summary>
    /// Restore from trash (deletion rejected).
    /// </summary>
    public bool RestoreFromTrash(RefID id)
    {
        if (_trashedObjects.TryGetValue(id, out var entry))
        {
            _trashedObjects.Remove(id);
            RegisterObject(entry.Element);
            return true;
        }
        return false;
    }
    
    #endregion
    
    #region Cleanup
    
    /// <summary>
    /// Clear all pending requests (used during world shutdown).
    /// </summary>
    public void ClearPendingRequests()
    {
        _pendingRequests.Clear();
    }
    
    /// <summary>
    /// Reset the controller (used when resetting world state).
    /// </summary>
    public void Reset()
    {
        _objects.Clear();
        _pendingRequests.Clear();
        _trashedObjects.Clear();
        _allocationStack.Clear();
        _localAllocationPosition = 1;
        _localAllocationDepth = 0;
        BlockAllocations = false;
        _allocationBlockCount = 0;
        IsDecodingBatch = false;

        _currentAllocation = new AllocationContext
        {
            UserByte = RefIDConstants.AUTHORITY_BYTE,
            Position = 1,
            NestedDepth = 0
        };
    }
    
    public void Dispose()
    {
        Reset();
    }
    
    #endregion

    private void CheckAllocation()
    {
        // Warn about allocations during OnAwake context
        // but don't throw - some components legitimately need to create sync members
        if (_allocationBlockCount > 0)
        {
            AquaLogger.Warn(
                "RefID allocation during OnAwake context. This may indicate a component " +
                "creating new objects during initialization. Consider deferring to OnInit.");
        }

        // The hard block (BlockAllocations property) still throws
        if (BlockAllocations)
        {
            throw new InvalidOperationException(
                "New RefID allocations are currently blocked. Are you accidentally " +
                "creating new objects in the wrong context?");
        }
    }
    
    #region Internal Types
    
    private struct AllocationContext
    {
        public byte UserByte;
        public ulong Position;
        public int NestedDepth;
    }
    
    private struct TrashEntry
    {
        public IWorldElement Element;
        public ulong TrashTick;
    }
    
    #endregion
}

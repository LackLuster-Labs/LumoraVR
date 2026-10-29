// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

public class ReferenceController : IDisposable
{
    public bool BlockAllocations { get; set; }

    private int _allocationBlockCount;

    // Hard block, throws. The OnAwake soft block only warns.
    public bool AllocationsBlocked => BlockAllocations;

    public bool InSoftBlockContext => _allocationBlockCount > 0;

    // Slots and components defer full init while a batch decodes.
    public bool IsDecodingBatch { get; private set; }

    // Nests. Wraps OnAwake, where creating objects only warns.
    public void BlockAllocationsStart()
    {
        _allocationBlockCount++;
    }

    public void BlockAllocationsEnd()
    {
        if (_allocationBlockCount > 0)
            _allocationBlockCount--;
    }

    public void BeginBatchDecode()
    {
        IsDecodingBatch = true;
    }

    public void EndBatchDecode()
    {
        IsDecodingBatch = false;
    }

    private readonly RefIdMap<IWorldElement> _objects = new();
    
    private readonly Dictionary<RefID, List<IWorldElementReceiver>> _pendingRequests = new();
    
    private readonly Stack<AllocationContext> _allocationStack = new();
    private AllocationContext _currentAllocation;
    
    private ulong _localAllocationPosition = 1;
    private int _localAllocationDepth;

    // Per-user-byte high-water position for owned allocation blocks. Seeded from the join grant via
    // SetOwnedStartPosition, carried forward across re-entry so an owned block always mints fresh IDs. -xlinka
    private readonly ulong[] _ownedAllocationPosition = new ulong[256];
    private ulong GetOwnedPosition(byte b) => _ownedAllocationPosition[b] == 0 ? 1UL : _ownedAllocationPosition[b];

    // Highest position ever REGISTERED per byte, whoever minted it. A joiner mints its own IDs client
    // side and they only reach us as registrations, so this is the only place that sees how far a
    // user's byte actually got. The allocator reads it to start a recycled byte past the previous
    // occupant instead of replaying their positions. Deliberately not cleared by PurgeUserByte:
    // forgetting the mark is exactly what would let a rejoin collide with in-flight references to the
    // old objects. -xlinka
    private readonly ulong[] _latestUserPosition = new ulong[256];

    // Per user byte, where the host's range for that user's scripts starts, and where the next thing they
    // make is minted. Set from the join grant on every machine that knows the grant; zero is no range.
    // A user's own machine never mints at or past the fence, and the authority refuses it if it tries,
    // so what the host mints there can never meet an id the user minted. -xlinka
    private readonly ulong[] _scriptFence = new ulong[256];
    private readonly ulong[] _scriptPosition = new ulong[256];
    
    private readonly Dictionary<RefID, TrashEntry> _trashedObjects = new();
    
    public World World { get; }
    
    public IEnumerable<KeyValuePair<RefID, IWorldElement>> AllObjects => _objects;
    
    public int ObjectCount => _objects.Count;

    
    public int PendingRequestCount => _pendingRequests.Count;
    
    public string AllocationContextInfo => 
        $"User:{_currentAllocation.UserByte} Pos:{_currentAllocation.Position} Depth:{_allocationStack.Count}";
    
    public ReferenceController(World world)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        
        _currentAllocation = new AllocationContext
        {
            UserByte = RefIDConstants.AUTHORITY_BYTE,
            Position = 1,
            NestedDepth = 0
        };
    }
    
    #region Object Registration
    
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

        ulong position = id.GetPosition();
        byte userByte = id.GetUserByte();
        if (position > _latestUserPosition[userByte])
            _latestUserPosition[userByte] = position;

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
                    LumoraLogger.Error($"Exception in OnWorldElementAvailable: {ex}");
                }
            }
        }
    }
    
    public void UnregisterObject(IWorldElement element)
    {
        if (element == null) return;
        _objects.Remove(element.ReferenceID);
    }
    
    public void UnregisterObject(in RefID id)
    {
        _objects.Remove(id);
    }

    // Remove every registered object that lives in a user's RefID byte. Called when a user leaves or a
    // half-finished join is torn down, so the byte can be recycled to a future joiner without that joiner
    // colliding with the previous occupant's leftovers (streams, body slots, etc.). Without this, a leave
    // or a failed join leaves the streams registered and the next user on this byte throws
    // "RefID collision! User[NNN]:... already registered". -xlinka
    public int PurgeUserByte(byte userByte)
    {
        if (!RefIDConstants.IsValidUserByte(userByte))
            return 0;

        List<RefID> toRemove = null!;
        foreach (var key in _objects.Keys)
        {
            if (key.GetUserByte() == userByte)
                (toRemove ??= new List<RefID>()).Add(key);
        }

        if (toRemove == null)
            return 0;

        foreach (var key in toRemove)
            _objects.Remove(key);

        LumoraLogger.Log($"ReferenceController: purged {toRemove.Count} leftover objects for user byte {userByte}");
        return toRemove.Count;
    }
    
    #endregion
    
    #region Object Lookup
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IWorldElement GetObjectOrNull(in RefID id)
    {
        _objects.TryGetValue(id, out var element);
        return element!;
    }
    
    public IWorldElement GetObjectOrThrow(in RefID id)
    {
        if (_objects.TryGetValue(id, out var element))
            return element;
        
        throw new KeyNotFoundException($"No object found with RefID {id}");
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetObject<T>(in RefID id, out T element) where T : class, IWorldElement
    {
        if (_objects.TryGetValue(id, out var obj) && obj is T typed)
        {
            element = typed;
            return true;
        }
        element = null!;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsObject(in RefID id)
    {
        return _objects.ContainsKey(id);
    }
    
    #endregion
    
    #region Async Reference Resolution
    
    // Calls back inline when the object already exists, otherwise once it registers.
    public void RequestObject(in RefID id, IWorldElementReceiver receiver)
    {
        if (id.IsNull)
            return;
        
        if (receiver == null)
            throw new ArgumentNullException(nameof(receiver));
        
        if (_objects.TryGetValue(id, out var element))
        {
            receiver.OnWorldElementAvailable(element);
            return;
        }
        
        if (!_pendingRequests.TryGetValue(id, out var list))
        {
            list = new List<IWorldElementReceiver>();
            _pendingRequests[id] = list;
        }
        list.Add(receiver);
    }
    
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
    
    // Network decode and save load.
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
    
    // Allocation blocks as a using-scope, and the only way they should be opened from now on. A hand-written
    // Begin/End pair is SKIPPED by any exception thrown between the two, and the controller then keeps minting
    // in that context forever: one component throwing out of OnInit inside a local component attach left the
    // whole world allocating LOCAL ids, so every user root slot built afterwards was refused by User.Root with
    // "Cannot reference local targets from non-local reference". -xlinka
    public readonly struct AllocationScope : IDisposable
    {
        internal enum Kind : byte { None, Local, Block, Owned, Script }

        private readonly ReferenceController? _controller;
        private readonly Kind _kind;
        private readonly byte _userByte;

        internal AllocationScope(ReferenceController controller, Kind kind, byte userByte = 0)
        {
            _controller = controller;
            _kind = kind;
            _userByte = userByte;
        }

        public void Dispose()
        {
            if (_controller == null)
                return;

            switch (_kind)
            {
                case Kind.Local:
                    _controller.LocalAllocationBlockEnd();
                    break;
                case Kind.Block:
                    _controller.AllocationBlockEnd();
                    break;
                case Kind.Owned:
                    _controller.OwnedAllocationBlockEnd(_userByte);
                    break;
                case Kind.Script:
                    _controller.ScriptAllocationBlockEnd(_userByte);
                    break;
            }
        }
    }

    public AllocationScope LocalAllocationScope()
    {
        LocalAllocationBlockBegin();
        return new AllocationScope(this, AllocationScope.Kind.Local);
    }

    public AllocationScope AllocationScopeAt(in RefID id)
    {
        AllocationBlockBegin(id);
        return new AllocationScope(this, AllocationScope.Kind.Block);
    }

    public AllocationScope OwnedAllocationScope(byte userByte)
    {
        OwnedAllocationBlockBegin(userByte);
        return new AllocationScope(this, AllocationScope.Kind.Owned, userByte);
    }

    // On the host in a frozen world, a slot or component made under one of the host's own things while
    // nothing has opened a context of its own is the host's too: a part its item builds for itself later, a
    // child put under it, a component put on it. A guest's machine mints those in its own byte by default;
    // the host's default is the world's byte, where the freeze would refuse the host its own thing's parts.
    // Opens nothing on a client, in a world being built, or inside any explicit context (a decode, a load,
    // a script's range, a local block). -xlinka
    //
    // A joiner's things get the same on the host: what the host builds into one of them with no context of
    // its own (an avatar's hitboxes refitted while nobody wears it, a world script's additions) is a part of
    // that thing and lands in the host's range of the joiner's byte. Minted in the world's byte it
    // was world content inside a guest's item, which the guest's delete could not take with it, so every
    // peer was left with those parts standing at the world root. Only while that joiner is in the session.
    // -xlinka
    public AllocationScope InheritedAllocationScope(in RefID parentId)
    {
        if (_allocationStack.Count != 0 || _currentAllocation.UserByte != RefIDConstants.AUTHORITY_BYTE)
            return default;
        byte parentByte = parentId.GetUserByte();
        if (!RefIDConstants.IsValidUserByte(parentByte))
            return default;
        byte own = World.HostItemByte;
        if (own != 0 && parentByte == own)
            return OwnedAllocationScope(own);
        if (!World.IsAuthority || World.LocalUser?.HoldsByte(parentByte) != false || !HasScriptRoom(parentByte)
            || World.UserHoldingByte(parentByte) == null)
            return default;
        return ScriptAllocationScope(parentByte);
    }

    private bool HasScriptRoom(byte userByte)
        => _scriptFence[userByte] != 0 && _scriptPosition[userByte] < RefID.MaxPosition - RefIDConstants.USER_JOIN_HEADROOM;

    public void SetScriptFence(byte userByte, ulong position)
    {
        if (!RefIDConstants.IsValidUserByte(userByte))
            return;
        _scriptFence[userByte] = position;
        _scriptPosition[userByte] = position;
    }

    public ulong GetScriptFence(byte userByte) => _scriptFence[userByte];

    // Whether an id sits below its byte's script fence, where that byte's own user mints.
    public bool BelowScriptFence(in RefID id)
    {
        ulong fence = _scriptFence[id.GetUserByte()];
        return fence == 0 || id.GetPosition() < fence;
    }

    // On the host: everything minted inside lands in a user's script range, and the range moves past it
    // when the scope closes, whatever happened inside.
    public AllocationScope ScriptAllocationScope(byte userByte)
    {
        if (!RefIDConstants.IsValidUserByte(userByte) || _scriptFence[userByte] == 0)
            throw new InvalidOperationException($"user byte {userByte} has no script range");
        ulong position = _scriptPosition[userByte];
        if (position >= RefID.MaxPosition - RefIDConstants.USER_JOIN_HEADROOM)
            throw new InvalidOperationException($"user byte {userByte} has run out of script range");
        AllocationBlockBegin(RefID.Construct(userByte, position));
        return new AllocationScope(this, AllocationScope.Kind.Script, userByte);
    }

    private void ScriptAllocationBlockEnd(byte userByte)
    {
        if (_currentAllocation.UserByte == userByte && _currentAllocation.NestedDepth == 0
            && _currentAllocation.Position > _scriptPosition[userByte])
            _scriptPosition[userByte] = _currentAllocation.Position;
        AllocationBlockEnd();
    }

    // For an element that either carries an assigned id or belongs to a local parent; neither means it
    // allocates in whatever context the caller is already in, and the scope then does nothing. -xlinka
    public AllocationScope ElementAllocationScope(in RefID id, bool local)
    {
        if (id != RefID.Null)
            return AllocationScopeAt(id);
        if (local)
            return LocalAllocationScope();
        return default;
    }

    // Begin a local allocation block for non-networked objects. Prefer LocalAllocationScope. -xlinka
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

    // Open an allocation block in a specific user's networked namespace (their allocation byte). Unlike
    // LocalAllocationBlockBegin (byte 254, never networked) this mints real user-byte RefIDs that replicate,
    // so anything built here is OWNED by that user and visible to everyone. Position carries forward via the
    // per-byte high-water mark so re-entry keeps minting fresh IDs. -xlinka
    public void OwnedAllocationBlockBegin(byte userByte)
    {
        CheckAllocation();
        if (!RefIDConstants.IsValidUserByte(userByte))
            throw new ArgumentOutOfRangeException(nameof(userByte), "Owned block requires a real user byte (1-253)");

        // Never into a context at or past the byte's script fence: that is the decode of something the
        // host made for this user's scripts, and minting on from there would take ids the host is about to.
        if (_currentAllocation.UserByte == userByte
            && (_scriptFence[userByte] == 0 || _currentAllocation.Position < _scriptFence[userByte]))
        {
            _currentAllocation.NestedDepth++;
            return;
        }

        _allocationStack.Push(_currentAllocation);
        _currentAllocation = new AllocationContext
        {
            UserByte = userByte,
            Position = OwnedStart(userByte),
            NestedDepth = 0
        };
    }

    // A user's machine mints its byte from two cursors that start at the same granted position: its default
    // context, and the owned block's own for a block opened under some other context. So an owned block starts
    // past every context minting that byte, and on closing moves them past what it took; otherwise the two hand
    // out the same id. Only below the byte's script fence: past it is the host's. -xlinka
    private ulong OwnedStart(byte userByte)
    {
        ulong start = GetOwnedPosition(userByte);
        foreach (var context in _allocationStack)
        {
            if (context.UserByte == userByte && BelowFence(userByte, context.Position) && context.Position > start)
                start = context.Position;
        }
        return start;
    }

    private void CatchUp(byte userByte, ulong reached)
    {
        if (!BelowFence(userByte, reached))
            return;
        if (_currentAllocation.UserByte == userByte && _currentAllocation.Position < reached)
            _currentAllocation.Position = reached;

        bool behind = false;
        foreach (var context in _allocationStack)
        {
            if (context.UserByte == userByte && context.Position < reached)
            {
                behind = true;
                break;
            }
        }
        if (!behind)
            return;
        // The contexts are values, so the stack is rebuilt with those moved. ToArray is top first.
        var contexts = _allocationStack.ToArray();
        _allocationStack.Clear();
        for (int i = contexts.Length - 1; i >= 0; i--)
        {
            var context = contexts[i];
            if (context.UserByte == userByte && context.Position < reached)
                context.Position = reached;
            _allocationStack.Push(context);
        }
    }

    private bool BelowFence(byte userByte, ulong position) => _scriptFence[userByte] == 0 || position < _scriptFence[userByte];

    // End an owned allocation block and restore the previous context, saving the high-water position so a
    // later block in the same byte resumes above it. -xlinka
    public void OwnedAllocationBlockEnd(byte userByte)
    {
        if (_currentAllocation.UserByte != userByte)
            throw new InvalidOperationException("Not in the matching owned allocation block!");

        if (_currentAllocation.NestedDepth > 0)
        {
            _currentAllocation.NestedDepth--;
            return;
        }

        ulong reached = _currentAllocation.Position;
        _ownedAllocationPosition[userByte] = reached;
        if (_allocationStack.Count == 0)
            throw new InvalidOperationException("Allocation stack is empty!");
        _currentAllocation = _allocationStack.Pop();
        CatchUp(userByte, reached);
    }

    // Highest position registered so far in a user byte, or 0 when the byte has never been used.
    public ulong GetLatestPosition(byte userByte) => _latestUserPosition[userByte];

    // Seed the start position for a user's owned namespace from the join grant. Callers pass a BARE position
    // (already GetPosition()-decoded), never a packed RefID - see the JoinGrant handler. -xlinka
    public void SetOwnedStartPosition(byte userByte, ulong position)
    {
        if (!RefIDConstants.IsValidUserByte(userByte))
            return;
        _ownedAllocationPosition[userByte] = position == 0 ? 1UL : position;
    }

    public RefID AllocateID()
    {
        CheckAllocation();
        RefID id = RefID.Construct(_currentAllocation.UserByte, _currentAllocation.Position);
        _currentAllocation.Position++;
        return id;
    }
    
    public RefID PeekID()
    {
        CheckAllocation();
        return RefID.Construct(_currentAllocation.UserByte, _currentAllocation.Position);
    }
    
    // For RefIDAllocator's user ranges.
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
    
    public byte CurrentUserByte => _currentAllocation.UserByte;
    
    public ulong CurrentPosition => _currentAllocation.Position;
    
    public bool IsInLocalAllocation => _currentAllocation.UserByte == RefIDConstants.LOCAL_BYTE;
    
    #endregion
    
    #region Trash Integration
    
    // Sync: a delete confirmation that needs rollback.
    public IWorldElement TryRetrieveFromTrash(ulong tick, RefID id)
    {
        if (_trashedObjects.TryGetValue(id, out var entry) && entry.TrashTick <= tick)
        {
            _trashedObjects.Remove(id);
            return entry.Element;
        }
        return null!;
    }
    
    // Held until the delete is confirmed.
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
    
    public void DeleteFromTrash(RefID id)
    {
        _trashedObjects.Remove(id);
    }
    
    // The delete was rejected.
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
    
    // World shutdown.
    public void ClearPendingRequests()
    {
        _pendingRequests.Clear();
    }
    
    public void Reset()
    {
        _objects.Clear();
        _pendingRequests.Clear();
        _trashedObjects.Clear();
        _allocationStack.Clear();
        _localAllocationPosition = 1;
        _localAllocationDepth = 0;
        for (int i = 0; i < _ownedAllocationPosition.Length; i++)
        {
            _ownedAllocationPosition[i] = 1;
            _latestUserPosition[i] = 0;
        }
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
            LumoraLogger.Warn(
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


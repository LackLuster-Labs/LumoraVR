using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Lumora.Core;
using Lumora.Core.Networking.Sync;
using LegacyJoinGrantData = Lumora.Core.Networking.Messages.JoinGrantData;
using LegacyJoinRequestData = Lumora.Core.Networking.Messages.JoinRequestData;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Manages synchronization with 3 dedicated threads (decode, encode, sync).
/// </summary>
public class SessionSyncManager : IDisposable
{
    public enum SyncLoopStage
    {
        WaitingForSyncThreadEvent,
        RunningMessageProcessing,
        ExitedMessageProcessing,
        ProcessingStopped,
        ContinuingAfterRefreshFinished,
        GeneratingDeltaBatch,
        GeneratingCorrections,
        EncodingStreams,
        FinishingSyncCycle,
        ProcessingControlMessages,
        InitializingNewUsers,
        Finished
    }

    // Threads
    private Thread _decodeThread;
    private Thread _encodeThread;
    private Thread _syncThread;

    // Thread synchronization
    private readonly AutoResetEvent _decodeThreadEvent = new(false);
    private readonly AutoResetEvent _encodeThreadEvent = new(false);
    private readonly AutoResetEvent _syncThreadEvent = new(false);
    private readonly ManualResetEvent _syncThreadInitEvent = new(false);
    private readonly ManualResetEvent _refreshFinished = new(false);
    private readonly ManualResetEvent _lastProcessingStopped = new(false);

    // Message queues
    private readonly ConcurrentQueue<RawInMessage> _rawInMessages = new();
    private readonly ConcurrentQueue<SyncMessage> _messagesToProcess = new();
    private readonly ConcurrentQueue<SyncMessage> _messagesToTransmit = new();
    private readonly Queue<DeltaBatch> _pendingDeltaBatches = new();
    private readonly Queue<StreamMessage> _pendingStreamMessages = new();
    private const int MaxPendingDeltaBatches = 256;
    private const int MaxPendingStreamMessages = 512;

    // State
    private bool _running;
    private bool _stopProcessing;
    private bool _isDisposed;
    private bool _acceptDeltas;

    // Progress tracking for client initialization
    private int _expectedComponents = 0;
    private int _receivedComponents = 0;
    private int _initializedComponents = 0;
    private readonly HashSet<RefID> _receivedComponentIds = new();
    private readonly object _progressLock = new();
    private bool _initialFullBatchReceived = false;

    // Client-side change confirmations (sync tick -> RefIDs)
    private readonly Dictionary<ulong, HashSet<RefID>> _changesToConfirm = new();

    // Pending data records that couldn't decode yet (missing elements)
    private readonly Dictionary<RefID, PendingRecord> _pendingFullRecords = new();
    private readonly Dictionary<RefID, PendingRecord> _pendingDeltaRecords = new();
    private readonly object _pendingLock = new();
    private const int PendingRecordMaxAttempts = 20;
    private const int PendingRecordMaxAgeTicks = 400;
    // New user initialization queue
    private readonly List<User> _newUsersToInitialize = new();
    private readonly object _newUsersLock = new();

    // Local user pending initialization (wait for sync-created User)
    internal RefID LocalUserRefIDToInit { get; private set; }
    private string? _pendingLocalUserName;
    private ulong _pendingAllocationStart;
    private ulong _pendingAllocationEnd;

    // Debug
    public SyncLoopStage DEBUG_SyncLoopStage { get; private set; }
    public SyncMessage ProcessingSyncMessage { get; private set; }

    // Statistics
    public int TotalProcessedMessages { get; private set; }
    public int TotalReceivedDeltas { get; private set; }
    public int TotalReceivedFulls { get; private set; }
    public int TotalReceivedStreams { get; private set; }
    public int TotalSentDeltas { get; private set; }
    public int TotalSentFulls { get; private set; }
    public int TotalSentStreams { get; private set; }
    public int TotalCorrections { get; private set; }
    public int LastGeneratedDeltaChanges { get; private set; }

    public Session Session { get; private set; }
    public World World => Session?.World;
    public int SyncRate { get; set; } = 20;

    public SessionSyncManager(Session session)
    {
        Session = session;
    }

    /// <summary>
    /// Start all sync threads.
    /// </summary>
    public void Start()
    {
        if (_syncThread != null)
            throw new InvalidOperationException("Sync threads already started");

        _running = true;

        _decodeThread = new Thread(DecodeLoop)
        {
            Name = "SessionDecodeThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };

        _encodeThread = new Thread(EncodeLoop)
        {
            Name = "SessionEncodeThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };

        _syncThread = new Thread(SyncLoop)
        {
            Name = "SessionSyncThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };

        _decodeThread.Start();
        _encodeThread.Start();
        _syncThread.Start();

        // Wait for sync thread to initialize
        _syncThreadInitEvent.WaitOne();

        AquaLogger.Log("SessionSyncManager: All threads started");
    }

    /// <summary>
    /// Signal that world update has finished.
    /// Called from main thread after World.Update().
    /// Allows the sync thread to advance past the refresh wait.
    /// </summary>
    public void SignalRefreshFinished()
    {
        _refreshFinished.Set();
    }

    /// <summary>
    /// Stop sync processing and wait for main thread refresh.
    /// Sync thread pauses here while the main thread runs World.Update().
    /// </summary>
    public ManualResetEvent ProcessingStopped => _lastProcessingStopped;

    /// <summary>
    /// Event signaling that world refresh has completed.
    /// </summary>
    public WaitHandle RefreshFinished => _refreshFinished;

    /// <summary>
    /// Request the sync thread to stop processing and wait for world refresh.
    /// </summary>
    public void StopProcessing()
    {
        _stopProcessing = true;
        _syncThreadEvent.Set();
    }

    /// <summary>
    /// Queue raw incoming data for decoding.
    /// </summary>
    public void QueueRawIncoming(RawInMessage message)
    {
        _rawInMessages.Enqueue(message);
        _decodeThreadEvent.Set();
    }

    /// <summary>
    /// Queue message for transmission.
    /// </summary>
    public void EnqueueForTransmission(SyncMessage message)
    {
        _messagesToTransmit.Enqueue(message);
        _encodeThreadEvent.Set();
    }

    /// <summary>
    /// Queue message for processing.
    /// </summary>
    private void EnqueueForProcessing(SyncMessage message)
    {
        _messagesToProcess.Enqueue(message);
        _syncThreadEvent.Set();
    }

    /// <summary>
    /// Add user to initialization queue.
    /// </summary>
    public void QueueUserForInitialization(User user)
    {
        AquaLogger.Log($"QueueUserForInitialization: Queuing user '{user.UserName.Value}' (RefID: {user.ReferenceID})");
        lock (_newUsersLock)
        {
            _newUsersToInitialize.Add(user);
            AquaLogger.Log($"QueueUserForInitialization: Queue now has {_newUsersToInitialize.Count} users");
        }
    }

    // ===== DECODE THREAD =====

    private void DecodeLoop()
    {
        AquaLogger.Log("DecodeLoop started");

        while (_running && !_isDisposed)
        {
            _decodeThreadEvent.WaitOne();

            if (_isDisposed) break;

            while (_rawInMessages.TryDequeue(out var rawMessage))
            {
                try
                {
                    var syncMessage = SyncMessage.Decode(rawMessage);

                    // Update statistics
                    switch (syncMessage)
                    {
                        case DeltaBatch: TotalReceivedDeltas++; break;
                        case FullBatch: TotalReceivedFulls++; break;
                        case StreamMessage: TotalReceivedStreams++; break;
                    }

                    EnqueueForProcessing(syncMessage);
                }
                catch (Exception ex)
                {
                    AquaLogger.Error($"DecodeLoop: Exception decoding message: {ex.Message}");
                }
            }
        }

        AquaLogger.Log("DecodeLoop stopped");
    }

    // ===== ENCODE THREAD =====

    private void EncodeLoop()
    {
        AquaLogger.Log("EncodeLoop started");

        while (_running && !_isDisposed)
        {
            _encodeThreadEvent.WaitOne();

            if (_isDisposed) break;

            while (_messagesToTransmit.TryDequeue(out var message))
            {
                if (message.Targets.Count == 0)
                {
                    message.Dispose();
                    continue;
                }

                try
                {
                    // Update statistics
                    switch (message)
                    {
                        case DeltaBatch: TotalSentDeltas++; break;
                        case FullBatch: TotalSentFulls++; break;
                        case StreamMessage: TotalSentStreams++; break;
                    }

                    // Encode and transmit
                    var encoded = message.Encode();
                    Session.Connections.Broadcast(encoded, message.Targets, message.Reliable);
                }
                catch (Exception ex)
                {
                    AquaLogger.Error($"EncodeLoop: Exception encoding message: {ex.Message}");
                }
                finally
                {
                    message.Dispose();
                }
            }
        }

        AquaLogger.Log("EncodeLoop stopped");
    }

    // ===== SYNC THREAD =====

    private void SyncLoop()
    {
        AquaLogger.Log("SyncLoop started");

        var controlMessagesToProcess = new List<ControlMessage>();
        ulong lastDeltaSyncTime = 0;

        _syncThreadInitEvent.Set();

        while (_running && !_isDisposed)
        {
            bool lockHeld = false;
            try
            {
                DEBUG_SyncLoopStage = SyncLoopStage.WaitingForSyncThreadEvent;

                if (_messagesToProcess.IsEmpty)
                {
                    _syncThreadEvent.WaitOne(1000 / SyncRate);
                }

                if (_isDisposed) break;

                World.HookManager?.DataModelLock(Thread.CurrentThread);
                lockHeld = true;

                DEBUG_SyncLoopStage = SyncLoopStage.RunningMessageProcessing;

                while (_messagesToProcess.TryDequeue(out var message))
                {
                    ProcessingSyncMessage = message;

                    if (ProcessMessage(message, lastDeltaSyncTime, controlMessagesToProcess))
                    {
                        TotalProcessedMessages++;
                    }

                    ProcessingSyncMessage = null;
                }

                DEBUG_SyncLoopStage = SyncLoopStage.ExitedMessageProcessing;

                ProcessPendingRecords();

                if (World.IsAuthority)
                {
                    World.IncrementStateVersion();
                }

                World.HookManager?.DataModelUnlock();
                lockHeld = false;
                _lastProcessingStopped.Set();

                DEBUG_SyncLoopStage = SyncLoopStage.ProcessingStopped;

                if (!_refreshFinished.WaitOne(1000))
                {
                    if (_newUsersToInitialize.Count > 0)
                    {
                        AquaLogger.Warn("SyncLoop: Timeout waiting for world refresh, continuing anyway");
                    }
                }

                if (_isDisposed) break;

                _lastProcessingStopped.Reset();
                _refreshFinished.Reset();

                DEBUG_SyncLoopStage = SyncLoopStage.ContinuingAfterRefreshFinished;

                World.HookManager?.DataModelLock(Thread.CurrentThread);
                lockHeld = true;

                DEBUG_SyncLoopStage = SyncLoopStage.GeneratingDeltaBatch;

                var deltaBatch = World.SyncController.CollectDeltaMessages();
                LastGeneratedDeltaChanges = deltaBatch.DataRecordCount;

                if (deltaBatch.DataRecordCount > 0)
                {
                    if (World.IsAuthority)
                    {
                        var connections = Session.Connections.GetAllConnections();
                        var initializingUsers = new HashSet<User>();

                        lock (_newUsersLock)
                        {
                            foreach (var user in _newUsersToInitialize)
                            {
                                initializingUsers.Add(user);
                            }
                        }

                        foreach (var connection in connections)
                        {
                            if (Session.Connections.TryGetUser(connection, out var user))
                            {
                                if (!initializingUsers.Contains(user))
                                {
                                    deltaBatch.Targets.Add(connection);
                                }
                            }
                            else
                            {
                                deltaBatch.Targets.Add(connection);
                            }
                        }
                    }
                    else
                    {
                        var hostConnection = Session.Connections.HostConnection;
                        if (hostConnection != null)
                        {
                            deltaBatch.Targets.Add(hostConnection);
                        }
                    }

                    if (deltaBatch.Targets.Count > 0)
                    {
                        if (!World.IsAuthority)
                        {
                            var confirmTime = deltaBatch.SenderSyncTick;
                            if (!_changesToConfirm.TryGetValue(confirmTime, out var ids))
                            {
                                ids = new HashSet<RefID>();
                                _changesToConfirm[confirmTime] = ids;
                            }

                            for (int i = 0; i < deltaBatch.DataRecordCount; i++)
                            {
                                ids.Add(deltaBatch.GetDataRecord(i).TargetID);
                            }
                        }

                        EnqueueForTransmission(deltaBatch);
                    }
                    else
                    {
                        deltaBatch.Dispose();
                    }
                }
                else
                {
                    deltaBatch.Dispose();
                }

                DEBUG_SyncLoopStage = SyncLoopStage.GeneratingCorrections;

                // TODO: Handle released drives and corrections

                DEBUG_SyncLoopStage = SyncLoopStage.EncodingStreams;

                if (World.State == World.WorldState.Running)
                {
                    var streams = new List<StreamMessage>();
                    World.SyncController.GatherStreams(streams);

                    foreach (var stream in streams)
                    {
                        AddStreamTargets(stream, excludeSender: false);
                        if (stream.Targets.Count > 0)
                        {
                            EnqueueForTransmission(stream);
                        }
                        else
                        {
                            stream.Dispose();
                        }
                    }
                }

                DEBUG_SyncLoopStage = SyncLoopStage.FinishingSyncCycle;

                lastDeltaSyncTime = World.StateVersion;
                World.IncrementSyncTick();

                DEBUG_SyncLoopStage = SyncLoopStage.ProcessingControlMessages;

                foreach (var controlMessage in controlMessagesToProcess)
                {
                    ProcessControlMessage(controlMessage);
                    controlMessage.Dispose();
                }
                controlMessagesToProcess.Clear();

                DEBUG_SyncLoopStage = SyncLoopStage.InitializingNewUsers;

                if (World.IsAuthority)
                {
                    List<User> usersToInit = null;
                    lock (_newUsersLock)
                    {
                        if (_newUsersToInitialize.Count > 0)
                        {
                            usersToInit = new List<User>(_newUsersToInitialize);
                            _newUsersToInitialize.Clear();
                        }
                    }

                    if (usersToInit != null && usersToInit.Count > 0)
                    {
                        AquaLogger.Log($"Stage 8: Encoding FullBatch for {usersToInit.Count} new users");
                        var fullBatch = World.SyncController.EncodeFullBatch();
                        AquaLogger.Log($"Stage 8: FullBatch has {fullBatch.DataRecordCount} records");

                        foreach (var user in usersToInit)
                        {
                            if (Session.Connections.TryGetConnection(user, out var connection))
                            {
                                AquaLogger.Log($"Stage 8: Adding target connection for user '{user.UserName.Value}'");
                                fullBatch.Targets.Add(connection);
                            }
                            else
                            {
                                AquaLogger.Warn($"Stage 8: No connection found for user '{user.UserName.Value}'");
                            }
                        }

                        AquaLogger.Log($"Stage 8: Enqueueing FullBatch with {fullBatch.Targets.Count} targets");
                        EnqueueForTransmission(fullBatch);

                        var startDeltaMessage = new ControlMessage(ControlMessage.Message.JoinStartDelta);
                        foreach (var user in usersToInit)
                        {
                            if (Session.Connections.TryGetConnection(user, out var connection))
                            {
                                startDeltaMessage.Targets.Add(connection);
                            }
                        }
                        AquaLogger.Log($"Stage 8: Enqueueing JoinStartDelta with {startDeltaMessage.Targets.Count} targets");
                        EnqueueForTransmission(startDeltaMessage);
                    }
                }

                DEBUG_SyncLoopStage = SyncLoopStage.Finished;
            }
            catch (Exception ex)
            {
                AquaLogger.Error($"SyncLoop: Exception: {ex.Message}\n{ex.StackTrace}");
                if (World.HookManager?.Lock == HookManager.LockOwner.DataModel)
                {
                    World.HookManager?.DataModelUnlock();
                }
            }
            finally
            {
                if (lockHeld)
                {
                    World.HookManager?.DataModelUnlock();
                }
            }
        }

        AquaLogger.Log("SyncLoop stopped");
    }

    private bool ProcessMessage(SyncMessage msg, ulong lastDeltaSyncTime, List<ControlMessage> controlMessagesToProcess)
    {
        try
        {
            // Link sender user
            if (msg is not ControlMessage)
            {
                if (World.IsAuthority)
                {
                    if (!Session.Connections.TryGetUser(msg.Sender, out var user))
                    {
                        msg.Dispose();
                        return true;
                    }
                    msg.LinkSenderUser(user);
                }
                // Client links to host user
            }

            switch (msg)
            {
                case DeltaBatch deltaBatch:
                    // Only process deltas when world is running
                    if (World.State != World.WorldState.Running)
                    {
                        EnqueuePendingDelta(deltaBatch, $"world state {World.State}");
                        break;
                    }

                    if (!World.IsAuthority && !_acceptDeltas)
                    {
                        EnqueuePendingDelta(deltaBatch, "JoinStartDelta not received yet");
                        break;
                    }

                    ApplyDeltaBatch(deltaBatch);
                    break;

                case FullBatch fullBatch:
                    // Track progress for initial FullBatch (client joining)
                    if (!World.IsAuthority && World.InitState == World.InitializationState.InitializingDataModel)
                    {
                        TrackFullBatchProgress(fullBatch);
                    }

                    ApplyDataRecords(fullBatch);

                    if (!World.IsAuthority)
                    {
                        World.SetStateVersion(fullBatch.SenderStateVersion);
                        // Transition happens on JoinStartDelta to avoid starting before deltas are allowed.
                    }

                    fullBatch.Dispose();
                    break;

                case ConfirmationMessage confirmation:
                    for (int i = 0; i < confirmation.DataRecordCount; i++)
                    {
                        World.SyncController.DecodeCorrection(i, confirmation);
                    }

                    if (_changesToConfirm.TryGetValue(confirmation.ConfirmTime, out var confirmedIds))
                    {
                        World.SyncController.ApplyConfirmations(confirmedIds, confirmation.ConfirmTime);

                        foreach (var id in confirmedIds)
                        {
                            World.ReferenceController?.DeleteFromTrash(id);
                        }

                        _changesToConfirm.Remove(confirmation.ConfirmTime);
                    }
                    else
                    {
                        AquaLogger.Debug($"Confirmation received for unknown tick {confirmation.ConfirmTime}");
                    }

                    if (!World.IsAuthority)
                    {
                        World.SetStateVersion(confirmation.SenderStateVersion);
                    }

                    confirmation.Dispose();
                    break;

                case StreamMessage streamMessage:
                    if (World.IsAuthority)
                    {
                        var forwarded = CloneStreamMessage(streamMessage);
                        AddStreamTargets(forwarded, excludeSender: true);
                        if (forwarded.Targets.Count > 0)
                        {
                            EnqueueForTransmission(forwarded);
                        }
                        else
                        {
                            forwarded.Dispose();
                        }
                    }
                    if (World.State != World.WorldState.Running)
                    {
                        EnqueuePendingStream(streamMessage, $"world state {World.State}");
                        break;
                    }
                    ApplyStreamMessage(streamMessage);
                    break;

                case ControlMessage controlMessage:
                    // Queue for later processing
                    controlMessagesToProcess.Add(controlMessage);
                    break;

                default:
                    AquaLogger.Warn($"Unknown message type: {msg.GetType()}");
                    msg.Dispose();
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"ProcessMessage: Exception: {ex.Message}");
            throw;
        }
    }

    private void EnqueuePendingDelta(DeltaBatch batch, string reason)
    {
        if (_pendingDeltaBatches.Count >= MaxPendingDeltaBatches)
        {
            AquaLogger.Warn($"ProcessMessage: Dropping delta batch - pending limit reached ({MaxPendingDeltaBatches})");
            batch.Dispose();
            return;
        }

        AquaLogger.Debug($"ProcessMessage: Queueing delta batch ({reason})");
        _pendingDeltaBatches.Enqueue(batch);
    }

    private void EnqueuePendingStream(StreamMessage streamMessage, string reason)
    {
        if (_pendingStreamMessages.Count >= MaxPendingStreamMessages)
        {
            AquaLogger.Warn($"ProcessMessage: Dropping stream message - pending limit reached ({MaxPendingStreamMessages})");
            streamMessage.Dispose();
            return;
        }

        AquaLogger.Debug($"ProcessMessage: Queueing stream message ({reason})");
        _pendingStreamMessages.Enqueue(streamMessage);
    }

    private void FlushPendingMessages()
    {
        if (World.State != World.WorldState.Running)
            return;

        if (World.IsAuthority || _acceptDeltas)
        {
            while (_pendingDeltaBatches.Count > 0)
            {
                ApplyDeltaBatch(_pendingDeltaBatches.Dequeue());
            }
        }

        while (_pendingStreamMessages.Count > 0)
        {
            ApplyStreamMessage(_pendingStreamMessages.Dequeue());
        }
    }

    private void ApplyDeltaBatch(DeltaBatch deltaBatch)
    {
        // Always validate before applying deltas; host will retransmit if needed.
        ValidateDeltaBatchAndRetransmit(deltaBatch);

        ApplyDataRecords(deltaBatch);

        if (!World.IsAuthority)
        {
            World.SetStateVersion(deltaBatch.SenderStateVersion);
        }

        deltaBatch.Dispose();
    }

    private void ApplyStreamMessage(StreamMessage streamMessage)
    {
        if (World.State == World.WorldState.Running)
        {
            World.SyncController.ApplyStreams(streamMessage);
        }
        streamMessage.Dispose();
    }

    private void ValidateDeltaBatchAndRetransmit(DeltaBatch batch)
    {
        World.SyncController.ValidateDeltaMessages(batch);

        var conflicting = new List<RefID>();
        batch.GetConflictingDataRecords(conflicting);
        batch.RemoveInvalidRecords();

        if (World.IsAuthority)
        {
            if (batch.DataRecordCount > 0)
            {
                var forward = CopyDeltaBatch(batch);

                var connections = Session.Connections.GetAllConnections();
                var initializingUsers = new HashSet<User>();
                lock (_newUsersLock)
                {
                    foreach (var user in _newUsersToInitialize)
                    {
                        initializingUsers.Add(user);
                    }
                }

                foreach (var connection in connections)
                {
                    if (connection == batch.Sender)
                        continue;

                    if (Session.Connections.TryGetUser(connection, out var user) &&
                        initializingUsers.Contains(user))
                    {
                        continue;
                    }

                    forward.Targets.Add(connection);
                }

                if (forward.Targets.Count > 0)
                {
                    EnqueueForTransmission(forward);
                }
                else
                {
                    forward.Dispose();
                }
            }

            if (conflicting.Count > 0 && batch.Sender != null)
            {
                TotalCorrections += conflicting.Count;
                var confirmation = new ConfirmationMessage(batch.SenderSyncTick, World.StateVersion, World.SyncTick);
                foreach (var id in conflicting)
                {
                    World.SyncController.EncodeFull(id, confirmation);
                }
                confirmation.Targets.Add(batch.Sender);
                EnqueueForTransmission(confirmation);
            }
        }
    }

    private void AddStreamTargets(StreamMessage stream, bool excludeSender)
    {
        if (World.IsAuthority)
        {
            var connections = Session.Connections.GetAllConnections();
            HashSet<User> initializingUsers = null;

            lock (_newUsersLock)
            {
                if (_newUsersToInitialize.Count > 0)
                {
                    initializingUsers = new HashSet<User>(_newUsersToInitialize);
                }
            }

            foreach (var connection in connections)
            {
                if (excludeSender && connection == stream.Sender)
                {
                    continue;
                }

                if (initializingUsers != null &&
                    Session.Connections.TryGetUser(connection, out var user) &&
                    initializingUsers.Contains(user))
                {
                    continue;
                }

                stream.Targets.Add(connection);
            }
        }
        else
        {
            var hostConnection = Session.Connections.HostConnection;
            if (hostConnection != null)
            {
                stream.Targets.Add(hostConnection);
            }
        }
    }

    private static StreamMessage CloneStreamMessage(StreamMessage source)
    {
        var clone = new StreamMessage(source.SenderStateVersion, source.SenderSyncTick)
        {
            IsAsynchronous = source.IsAsynchronous,
            UserID = source.UserID,
            StreamStateVersion = source.StreamStateVersion,
            StreamTime = source.StreamTime,
            StreamGroup = source.StreamGroup
        };

        var sourceData = source.GetData();
        sourceData.Position = 0;
        var cloneData = clone.GetData();
        sourceData.CopyTo(cloneData);

        return clone;
    }

    private DeltaBatch CopyDeltaBatch(DeltaBatch source)
    {
        var copy = new DeltaBatch(World.StateVersion, World.SyncTick);

		for (int i = 0; i < source.DataRecordCount; i++)
		{
			var record = source.GetDataRecord(i);
			var length = record.EndOffset - record.StartOffset;
			var reader = source.SeekDataRecord(i);
			var buffer = length > 0 ? reader.ReadBytes(length) : Array.Empty<byte>();

			var writer = copy.BeginNewDataRecord(record.TargetID);
			if (buffer.Length > 0)
			{
				writer.Write(buffer);
			}
			copy.FinishDataRecord(record.TargetID);
		}

        return copy;
    }

    private int ApplyDataRecords(BinaryMessageBatch batch)
    {
        // Simple retry logic, no complex dependency resolution
        int remaining = batch.DataRecordCount;
        int passes = 0;

        AquaLogger.Log($"ApplyDataRecords: Starting with {batch.DataRecordCount} records, isFull={batch is FullBatch}");

        while (remaining > 0)
        {
            int startRemaining = remaining;
            int decodedThisPass = 0;

            for (int i = 0; i < batch.DataRecordCount; i++)
            {
                if (batch.IsProcessed(i))
                    continue;

                bool decoded = false;
                try
                {
                    decoded = batch switch
                    {
                        DeltaBatch delta => World.SyncController.DecodeDeltaMessage(i, delta),
                        FullBatch full => World.SyncController.DecodeFullMessage(i, full),
                        _ => false
                    };

                    if (decoded)
                    {
                        batch.MarkDataRecordAsProcessed(i);
                        remaining--;
                        decodedThisPass++;
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't retry immediately - might succeed in next pass
                    var dataRecord = batch.GetDataRecord(i);
                    AquaLogger.Debug($"ApplyDataRecords: Decode failed for {dataRecord.TargetID}: {ex.Message}");
                }
            }

            AquaLogger.Log($"ApplyDataRecords: Pass {passes} decoded {decodedThisPass} records, {remaining} remaining");

            // If no progress in this pass, we're done
            if (remaining == startRemaining)
            {
                break;
            }

            passes++;
        }

        // Log any remaining failures with details
        if (remaining > 0)
        {
            AquaLogger.Debug($"ApplyDataRecords: {remaining} records could not be decoded after {passes} passes");
            // Log which RefIDs failed
            for (int i = 0; i < batch.DataRecordCount; i++)
            {
                if (!batch.IsProcessed(i))
                {
                    var record = batch.GetDataRecord(i);
                    var obj = World.ReferenceController?.GetObjectOrNull(record.TargetID);
                    AquaLogger.Warn($"  FAILED RefID={record.TargetID} Type={obj?.GetType().Name ?? "NOT_FOUND"}");
                    QueuePendingRecord(batch, i);
                }
            }
        }

        return remaining;
    }

    private void QueuePendingRecord(BinaryMessageBatch batch, int recordIndex)
    {
        if (World == null)
            return;

        var record = batch.GetDataRecord(recordIndex);
        var reader = batch.SeekDataRecord(recordIndex);
        var length = record.EndOffset - record.StartOffset;
        var buffer = length > 0 ? reader.ReadBytes(length) : Array.Empty<byte>();

        var pending = new PendingRecord
        {
            TargetID = record.TargetID,
            Data = buffer,
            SenderStateVersion = batch.SenderStateVersion,
            SenderSyncTick = batch.SenderSyncTick,
            FirstSeenTick = World.SyncTick,
            Attempts = 0
        };

        var isFull = batch is FullBatch;
        var map = isFull ? _pendingFullRecords : _pendingDeltaRecords;

        lock (_pendingLock)
        {
            if (map.TryGetValue(record.TargetID, out var existing))
            {
                if (pending.SenderSyncTick >= existing.SenderSyncTick)
                {
                    map[record.TargetID] = pending;
                }
            }
            else
            {
                map[record.TargetID] = pending;
            }
        }
    }

    private void ProcessPendingRecords()
    {
        if (World == null || World.ReferenceController == null || World.SyncController == null)
            return;

        List<PendingRecord> fullRecords;
        List<PendingRecord> deltaRecords;

        lock (_pendingLock)
        {
            if (_pendingFullRecords.Count == 0 && _pendingDeltaRecords.Count == 0)
                return;

            fullRecords = new List<PendingRecord>(_pendingFullRecords.Values);
            deltaRecords = new List<PendingRecord>(_pendingDeltaRecords.Values);
        }

        foreach (var record in fullRecords)
        {
            ProcessPendingRecord(record, isFull: true);
        }

        if (World.State == World.WorldState.Running && (_acceptDeltas || World.IsAuthority))
        {
            foreach (var record in deltaRecords)
            {
                ProcessPendingRecord(record, isFull: false);
            }
        }
    }

    private void ProcessPendingRecord(PendingRecord record, bool isFull)
    {
        if (World == null || World.ReferenceController == null || World.SyncController == null)
            return;

        var isExpired = World.SyncTick - record.FirstSeenTick > PendingRecordMaxAgeTicks;
        if (isExpired)
        {
            DropPendingRecord(record.TargetID, isFull, "expired");
            return;
        }

        if (!World.ReferenceController.ContainsObject(record.TargetID))
        {
            return;
        }

        if (!isFull)
        {
            var syncElement = World.ReferenceController.GetObjectOrNull(record.TargetID) as SyncElement;
            if (syncElement != null && syncElement.IsSyncDirty)
            {
                // Defer pending delta while local changes are still dirty.
                return;
            }
        }

        var decoded = TryDecodePendingRecord(record, isFull);
        if (decoded)
        {
            DropPendingRecord(record.TargetID, isFull, null);
            return;
        }

        record.Attempts++;
        if (record.Attempts >= PendingRecordMaxAttempts)
        {
            DropPendingRecord(record.TargetID, isFull, "attempts exceeded");
            return;
        }

        UpdatePendingRecord(record, isFull);
    }

    private bool TryDecodePendingRecord(PendingRecord record, bool isFull)
    {
        BinaryMessageBatch batch = isFull
            ? new FullBatch(record.SenderStateVersion, record.SenderSyncTick)
            : new DeltaBatch(record.SenderStateVersion, record.SenderSyncTick);

        try
        {
            var writer = batch.BeginNewDataRecord(record.TargetID);
            if (record.Data.Length > 0)
            {
                writer.Write(record.Data);
            }
            batch.FinishDataRecord(record.TargetID);

            return isFull
                ? World.SyncController.DecodeFullMessage(0, (FullBatch)batch)
                : World.SyncController.DecodeDeltaMessage(0, (DeltaBatch)batch);
        }
        catch (InvalidOperationException ex)
        {
            // Common case: delta applied to a locally dirty element. Keep pending for later.
            AquaLogger.Debug($"TryDecodePendingRecord: Deferred {record.TargetID} ({(isFull ? "full" : "delta")}) - {ex.Message}");
            return false;
        }
        finally
        {
            batch.Dispose();
        }
    }

    private void UpdatePendingRecord(PendingRecord record, bool isFull)
    {
        lock (_pendingLock)
        {
            var map = isFull ? _pendingFullRecords : _pendingDeltaRecords;
            if (map.ContainsKey(record.TargetID))
            {
                map[record.TargetID] = record;
            }
        }
    }

    private void DropPendingRecord(RefID targetId, bool isFull, string reason)
    {
        lock (_pendingLock)
        {
            var map = isFull ? _pendingFullRecords : _pendingDeltaRecords;
            if (map.Remove(targetId) && !string.IsNullOrEmpty(reason))
            {
                AquaLogger.Warn($"Pending record dropped for {targetId}: {reason}");
            }
        }
    }

    private struct PendingRecord
    {
        public RefID TargetID;
        public byte[] Data;
        public ulong SenderStateVersion;
        public ulong SenderSyncTick;
        public ulong FirstSeenTick;
        public int Attempts;
    }

    /// <summary>
    /// Track progress of initial FullBatch reception for client joining.
    /// </summary>
    private void TrackFullBatchProgress(FullBatch fullBatch)
    {
        lock (_progressLock)
        {
            if (!_initialFullBatchReceived)
            {
                _initialFullBatchReceived = true;
                _expectedComponents = fullBatch.DataRecordCount;
                AquaLogger.Log($"TrackFullBatchProgress: Expecting {_expectedComponents} components from initial FullBatch");
            }

            // Count successfully received components
            for (int i = 0; i < fullBatch.DataRecordCount; i++)
            {
                var record = fullBatch.GetDataRecord(i);
                if (!_receivedComponentIds.Contains(record.TargetID))
                {
                    _receivedComponentIds.Add(record.TargetID);
                    _receivedComponents++;
                }
            }

            AquaLogger.Log($"TrackFullBatchProgress: Received {_receivedComponents}/{_expectedComponents} components ({(_receivedComponents * 100.0f / _expectedComponents):F1}%)");
        }
    }

    // NOTE: World transitions to Running when JoinStartDelta is received,
    // not based on percentage of components synchronized.

    /// <summary>
    /// Get current initialization progress (0.0 to 1.0) for loading indicators.
    /// </summary>
    public float GetInitializationProgress()
    {
        lock (_progressLock)
        {
            if (!_initialFullBatchReceived || _expectedComponents == 0)
                return 0f;

            return MathF.Min(1f, (float)_initializedComponents / _expectedComponents);
        }
    }

    /// <summary>
    /// Get current initialization status text for loading indicators.
    /// </summary>
    public string GetInitializationStatus()
    {
        if (World.IsAuthority)
            return "Authority";

        if (World.InitState == World.InitializationState.Failed)
            return "Connection failed";

        if (World.InitState == World.InitializationState.WaitingForJoinGrant)
            return "Waiting for join approval...";

        if (World.InitState == World.InitializationState.InitializingNetwork)
            return "Connecting to session...";

        lock (_progressLock)
        {
            if (!_initialFullBatchReceived)
                return "Waiting for world data...";

            if (_expectedComponents == 0)
                return "Initializing...";

            var progress = (int)((float)_initializedComponents / _expectedComponents * 100f);
            return $"Loading world components... {_initializedComponents}/{_expectedComponents} ({progress}%)";
        }
    }

    private void ProcessControlMessage(ControlMessage message)
    {
        switch (message.ControlMessageType)
        {
            case ControlMessage.Message.JoinRequest:
                if (World.IsAuthority)
                {
                    if (message.Payload == null || message.Payload.Length == 0)
                    {
                        AquaLogger.Warn("ProcessControlMessage: JoinRequest missing payload");
                        return;
                    }

                    var requestData = LegacyJoinRequestData.Decode(message.Payload);
                    AquaLogger.Log($"ProcessControlMessage: JoinRequest from '{requestData.UserName}'");
                    Session.Connections.HandleJoinRequest(message.Sender, requestData);
                }
                break;

            case ControlMessage.Message.JoinGrant:
                if (message.Payload == null || message.Payload.Length == 0)
                {
                    AquaLogger.Warn("ProcessControlMessage: JoinGrant missing payload");
                    return;
                }

                var grantData = LegacyJoinGrantData.Decode(message.Payload);
                AquaLogger.Log($"ProcessControlMessage: JoinGrant UserID={grantData.AssignedUserID}");

                var assignedRefID = new RefID(grantData.AssignedUserID);

                // Store the assigned RefID and wait for the User to sync from host.
                // When User syncs and its RefID matches, it will call SetLocalUser(this).
                LocalUserRefIDToInit = assignedRefID;
                _pendingLocalUserName = Environment.MachineName;
                _pendingAllocationStart = grantData.AllocationIDStart;
                _pendingAllocationEnd = grantData.AllocationIDEnd;

                AquaLogger.Log($"ProcessControlMessage: Stored pending local user RefID {assignedRefID} - waiting for User to sync from host");

                Session.World.SetStateVersion(grantData.StateVersion);

                // Switch allocation context to user's namespace for any local allocations
                var userByte = assignedRefID.GetUserByte();
                var startPos = grantData.AllocationIDStart > 0 ? grantData.AllocationIDStart : 1UL;
                World.ReferenceController.SetAllocationContext(userByte, startPos);
                AquaLogger.Log($"Switched allocation to user namespace: byte={userByte}, startPos={startPos}");

                Session.World.OnJoinGrantReceived();
                break;

            case ControlMessage.Message.JoinStartDelta:
                AquaLogger.Log("ProcessControlMessage: JoinStartDelta received - can now accept delta updates");
                _acceptDeltas = true;
                if (!World.IsAuthority && World.InitState == World.InitializationState.InitializingDataModel)
                {
                    AquaLogger.Log("JoinStartDelta: transitioning client world to Running");
                    World.OnFullStateReceived();
                }
                FlushPendingMessages();
                break;

            case ControlMessage.Message.RequestFullState:
                AquaLogger.Log("ProcessControlMessage: RequestFullState received from client");
                
                if (World.IsAuthority && Session.Connections.TryGetUser(message.Sender, out var requestingUser))
                {
                    AquaLogger.Log($"ProcessControlMessage: Sending full world state to user {requestingUser.UserName.Value}");
                    var fullBatch = World.SyncController.EncodeFullBatch();
                    fullBatch.Targets.Add(message.Sender);
                    EnqueueForTransmission(fullBatch);
                    
                    // Send JoinStartDelta to indicate they can now receive delta updates
                    var startDeltaMessage = new ControlMessage(ControlMessage.Message.JoinStartDelta);
                    startDeltaMessage.Targets.Add(message.Sender);
                    EnqueueForTransmission(startDeltaMessage);
                }
                else if (!World.IsAuthority)
                {
                    AquaLogger.Warn("ProcessControlMessage: Non-authority received RequestFullState - ignoring");
                }
                break;

            default:
                AquaLogger.Log($"ProcessControlMessage: {message.ControlMessageType}");
                break;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _running = false;

        // Signal all threads to wake up and exit
        _decodeThreadEvent.Set();
        _encodeThreadEvent.Set();
        _syncThreadEvent.Set();
        _refreshFinished.Set();

        // Wait for threads to finish
        _decodeThread?.Join(1000);
        _encodeThread?.Join(1000);
        _syncThread?.Join(1000);

        // Dispose events
        _decodeThreadEvent.Dispose();
        _encodeThreadEvent.Dispose();
        _syncThreadEvent.Dispose();
        _syncThreadInitEvent.Dispose();
        _refreshFinished.Dispose();
        _lastProcessingStopped.Dispose();
        _changesToConfirm.Clear();
        lock (_pendingLock)
        {
            _pendingFullRecords.Clear();
            _pendingDeltaRecords.Clear();
        }
        while (_pendingDeltaBatches.Count > 0)
        {
            _pendingDeltaBatches.Dequeue().Dispose();
        }
        while (_pendingStreamMessages.Count > 0)
        {
            _pendingStreamMessages.Dequeue().Dispose();
        }

        AquaLogger.Log("SessionSyncManager disposed");
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Lumora.Core;
using Lumora.Core.Networking.Sync;
using LegacyJoinGrantData = Lumora.Core.Networking.Messages.JoinGrantData;
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

	// State
	private bool _running;
	private bool _stopProcessing;
	private bool _isDisposed;

	// New user initialization
	private readonly List<User> _newUsersToInitialize = new();
	private readonly object _newUsersLock = new();

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
	/// </summary>
	public void SignalWorldUpdateFinished()
	{
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
		lock (_newUsersLock)
		{
			_newUsersToInitialize.Add(user);
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

		// Lock data model for sync thread
		World.HookManager?.DataModelLock(Thread.CurrentThread);

		_syncThreadInitEvent.Set();

		while (_running && !_isDisposed)
		{
			try
			{
				// Stage 1: Wait for world update or messages
				DEBUG_SyncLoopStage = SyncLoopStage.WaitingForSyncThreadEvent;

				if (_messagesToProcess.IsEmpty)
				{
					_syncThreadEvent.WaitOne(1000 / SyncRate);
				}

				if (_isDisposed) break;

				// Stage 2: Process incoming messages
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

				// Stage 3: Generate and send delta batch (authority only)
				DEBUG_SyncLoopStage = SyncLoopStage.GeneratingDeltaBatch;

				if (World.IsAuthority)
				{
					World.IncrementStateVersion();
				}

				var deltaBatch = World.SyncController.CollectDeltaMessages();
				LastGeneratedDeltaChanges = deltaBatch.DataRecordCount;

				if (deltaBatch.DataRecordCount > 0)
				{
					if (World.IsAuthority)
					{
						// Send to all connected users
						var connections = Session.Connections.GetAllConnections();
						deltaBatch.Targets.AddRange(connections);
					}
					else
					{
						// Send to host
						var hostConnection = Session.Connections.HostConnection;
						if (hostConnection != null)
						{
							deltaBatch.Targets.Add(hostConnection);
						}
					}

					EnqueueForTransmission(deltaBatch);
				}
				else
				{
					deltaBatch.Dispose();
				}

				// Stage 4: Generate corrections (authority only)
				DEBUG_SyncLoopStage = SyncLoopStage.GeneratingCorrections;

				// TODO: Handle released drives and corrections

				// Stage 5: Gather and send streams
				DEBUG_SyncLoopStage = SyncLoopStage.EncodingStreams;

				if (World.State == World.WorldState.Running)
				{
					var streams = new List<StreamMessage>();
					World.SyncController.GatherStreams(streams);

					foreach (var stream in streams)
					{
						EnqueueForTransmission(stream);
					}
				}

				// Stage 6: Finish sync cycle
				DEBUG_SyncLoopStage = SyncLoopStage.FinishingSyncCycle;

				lastDeltaSyncTime = World.StateVersion;
				World.IncrementSyncTick();

				// Stage 7: Process control messages
				DEBUG_SyncLoopStage = SyncLoopStage.ProcessingControlMessages;

				foreach (var controlMessage in controlMessagesToProcess)
				{
					ProcessControlMessage(controlMessage);
					controlMessage.Dispose();
				}
				controlMessagesToProcess.Clear();

				// Stage 8: Initialize new users
				DEBUG_SyncLoopStage = SyncLoopStage.InitializingNewUsers;

				List<User> usersToInit;
				lock (_newUsersLock)
				{
					if (_newUsersToInitialize.Count > 0)
					{
						usersToInit = new List<User>(_newUsersToInitialize);
						_newUsersToInitialize.Clear();
					}
					else
					{
						usersToInit = null;
					}
				}

				if (usersToInit != null && usersToInit.Count > 0)
				{
					var fullBatch = World.SyncController.EncodeFullBatch();

					foreach (var user in usersToInit)
					{
						if (Session.Connections.TryGetConnection(user, out var connection))
						{
							fullBatch.Targets.Add(connection);
						}
					}

					EnqueueForTransmission(fullBatch);

					// Send JoinStartDelta to new users
					var startDeltaMessage = new ControlMessage(ControlMessage.Message.JoinStartDelta);
					foreach (var user in usersToInit)
					{
						if (Session.Connections.TryGetConnection(user, out var connection))
						{
							startDeltaMessage.Targets.Add(connection);
						}
					}
					EnqueueForTransmission(startDeltaMessage);
				}

				DEBUG_SyncLoopStage = SyncLoopStage.Finished;
				if (LastGeneratedDeltaChanges > 0)
				{
					AquaLogger.Debug($"SyncLoop: DeltaChanges={LastGeneratedDeltaChanges}");
				}
			}
			catch (Exception ex)
			{
				AquaLogger.Error($"SyncLoop: Exception: {ex.Message}\n{ex.StackTrace}");
			}
		}

		World.HookManager?.DataModelUnlock();
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
					if (World.State != World.WorldState.Running)
						throw new InvalidOperationException("Cannot process delta when not running");

					// Validate and retransmit (authority)
					if (World.IsAuthority)
					{
						World.SyncController.ValidateDeltaMessages(deltaBatch);
					}

					// Apply data records
					ApplyDataRecords(deltaBatch);

					if (!World.IsAuthority)
					{
						World.SetStateVersion(deltaBatch.SenderStateVersion);
					}

					deltaBatch.Dispose();
					break;

				case FullBatch fullBatch:
					ApplyDataRecords(fullBatch);

					if (!World.IsAuthority)
					{
						World.SetStateVersion(fullBatch.SenderStateVersion);
					}

					fullBatch.Dispose();
					break;

				case ConfirmationMessage confirmation:
					for (int i = 0; i < confirmation.DataRecordCount; i++)
					{
						World.SyncController.DecodeCorrection(i, confirmation);
					}

					if (!World.IsAuthority)
					{
						World.SetStateVersion(confirmation.SenderStateVersion);
					}

					confirmation.Dispose();
					break;

				case StreamMessage streamMessage:
					if (World.State == World.WorldState.Running)
					{
						World.SyncController.ApplyStreams(streamMessage);
					}
					msg.Dispose();
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

	private int ApplyDataRecords(BinaryMessageBatch batch)
	{
		int remaining = batch.DataRecordCount;
		int lastRemaining = remaining;

		while (remaining > 0)
		{
			for (int i = 0; i < batch.DataRecordCount; i++)
			{
				if (batch.IsProcessed(i))
					continue;

				bool decoded = batch switch
				{
					DeltaBatch delta => World.SyncController.DecodeDeltaMessage(i, delta),
					FullBatch full => World.SyncController.DecodeFullMessage(i, full),
					_ => false
				};

				if (decoded)
				{
					batch.MarkDataRecordAsProcessed(i);
					remaining--;
				}
			}

			if (remaining == lastRemaining)
			{
				// No progress, some records couldn't be decoded
				break;
			}

			lastRemaining = remaining;
		}

		return remaining;
	}

	private void ProcessControlMessage(ControlMessage message)
	{
		switch (message.ControlMessageType)
		{
			case ControlMessage.Message.JoinGrant:
				if (message.Payload == null || message.Payload.Length == 0)
				{
					AquaLogger.Warn("ProcessControlMessage: JoinGrant missing payload");
					return;
				}

				var grantData = LegacyJoinGrantData.Decode(message.Payload);
				AquaLogger.Log($"ProcessControlMessage: JoinGrant UserID={grantData.AssignedUserID}");

				var assignedRefID = new RefID(grantData.AssignedUserID);
				var localUser = new User(World, assignedRefID);
				localUser.UserID.Value = grantData.AssignedUserID.ToString();
				localUser.AllocationIDStart.Value = grantData.AllocationIDStart;
				localUser.AllocationIDEnd.Value = grantData.AllocationIDEnd;

				Session.World.SetLocalUser(localUser);
				Session.World.SetStateVersion(grantData.StateVersion);
				Session.World.OnJoinGrantReceived();
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

		AquaLogger.Log("SessionSyncManager disposed");
	}
}

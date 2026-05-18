// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumora.Core.Assets;
using Lumora.Core.Networking;
using Lumora.Core.Networking.Sync;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Handles peer-to-peer asset transfer over the control message channel.
///
/// Protocol:
///   Client  →  AssetRequest(uri)         →  Host/Owner
///   Host    →  AssetTransmissionStart(id, uri, totalBytes)  →  Client
///   Client  →  AssetNextChunkRequest(id)  →  Host   (first pull fetches 16 chunks)
///   Host    →  AssetChunk(id, offset, data) ×N  →  Client
///   …repeat until done…
///   Host    →  AssetNotAvailable(uri)     →  Client  (if it can't serve the file)
/// </summary>
public class SessionAssetTransferer : IDisposable
{
    // ── Job identity ──────────────────────────────────────────────────────────

    private readonly struct JobID : IEquatable<JobID>
    {
        public readonly IConnection Connection;
        public readonly int ID;

        public JobID(IConnection connection, int id) { Connection = connection; ID = id; }
        public bool Equals(JobID other) => Connection == other.Connection && ID == other.ID;
        public override bool Equals(object obj) => obj is JobID j && Equals(j);
        public override int GetHashCode() => HashCode.Combine(Connection, ID);
    }

    // ── Outbound (we are sending) ─────────────────────────────────────────────

    private sealed class FileTransmitJob
    {
        public IConnection Target { get; }
        public int ID { get; }
        public Uri AssetUri { get; }

        private readonly byte[] _data;
        private int _offset;
        private bool _firstChunk = true;

        public bool IsDone => _offset >= _data.Length;
        public bool FirstChunk => _firstChunk;

        private const int ChunkSize = 32 * 1024; // 32 KB

        public FileTransmitJob(string filePath, Uri assetUri, IConnection target, int id)
        {
            Target = target;
            ID = id;
            AssetUri = assetUri;
            _data = File.ReadAllBytes(filePath);
        }

        /// <summary>Build the AssetTransmissionStart control message.</summary>
        public ControlMessage Initialize()
        {
            var msg = new ControlMessage(ControlMessage.Message.AssetTransmissionStart);
            msg.Targets.Add(Target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            w.Write(AssetUri.ToString());
            w.Write(_data.Length);
            msg.Payload = ms.ToArray();
            return msg;
        }

        /// <summary>Build one AssetChunk control message.</summary>
        public ControlMessage GetChunk()
        {
            int size = System.Math.Min(ChunkSize, _data.Length - _offset);
            var msg = new ControlMessage(ControlMessage.Message.AssetChunk);
            msg.Targets.Add(Target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            w.Write(_offset);
            w.Write(size);
            w.Write(_data, _offset, size);
            msg.Payload = ms.ToArray();
            _offset += size;
            _firstChunk = false;
            return msg;
        }
    }

    // ── Inbound (we are receiving) ────────────────────────────────────────────

    private sealed class FileReceiveJob
    {
        public int ID { get; }
        public Uri AssetUri { get; }
        public IConnection Source { get; }

        private readonly int _totalSize;
        private readonly byte[] _buffer;
        private int _received;
        private readonly string _tempPath;

        public bool IsDone => _received >= _totalSize;

        public FileReceiveJob(IConnection source, BinaryReader reader, string tempPath)
        {
            Source = source;
            ID = reader.ReadInt32();
            // Bound the URI string before constructing Uri (which itself has work to do).
            AssetUri = new Uri(reader.ReadBoundedString(NetworkLimits.MaxAssetUriBytes));
            _totalSize = reader.ReadInt32();

            // A peer cannot make us pre-allocate a multi-GB buffer just by declaring
            // a fake transfer size. Reject negative or out-of-bounds totals before alloc.
            if (_totalSize < 0 || _totalSize > NetworkLimits.MaxAssetTransferTotalBytes)
                throw new InvalidDataException($"Asset transfer total size {_totalSize} out of bounds (cap {NetworkLimits.MaxAssetTransferTotalBytes}).");

            _buffer = new byte[_totalSize];
            _tempPath = tempPath;
        }

        public void ApplyChunk(BinaryReader reader)
        {
            int offset = reader.ReadInt32();
            int size = reader.ReadInt32();

            // Bound the chunk size, reject negative offsets, and ensure the chunk lands
            // inside the pre-declared buffer — the latter prevents an OOB write via
            // a peer crafting offset+size > _totalSize after a legitimate Initialize.
            if (size < 0 || size > NetworkLimits.MaxAssetChunkBytes)
                throw new InvalidDataException($"Asset chunk size {size} out of bounds (cap {NetworkLimits.MaxAssetChunkBytes}).");
            if (offset < 0 || offset > _totalSize - size)
                throw new InvalidDataException($"Asset chunk offset {offset} + size {size} exceeds buffer length {_totalSize}.");

            var data = reader.ReadBoundedBytes(size, NetworkLimits.MaxAssetChunkBytes);
            Buffer.BlockCopy(data, 0, _buffer, offset, size);
            _received += size;
        }

        public ControlMessage NextChunkRequest(IConnection target)
        {
            var msg = new ControlMessage(ControlMessage.Message.AssetNextChunkRequest);
            msg.Targets.Add(target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            msg.Payload = ms.ToArray();
            return msg;
        }

        /// <summary>Write buffered data to disk and return the temp file path.</summary>
        public string FinalizeAndGetFile()
        {
            File.WriteAllBytes(_tempPath, _buffer);
            return _tempPath;
        }
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private const int MaxTransmitJobs = 4;

    private readonly object _lock = new();
    private readonly Dictionary<JobID, FileTransmitJob> _transmitJobs = new();
    private readonly Dictionary<JobID, FileReceiveJob> _receiveJobs = new();
    private readonly Dictionary<string, Action<Uri, string>> _assetRequests = new();
    private readonly Queue<FileTransmitJob> _transmitQueue = new();
    private int _outboundIdPool;

    public Session Session { get; }

    public SessionAssetTransferer(Session session)
    {
        Session = session;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Request an asset by URI. Sends AssetRequest to the host (clients) or to the
    /// asset owner's connection (if we are the authority).
    /// <paramref name="onGathered"/> receives (uri, localFilePath) — path is null on failure.
    /// </summary>
    public void RequestAsset(Uri assetUri, Action<Uri, string> onGathered)
    {
        lock (_lock)
        {
            var uriStr = assetUri.ToString();
            if (_assetRequests.ContainsKey(uriStr))
                return; // already in-flight

            IConnection target;
            if (Session.World.IsAuthority)
            {
                // Authority → request from the machine that owns this local:// asset
                var machineId = assetUri.Host;
                var owner = Session.World.GetAllUsers()
                    ?.FirstOrDefault(u => u.MachineID?.Value == machineId);
                if (owner == null || !Session.Connections.TryGetConnection(owner, out target))
                {
                    LumoraLogger.Warn($"AssetTransferer: no peer for machine '{machineId}', cannot fetch {uriStr}");
                    onGathered(assetUri, null);
                    return;
                }
            }
            else
            {
                target = Session.Connections.HostConnection;
            }

            if (target == null)
            {
                onGathered(assetUri, null);
                return;
            }

            _assetRequests[uriStr] = onGathered;

            var msg = new ControlMessage(ControlMessage.Message.AssetRequest);
            msg.Targets.Add(target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(uriStr);
            msg.Payload = ms.ToArray();
            Session.Sync.EnqueueForTransmission(msg);
            LumoraLogger.Log($"AssetTransferer: requested {uriStr}");
        }
    }

    /// <summary>Process an incoming asset control message.</summary>
    public void ProcessMessage(ControlMessage message)
    {
        lock (_lock)
        {
            // Per-message try/catch so a malformed payload from one peer doesn't take
            // down the asset transfer system for everyone. Bounds-check failures from
            // the decoders surface here as InvalidDataException.
            try
            {
                switch (message.ControlMessageType)
                {
                    case ControlMessage.Message.AssetRequest: HandleAssetRequest(message); break;
                    case ControlMessage.Message.AssetTransmissionStart: HandleTransmissionStart(message); break;
                    case ControlMessage.Message.AssetChunk: HandleChunk(message); break;
                    case ControlMessage.Message.AssetNextChunkRequest: HandleNextChunkRequest(message); break;
                    case ControlMessage.Message.AssetNotAvailable: HandleNotAvailable(message); break;
                }
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"AssetTransferer: dropped malformed {message.ControlMessageType} from {message.Sender?.Identifier}: {ex.Message}");
            }
            RefreshJobs();
        }
    }

    /// <summary>Clean up all jobs associated with a disconnected peer.</summary>
    public void ConnectionClosed(IConnection connection)
    {
        lock (_lock)
        {
            // Cancel outbound jobs
            var dead = _transmitJobs.Keys.Where(k => k.Connection == connection).ToList();
            foreach (var key in dead) _transmitJobs.Remove(key);

            // Cancel inbound jobs and fail their callbacks
            dead = _receiveJobs.Keys.Where(k => k.Connection == connection).ToList();
            foreach (var key in dead)
            {
                var job = _receiveJobs[key];
                _receiveJobs.Remove(key);
                var uriStr = job.AssetUri.ToString();
                if (_assetRequests.TryGetValue(uriStr, out var cb))
                {
                    _assetRequests.Remove(uriStr);
                    cb(job.AssetUri, null);
                }
            }

            RefreshJobs();
        }
    }

    // ── Message handlers ──────────────────────────────────────────────────────

    private void HandleAssetRequest(ControlMessage message)
    {
        string uriStr;
        using (var ms = new MemoryStream(message.Payload))
        using (var r = new BinaryReader(ms))
            uriStr = r.ReadBoundedString(NetworkLimits.MaxAssetUriBytes);

        var assetUri = new Uri(uriStr);

        // Try to resolve locally via LocalDB
        var localDB = Engine.Current?.LocalDB;
        string localPath = null;
        if (assetUri.Scheme == "local" && localDB != null)
            localPath = localDB.GetFilePath(uriStr);

        if (localPath != null && File.Exists(localPath))
        {
            var id = _outboundIdPool++;
            _transmitQueue.Enqueue(new FileTransmitJob(localPath, assetUri, message.Sender, id));
            LumoraLogger.Log($"AssetTransferer: queued outbound transfer for {uriStr} (job {id})");
        }
        else
        {
            LumoraLogger.Warn($"AssetTransferer: cannot serve {uriStr} — not found locally");
            var msg = new ControlMessage(ControlMessage.Message.AssetNotAvailable);
            msg.Targets.Add(message.Sender);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(uriStr);
            msg.Payload = ms.ToArray();
            Session.Sync.EnqueueForTransmission(msg);
        }
    }

    private void HandleTransmissionStart(ControlMessage message)
    {
        // Get a unique temp file path from LocalDB, fall back to system temp
        var localDB = Engine.Current?.LocalDB;
        string tempPath = localDB?.GetTempFilePath(".asset") ?? Path.GetTempFileName();

        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);
        var job = new FileReceiveJob(message.Sender, r, tempPath);
        _receiveJobs[new JobID(message.Sender, job.ID)] = job;

        Session.Sync.EnqueueForTransmission(job.NextChunkRequest(message.Sender));
        LumoraLogger.Log($"AssetTransferer: receiving {job.AssetUri} ({job.ID})");
    }

    private void HandleChunk(ControlMessage message)
    {
        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);

        int id = r.ReadInt32();
        var key = new JobID(message.Sender, id);
        if (!_receiveJobs.TryGetValue(key, out var job))
            return;

        job.ApplyChunk(r);

        if (job.IsDone)
        {
            _receiveJobs.Remove(key);
            string path = job.FinalizeAndGetFile();
            LumoraLogger.Log($"AssetTransferer: finished receiving {job.AssetUri} -> {path}");

            var uriStr = job.AssetUri.ToString();
            if (_assetRequests.TryGetValue(uriStr, out var cb))
            {
                _assetRequests.Remove(uriStr);
                cb(job.AssetUri, path);
            }
        }
        else
        {
            Session.Sync.EnqueueForTransmission(job.NextChunkRequest(message.Sender));
        }
    }

    private void HandleNextChunkRequest(ControlMessage message)
    {
        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);
        int id = r.ReadInt32();

        var key = new JobID(message.Sender, id);
        if (!_transmitJobs.TryGetValue(key, out var job))
            return;

        // First pull: send 16 chunks; subsequent pulls: send 1
        int count = job.FirstChunk ? 16 : 1;
        for (int i = 0; i < count; i++)
        {
            Session.Sync.EnqueueForTransmission(job.GetChunk());
            if (job.IsDone)
            {
                _transmitJobs.Remove(key);
                LumoraLogger.Log($"AssetTransferer: finished sending job {id}");
                break;
            }
        }
    }

    private void HandleNotAvailable(ControlMessage message)
    {
        string uriStr;
        using (var ms = new MemoryStream(message.Payload))
        using (var r = new BinaryReader(ms))
            uriStr = r.ReadBoundedString(NetworkLimits.MaxAssetUriBytes);

        LumoraLogger.Warn($"AssetTransferer: peer reported AssetNotAvailable for {uriStr}");
        if (_assetRequests.TryGetValue(uriStr, out var cb))
        {
            _assetRequests.Remove(uriStr);
            cb(new Uri(uriStr), null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshJobs()
    {
        while (_transmitJobs.Count < MaxTransmitJobs && _transmitQueue.Count > 0)
        {
            var job = _transmitQueue.Dequeue();
            var key = new JobID(job.Target, job.ID);
            _transmitJobs[key] = job;
            Session.Sync.EnqueueForTransmission(job.Initialize());
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _transmitJobs.Clear();
            _receiveJobs.Clear();
            _assetRequests.Clear();
        }
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Networking;
using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

// Handles peer-to-peer asset transfer over the control message channel.
//
// Protocol:
//   Client -> AssetRequest(uri) -> Host/Owner
//   Host -> AssetTransmissionStart(id, uri, totalBytes, format, totalBytes64) -> Client
//   Client -> AssetNextChunkRequest(id, -1) -> Host   (ready for chunks)
//   Host -> AssetChunk(id, offset, data, offset64) as many as the link's window holds -> Client
//   Client -> AssetNextChunkRequest(id, offset64) for every chunk that lands -> Host
//   ...until done...
//   Host -> AssetNotAvailable(uri) -> Client  (if it can't serve the file)
//
// Host relay: a client always asks the host for an asset, but the host might not own it - another
// peer may have imported it (its URI carries that peer's machine id). When the host can't serve a
// local:// asset from its own store, instead of giving up it forwards the request to the owning peer,
// receives the bytes, and streams them on to the original requester. Several requesters waiting on the
// same asset are coalesced onto one fetch. This is what lets "B imports content, C joins" work when
// the host is neither B nor C.
//
// Nothing here waits on an answer forever. A receiver that goes quiet holds only its own slots, and
// the sender retries until it answers again; a requester that hears nothing asks again,
// and the sender folds a repeated ask into the job it already has. The first three-player session
// over Steam sat at four uploads of 0 B for good: one guest stopped answering, the pool of four
// slots was shared by everyone, and the least-recently-served pick kept handing that guest every
// slot that came free until it held all four and nobody else got a byte. -xlinka
//
// Files go from disk to the wire and from the wire to disk a chunk at a time, so neither end holds a
// whole file, and sizes and offsets are 64-bit. Both travel twice: the 32-bit fields a build without
// them reads, then the 64-bit ones after everything such a build looks at, so either build reads the
// other's messages. A file over 2 GB is only ever sent to a build that reads the wide fields, because
// an older one refuses anything over its own cap well before that. -xlinka
//
// A receiver on this build names the chunk each ack answers and takes chunks in any order, so the sender
// spreads its chunks over the transport's bulk lanes. A build before that asks once per chunk with no
// offset, one ask fewer than it gets chunks, and needs them in order: it gets them in order on the
// background channel. -xlinka
//
// Each active file gets a share of the peer's window, capped by its own fixed limit. This used to steer its own
// window off round trips and receiver hold times, stacked on top of the transport, which already paces
// every link and keeps bulk under voice, poses and world changes. The two read each other's queues as
// congestion: a receiver busy loading or a link carrying an avatar shrank the window to two chunks and a
// big file crawled or sat at 0 % until the stall timer started it over. Now the transport alone decides
// what crosses the link, and this only keeps enough out to fill it. -xlinka
//
// None of it runs on a world thread or under the data model lock. Messages are handled on this
// transferer's own thread, files are read and written on the disk threads, and answers to whoever
// asked go out through the thread pool. -xlinka
public class SessionAssetTransferer : IDisposable
{

    private readonly struct JobID : IEquatable<JobID>
    {
        public readonly IConnection Connection;
        public readonly int ID;

        public JobID(IConnection connection, int id) { Connection = connection; ID = id; }
        public bool Equals(JobID other) => Connection == other.Connection && ID == other.ID;
        public override bool Equals(object? obj) => obj is JobID j && Equals(j);
        public override int GetHashCode() => HashCode.Combine(Connection, ID);
    }

    // A chunk goes onto the wire as one burst, and live data sent behind it waits for all of it at a thin
    // link's bottleneck: 64 KB is 21 ms at 3 MB/s, 32 KB half that. -xlinka
    private const int ChunkSize = 32 * 1024;
    private const int MaxReceiveRuns = 4096;
    // Out and not acked yet, per file and per peer. 64 chunks is 2 MB, 20 MB/s on a 100 ms path for one file;
    // a peer's whole window covers a fast long link with several files going. -xlinka
    private const long JobWindow = 64L * ChunkSize;
    private const long PeerWindow = 16L * 1024 * 1024;
    // Read ahead of the window, one disk read at a time per file.
    private const int ReadAheadChunks = 16;
    // Chunks one disk write takes from a file's backlog.
    private const int WriteBatchChunks = 32;

    // Outbound (we are sending)

    private sealed class FileTransmitJob : IDisposable
    {
        public IConnection Target { get; }
        public int ID { get; }
        public Uri AssetUri { get; }

        private readonly string _filePath;
        private readonly string _format;
        private Stream? _source;
        private long _offset;
        private byte[]? _scratch;

        public bool AllSent => _source != null && _offset >= TotalBytes;

        public long TotalBytes { get; private set; }
        public long SentBytes => _offset;

        public bool Loading;
        public bool Reading;
        public bool Busy => Loading || Reading;
        public int Generation;
        // Chunks read and waiting for room in the window, in file order from SentBytes on.
        public readonly Queue<ControlMessage> Prepared = new();
        public long ReadTo;

        public void ResumeAt(long offset)
        {
            if (offset < 0 || offset > TotalBytes)
                throw new InvalidDataException("Asset resume offset exceeds its length.");
            _offset = offset;
            ReadTo = offset;
            ClearPrepared();
        }

        public void Advance(int size) => _offset += size;

        public void ClearPrepared()
        {
            while (Prepared.TryDequeue(out var stale))
                stale.Dispose();
        }

        // When the receiver last asked for a chunk, or the start last went out, and how many starts
        // have gone. Read by the stall sweep.
        public double LastHeard;
        public int Starts;
        public double StartSentAt;

        // When the oldest chunk still waiting for its ack went out, or 0 with none out.
        public double OldestOut()
        {
            double oldest = 0;
            foreach (var (at, _) in _byOffset.Values)
                if (oldest == 0 || at < oldest)
                    oldest = at;
            if (_inOrder.TryPeek(out var first) && (oldest == 0 || first.At < oldest))
                oldest = first.At;
            return oldest;
        }

        // The receiver asked for this start's first chunk, and whether it acks each chunk by offset and
        // takes them in any order.
        public bool Ready;
        public bool Lanes;

        // Chunks out and not acked yet, with when each went: by offset for a receiver that names them, in
        // order for one that does not. The last chunk to an older receiver is not here, as it never acks it.
        private readonly Dictionary<long, (double At, int Size)> _byOffset = new();
        private readonly Queue<(double At, int Size)> _inOrder = new();
        public long OutBytes { get; private set; }
        public bool Drained => _byOffset.Count == 0 && _inOrder.Count == 0;

        public void Track(long offset, double at, int size)
        {
            if (Lanes)
                _byOffset[offset] = (at, size);
            else
                _inOrder.Enqueue((at, size));
            OutBytes += size;
        }

        // The chunk an ack answers; a size of zero when it answers nothing still out.
        public (double At, int Size) Acked(long? offset)
        {
            (double At, int Size) hit;
            if (Lanes)
            {
                if (offset is not long at || !_byOffset.Remove(at, out hit))
                    return default;
            }
            else if (!_inOrder.TryDequeue(out hit))
            {
                return default;
            }
            OutBytes -= hit.Size;
            return hit;
        }

        public long ClearFlight()
        {
            long had = OutBytes;
            _byOffset.Clear();
            _inOrder.Clear();
            OutBytes = 0;
            return had;
        }

        public FileTransmitJob(string filePath, Uri assetUri, IConnection target, int id)
        {
            Target = target;
            ID = id;
            AssetUri = assetUri;
            _filePath = filePath;
            // A local:// URI is a content hash and carries no format. The only machine that still knows
            // what these bytes are is the one holding the file, and it knows because its record kept the
            // extension, so send it. Without it the receiver has a nameless blob and every decoder that
            // dispatches on extension - the mesh decoder above all - has nothing to dispatch on. -xlinka
            var db = Engine.Current?.LocalDB;
            var filed = db?.GetExtension(assetUri.OriginalString);
            _format = !string.IsNullOrEmpty(filed) ? filed! : Path.GetExtension(filePath) ?? string.Empty;
            long plain = db?.GetPlainSize(assetUri.OriginalString) ?? -1;
            if (plain >= 0)
                TotalBytes = plain;
            else
            {
                try { TotalBytes = new FileInfo(filePath).Length; }
                catch { TotalBytes = 0; }
            }
        }

        // A queued job holds the path, not the bytes, and a started one reads its file as it goes. Reading
        // whole files at queue time put every file an avatar has into memory at once, read on the sync
        // thread under the data model lock in a single cycle, for jobs that would wait minutes for a slot.
        // A file the store keeps sealed is read through a decrypting stream, a chunk at a time, so the peer
        // gets exactly the plaintext it always got and the hash in the address still holds. -xlinka
        public bool Load(LocalDB? db)
        {
            if (_source != null)
                return true;
            try
            {
                var uriStr = AssetUri.OriginalString;
                var stored = db?.OpenRead(uriStr);
                if (stored != null)
                {
                    _source = stored;
                }
                else if (db != null && db.OwnsPath(_filePath))
                {
                    return false;
                }
                else
                {
                    _source = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.SequentialScan);
                }
                TotalBytes = _source.Length;
                return true;
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"AssetTransferer: cannot read '{_filePath}' for {AssetUri.OriginalString}: {ex.Message}");
                Dispose();
                return false;
            }
        }

        // A new start defaults to offset zero; a receiver with retained ranges supplies its resume offset.
        public ControlMessage Initialize()
        {
            Generation++;
            ClearPrepared();
            _offset = 0;
            ReadTo = 0;
            Ready = false;
            var msg = new ControlMessage(ControlMessage.Message.AssetTransmissionStart);
            msg.Targets.Add(Target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            w.Write(AssetUri.OriginalString);
            w.Write((int)System.Math.Min(TotalBytes, int.MaxValue));
            // Appended after the fields a build without it wrote, so its reader stops cleanly at the
            // length and simply gets no format.
            w.Write(_format);
            w.Write(TotalBytes);
            msg.Payload = ms.ToArray();
            return msg;
        }

        // Up to count chunks from offset on, in one pass over the file. Runs on a disk thread, never two at once
        // for one job.
        public List<ControlMessage> ReadChunks(long offset, int count)
        {
            var chunks = new List<ControlMessage>(count);
            for (int i = 0; i < count && offset < TotalBytes; i++)
            {
                chunks.Add(GetChunk(offset));
                offset += System.Math.Min(ChunkSize, TotalBytes - offset);
            }
            return chunks;
        }

        public ControlMessage GetChunk(long offset)
        {
            int size = (int)System.Math.Min(ChunkSize, TotalBytes - offset);
            _scratch ??= new byte[ChunkSize];
            int read = 0;
            if (size > 0)
            {
                _source!.Position = offset;
                while (read < size)
                {
                    int n = _source.Read(_scratch, read, size - read);
                    if (n <= 0)
                        throw new EndOfStreamException($"{_filePath} ended at {offset + read} of {TotalBytes} bytes");
                    read += n;
                }
            }

            var msg = new ControlMessage(ControlMessage.Message.AssetChunk);
            msg.Targets.Add(Target);
            using var ms = new MemoryStream(size + 32);
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            w.Write((int)System.Math.Min(offset, int.MaxValue));
            w.Write(size);
            w.Write(_scratch, 0, size);
            w.Write(offset);
            msg.Payload = ms.ToArray();
            return msg;
        }

        public void Dispose()
        {
            ClearPrepared();
            _source?.Dispose();
            _source = null;
        }
    }

    // Inbound (we are receiving)

    private sealed class FileReceiveJob : IDisposable
    {
        public int ID { get; }
        public Uri AssetUri { get; }
        public IConnection Source { get; }

        private readonly long _totalSize;
        private long _received;
        // What has landed, as sorted runs of [start, end). Chunks come in any order: a sender on this build
        // spreads them over lanes, and a start sent again can have chunks of the one before still on the
        // way. The address is the content, so the bytes at an offset are the same whichever pass sent them.
        private readonly List<(long Start, long End)> _runs = new();
        private readonly string _tempPath;
        private FileStream? _sink;
        // With a store to adopt into, the bytes land sealed from the first chunk, under a key that goes into
        // the store's record with the file. Only a run with no store keeps a plain temp file.
        private Lumora.Nexus.Storage.SealedScatterWriter? _sealed;
        private bool _finished;

        public bool IsDone => _received >= _totalSize;

        public long TotalBytes => _totalSize;
        public long ReceivedBytes => _received;
        public long ResumeOffset => _runs.Count > 0 && _runs[0].Start == 0 ? _runs[0].End : 0;

        public double LastProgress;
        public readonly Queue<(byte[] Payload, long Arrived)> PendingChunks = new();
        public long PendingBytes;
        public bool Writing;
        public bool RestartPending;

        // tempPathFor is handed the sender's declared format so the file we land on disk keeps the
        // extension the asset actually has. The temp name is what the local database then adopts, and
        // the extension on that cache file is the only thing an extension-dispatching decoder has to go
        // on later, so losing it here quietly breaks every load of that asset from then on. -xlinka
        public byte[]? SealKey { get; }

        public FileReceiveJob(IConnection source, BinaryReader reader, Func<string, string> tempPathFor, bool seal)
        {
            Source = source;
            ID = reader.ReadInt32();
            // Bound the URI string before constructing Uri (which itself has work to do).
            AssetUri = new Uri(reader.ReadBoundedString(NetworkLimits.MaxAssetUriBytes));
            long total = reader.ReadInt32();

            // Absent on a build that predates the format field, which leaves the stream exactly here.
            string? declaredFormat = null;
            if (HasMore(reader))
                declaredFormat = reader.ReadBoundedString(NetworkLimits.MaxAssetFormatBytes);
            if (reader.BaseStream.CanSeek && reader.BaseStream.Length - reader.BaseStream.Position >= sizeof(long))
                total = reader.ReadInt64();
            Format = SanitizeFormat(declaredFormat);

            // The size is the peer's word, and the file it names is made on our disk as the bytes arrive,
            // so it is held to a ceiling before anything is opened.
            if (total < 0 || total > NetworkLimits.MaxAssetTransferTotalBytes)
                throw new InvalidDataException($"Asset transfer total size {total} out of bounds (cap {NetworkLimits.MaxAssetTransferTotalBytes}).");
            _totalSize = total;

            _tempPath = tempPathFor(Format);
            if (seal)
            {
                SealKey = Lumora.Nexus.Storage.SealedKeys.NewKey();
                _sealed = new Lumora.Nexus.Storage.SealedScatterWriter(_tempPath, total, SealKey);
            }
            else
            {
                _sink = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize);
            }
        }

        public string Format { get; }

        private static bool HasMore(BinaryReader reader)
        {
            var stream = reader.BaseStream;
            return stream.CanSeek && stream.Position < stream.Length;
        }

        // Writes the chunk where it goes and returns its offset, for the ack. One wholly here already is a
        // repeat and passed over.
        public long ApplyChunk(BinaryReader reader)
        {
            long offset = reader.ReadInt32();
            int size = reader.ReadInt32();

            // Bound the chunk size, reject negative offsets, and ensure the chunk lands
            // inside the pre-declared length.
            if (size < 0 || size > NetworkLimits.MaxAssetChunkBytes)
                throw new InvalidDataException($"Asset chunk size {size} out of bounds (cap {NetworkLimits.MaxAssetChunkBytes}).");

            var data = reader.ReadBoundedBytes(size, NetworkLimits.MaxAssetChunkBytes);
            if (HasMore(reader))
                offset = reader.ReadInt64();
            if (offset < 0 || offset > _totalSize - size)
                throw new InvalidDataException($"Asset chunk offset {offset} + size {size} exceeds length {_totalSize}.");

            if (size == 0 || Covers(offset, offset + size))
                return offset;
            // A sender scatters no further than its window, a few hundred runs at most. A peer laying down
            // slivers to grow the list without end is not sending a file.
            if (_runs.Count >= MaxReceiveRuns)
                throw new InvalidDataException($"Asset chunks scattered over more than {MaxReceiveRuns} runs.");
            if (_sealed != null)
            {
                _sealed.Write(offset, data.AsSpan(0, size));
            }
            else
            {
                _sink!.Position = offset;
                _sink.Write(data, 0, size);
            }
            Cover(offset, offset + size);
            return offset;
        }

        private bool Covers(long start, long end)
        {
            int i = RunAtOrBefore(start);
            return i >= 0 && _runs[i].End >= end;
        }

        // Index of the last run starting at or before `at`, or -1.
        private int RunAtOrBefore(long at)
        {
            int lo = 0, hi = _runs.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_runs[mid].Start <= at) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        private void Cover(long start, long end)
        {
            int i = RunAtOrBefore(start);
            if (i < 0 || _runs[i].End < start)
                i++;
            long had = 0;
            int j = i;
            while (j < _runs.Count && _runs[j].Start <= end)
            {
                start = System.Math.Min(start, _runs[j].Start);
                end = System.Math.Max(end, _runs[j].End);
                had += _runs[j].End - _runs[j].Start;
                j++;
            }
            _runs.RemoveRange(i, j - i);
            _runs.Insert(i, (start, end));
            _received += end - start - had;
        }

        // An ack: the offset of the chunk that landed, or -1 for ready, then how long in microseconds the
        // chunk sat here between coming off the wire and this going out. A build that predates offsets
        // reads the id and stops.
        public ControlMessage NextChunkRequest(IConnection target, long offset, int heldMicros = 0)
        {
            var msg = new ControlMessage(ControlMessage.Message.AssetNextChunkRequest);
            msg.Targets.Add(target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            w.Write(offset);
            w.Write(heldMicros);
            if (offset == -1)
                w.Write(ResumeOffset);
            msg.Payload = ms.ToArray();
            return msg;
        }

        public string FinalizeAndGetFile()
        {
            if (_sealed != null)
            {
                _sealed.Complete();
                _sealed = null;
            }
            else
            {
                _sink!.Flush();
                _sink.Dispose();
                _sink = null;
            }
            _finished = true;
            return _tempPath;
        }

        // A job let go of before it finished takes its partial file with it.
        public void Dispose()
        {
            if (_sealed != null)
            {
                try { _sealed.Dispose(); } catch { }
                _sealed = null;
            }
            if (_sink != null)
            {
                try { _sink.Dispose(); } catch { }
                _sink = null;
            }
            if (!_finished)
            {
                try { File.Delete(_tempPath); } catch { }
            }
        }
    }

    // Uploads in flight per peer, and in all. Byte budgets are spent on chunks, not on opening files:
    // charging each large file a megabyte here limited every peer to four active files. A file in flight
    // costs one start and a small window, so a body of a few hundred small parts goes many at a time.
    // -xlinka
    private const int MaxTransmitJobsPerPeer = 32;
    private const int MaxTransmitJobs = 128;

    // One peer's uploads: whose turn is next, and how far its acks have got.
    private sealed class PeerSend
    {
        public readonly List<FileTransmitJob> Jobs = new();
        public int Next;
        // The latest send time of any chunk this peer has acked. Past what a file is waiting on, the link
        // carried newer data than it, so the file's own chunks or start went missing on the far side.
        public double AckedSentAt;

    }

    private readonly Dictionary<IConnection, PeerSend> _peers = new();

    private PeerSend PeerFor(IConnection target)
    {
        if (!_peers.TryGetValue(target, out var peer))
            _peers[target] = peer = new PeerSend();
        return peer;
    }

    // SENDER STALLS
    //
    // A file is only started over when its receiver has said nothing about it for StallSeconds while acking
    // chunks this machine sent after the ones the file waits on: the link carried newer data, so the far side
    // lost the job. A fresh start lets it resume. A link that is merely slow or full acks nothing newer, and
    // starting its files over only put the same chunks on it twice and threw the receiver back to 0 %. A file
    // that stays lost through MaxStarts starts, or that hears nothing at all for DropSeconds, is let go with
    // an explicit no, so the slot frees and whoever asked hears why. Fields, not constants, so a test can
    // shorten them. -xlinka
    internal static double StallSeconds = 15;
    internal static double DropSeconds = 300;
    private const int MaxStarts = 3;
    private const double OvertakeSlackSeconds = 2;

    // REQUESTER RETRIES
    //
    // A request that has heard nothing back is asked again after RequestRetrySeconds, doubling to four times
    // that. A request still waiting its turn at the sender costs one small message per ask. One whose sender
    // has sent this machine no asset traffic at all for GiveUpSeconds fails, so a load shows an error instead
    // of a bar at 0 % for the rest of the session; one stuck behind other files from a sender that is
    // still sending waits its turn. -xlinka
    internal static double RequestRetrySeconds = 10;
    internal static double GiveUpSeconds = 300;
    // A receive buffer nothing has touched for this long is let go. Anything still wanted is asked for
    // again by its request and resumes or starts over.
    internal static double ReceiveIdleSeconds = 120;
    private const double SweepIntervalSeconds = 0.5;

    // When each peer last sent this machine a start or a chunk, read by the give-up rule.
    private readonly Dictionary<IConnection, double> _heardFrom = new();

    // What an incoming asset is called when the sender declared no format, or declared one we refuse.
    private const string UnknownFormat = ".asset";

    // The declared format ends up in a filename, so it is a peer-controlled path fragment and gets no
    // benefit of the doubt: a leading dot and a short run of ASCII alphanumerics, nothing else. That
    // rules out separators, "..", drive letters and every unicode lookalike in one pass, and a refusal
    // costs only the neutral extension plus the decoder's content sniff. -xlinka
    private static string SanitizeFormat(string? format)
    {
        if (string.IsNullOrEmpty(format) || format![0] != '.' || format.Length < 2 || format.Length > 12)
            return UnknownFormat;

        for (int i = 1; i < format.Length; i++)
        {
            char c = format[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
            if (!ok)
                return UnknownFormat;
        }

        return format;
    }

    // How many distinct assets the host fetches from owning peers at once to relay. Past that they wait
    // their turn rather than being refused: refusing them is what left most of a joiner's avatar grey for
    // everyone else, one request in eight answered and the rest told the asset did not exist. The queue
    // behind them is bounded too, against a peer asking for a flood of addresses. -xlinka
    private const int MaxConcurrentRelays = 16;
    private const int MaxQueuedRelays = 8192;

    // Uri.ToString() LOWERCASES the authority, and our machine ids are case-sensitive, so keying a
    // request on it makes a client-imported asset unfindable while a host-imported one works. That is
    // the same class of bug that once stopped peer meshes transferring at all; OriginalString is the
    // spelling the sender actually replicated. -xlinka
    private static string UriKey(Uri uri) => uri.OriginalString;

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // A derived address (a texture's sidecar or a smaller rung) is asked for on the chance its owner made
    // it, so a no is routine and not worth a warning.
    private static bool IsDerived(string uriStr) => uriStr.IndexOf('~') >= 0;

    // Holds the completion callback plus an optional per-chunk progress callback for an in-flight fetch. -xlinka
    private sealed class GatherCallbacks
    {
        public readonly Action<Uri, string> OnGathered;
        public readonly Action<Uri, long, long>? OnProgress; // (uri, totalBytes, receivedBytes)
        public GatherCallbacks(Action<Uri, string> onGathered, Action<Uri, long, long>? onProgress)
        {
            OnGathered = onGathered;
            OnProgress = onProgress;
        }

        private Task _notifications = Task.CompletedTask;

        // Callbacks run in order for this consumer, outside every transfer lock. A decoder or consumer
        // that blocks must not stop the other files or sessions. -xlinka
        public void Gathered(Uri uri, string path) => Notify(() => OnGathered(uri, path));
        public void Progress(Uri uri, long total, long received)
        {
            if (OnProgress != null)
                Notify(() => OnProgress(uri, total, received));
        }

        private void Notify(Action action)
        {
            _notifications = _notifications.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { LumoraLogger.Warn($"AssetTransferer: asset callback failed: {ex.Message}"); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    // One asset we asked a peer for: who we asked, everyone here waiting on it (none when the host only
    // relays it), and when we last heard anything about it.
    private sealed class PendingFetch
    {
        // A LIST of waiters, not one. Nine meshes of one avatar routinely resolve to the same asset, and
        // dropping the second requester's callback left its gather task awaiting a completion that could
        // never arrive - a permanent hang, not a slow load. Every waiter is answered from the one transfer.
        public readonly List<GatherCallbacks> Waiters = new();
        public readonly IConnection Target;
        public readonly double Since = Now;
        public double LastProgress = Now;
        public double LastAsked = Now;
        public int Asks = 1;

        public PendingFetch(IConnection target)
        {
            Target = target;
        }
    }

    private readonly object _lock = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<ControlMessage> _incoming = new();
    private readonly AutoResetEvent _incomingSignal = new(false);
    private readonly Thread _worker;
    private volatile bool _disposed;
    private readonly Dictionary<JobID, FileTransmitJob> _transmitJobs = new();
    private readonly Dictionary<JobID, FileReceiveJob> _receiveJobs = new();
    private readonly Dictionary<string, PendingFetch> _fetches = new();

    // Pending outbound transfers awaiting an initialize slot. Not a FIFO: RefreshJobs picks the job whose
    // target connection was served least recently, so one peer flooding requests can't starve another. -xlinka
    private readonly List<FileTransmitJob> _transmitJobsToInitialize = new();
    // Last-served ordinal per connection; lower = waited longer (absent = never served = -1). -xlinka
    private readonly Dictionary<IConnection, int> _connectionIndexes = new();
    private int _globalConnectionIndex;

    // uriStr -> the connections waiting for the host to relay that asset to them. The host fetches the
    // bytes from the owning peer once and fans them out to everyone in the list when they arrive. -xlinka
    private readonly Dictionary<string, List<IConnection>> _pendingRelays = new();
    // Relays being fetched from their owners now, and the ones waiting for one of those to finish.
    private readonly HashSet<string> _relaysStarted = new();
    private readonly Queue<string> _relayBacklog = new();

    private int _outboundIdPool;
    private double _nextSweep;

    public Session Session { get; }

    // Live transfer counts for diagnostics (the Network debug tab), guarded by the jobs' own lock.
    public int UploadJobCount { get { lock (_lock) { return _transmitJobs.Count + _transmitJobsToInitialize.Count; } } }
    public int DownloadJobCount { get { lock (_lock) { return _receiveJobs.Count; } } }
    public int PendingAssetRequestCount { get { lock (_lock) { return _fetches.Count; } } }
    public int PendingRelayCount { get { lock (_lock) { return _pendingRelays.Count; } } }

    public readonly struct AssetTransfer
    {
        public readonly Uri Uri;
        public readonly bool IsUpload;
        public readonly long Transferred;
        public readonly long Total;

        public AssetTransfer(Uri uri, bool isUpload, long transferred, long total)
        {
            Uri = uri;
            IsUpload = isUpload;
            Transferred = transferred;
            Total = total;
        }

        public float Fraction => Total > 0 ? (float)Transferred / Total : 0f;
    }

    // Safe to call from the render/UI thread.
    public List<AssetTransfer> GetActiveTransfers()
    {
        var list = new List<AssetTransfer>();
        lock (_lock)
        {
            foreach (var job in _transmitJobs.Values)
                list.Add(new AssetTransfer(job.AssetUri, true, job.SentBytes, job.TotalBytes));
            foreach (var job in _receiveJobs.Values)
                list.Add(new AssetTransfer(job.AssetUri, false, job.ReceivedBytes, job.TotalBytes));
        }
        return list;
    }

    // What this machine is waiting to receive, by address as it was asked for: bytes in so far and the
    // size, or both zero for one asked for whose first chunk has not come. Into the caller's list, so a
    // readout asking a few times a second allocates nothing.
    public void CollectDownloads(List<(string Address, long Received, long Total)> into)
    {
        lock (_lock)
        {
            foreach (var job in _receiveJobs.Values)
                into.Add((UriKey(job.AssetUri), job.ReceivedBytes, job.TotalBytes));
            foreach (var pair in _fetches)
                into.Add((pair.Key, 0, 0));
        }
    }

    // Every transferer of a session this machine is in, newest last. A machine can be in several at once
    // (its own home is hosted too), and a fetch has to go to the session whose people can answer it.
    private static readonly List<SessionAssetTransferer> _live = new();

    // Every machine id seen among this session's people, kept after they leave: an asset whose owner was
    // here and went is waited on, one whose owner never was is not.
    private readonly HashSet<string> _seenMachines = new();

    private void NoteMachines()
    {
        var users = Session?.World?.GetAllUsers();
        if (users == null)
            return;
        lock (_seenMachines)
        {
            foreach (var u in users)
            {
                var id = u.MachineID?.Value;
                if (!string.IsNullOrEmpty(id))
                    _seenMachines.Add(id!);
            }
        }
    }

    private static List<SessionAssetTransferer> Live()
    {
        lock (_live)
            return _live.Where(t => t.Session?.World is { IsDisposed: false } && !t.Session.IsDisposed).ToList();
    }

    // Whether a machine owning this local:// address is in any session this machine is in now.
    public static bool IsOwnerPresent(string uri)
    {
        if (!uri.StartsWith("local://", StringComparison.Ordinal))
            return false;
        var machineId = ExtractMachineId(uri);
        foreach (var t in Live())
        {
            var users = t.Session.World.GetAllUsers();
            if (users != null && users.Any(u => !u.IsLocal && u.MachineID?.Value == machineId))
                return true;
        }
        return false;
    }



    public SessionAssetTransferer(Session session)
    {
        Session = session;
        lock (_live)
            _live.Add(this);
        _worker = new Thread(ProcessIncoming)
        {
            Name = "SessionAssetTransfer",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    // One reader preserves start/chunk ordering without tying disk work to a frame or the decode thread.
    // Ownership of the message passes to this queue, including when the session is closing. -xlinka
    internal void EnqueueMessage(ControlMessage message)
    {
        if (_disposed)
        {
            message.Dispose();
            return;
        }
        _incoming.Enqueue(message);
        _incomingSignal.Set();
    }

    // Its own thread, not the pool's: an avatar load fills every pool thread with decodes for seconds, and a
    // chunk or an ack waiting behind them for a thread held the whole link up. -xlinka
    private void ProcessIncoming()
    {
        double nextSweep = Now + SweepIntervalSeconds;
        while (!_disposed)
        {
            double wait = nextSweep - Now;
            if (wait > 0 && _incoming.IsEmpty)
                _incomingSignal.WaitOne(TimeSpan.FromSeconds(wait));
            while (_incoming.TryDequeue(out var message))
            {
                try
                {
                    ProcessMessage(message);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Warn($"AssetTransferer: processing {message.ControlMessageType} failed: {ex.Message}");
                }
                finally
                {
                    message.Dispose();
                }
            }
            if (Now >= nextSweep)
            {
                nextSweep = Now + SweepIntervalSeconds;
                try
                {
                    Update();
                }
                catch (Exception ex)
                {
                    LumoraLogger.Warn($"AssetTransferer: sweep failed: {ex.Message}");
                }
            }
        }
        while (_incoming.TryDequeue(out var left))
            left.Dispose();
    }

    // File reads and writes, on threads of their own for the same reason. Two, so one file on a stalled disk
    // does not stop the others. Shared by every session's transferer. -xlinka
    private static class Disk
    {
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> _work = Start();

        private static System.Collections.Concurrent.BlockingCollection<Action> Start()
        {
            var work = new System.Collections.Concurrent.BlockingCollection<Action>();
            for (int i = 0; i < 2; i++)
            {
                new Thread(() =>
                {
                    foreach (var item in work.GetConsumingEnumerable())
                    {
                        try { item(); }
                        catch (Exception ex) { LumoraLogger.Warn($"AssetTransferer: disk work failed: {ex.Message}"); }
                    }
                })
                {
                    Name = "SessionAssetDisk",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                }.Start();
            }
            return work;
        }

        public static void Run(Action action) => _work.Add(action);
    }

    // The session to ask for a local:// asset this machine does not hold. A single "active" transferer used
    // to answer this, whichever session started last, and it was cleared when that session closed even with
    // others still open: a guest who had joined a world and then opened another asked the wrong host for
    // everything in the first, and one who left the second asked nobody. In order: a session the owning
    // machine is in; a session we joined (its host may hold a copy); the focused world's; the newest. -xlinka
    public static SessionAssetTransferer? ForLocalAsset(string uri)
    {
        var live = Live();
        if (live.Count == 0)
            return null;

        var machineId = uri.StartsWith("local://", StringComparison.Ordinal) ? ExtractMachineId(uri) : null;
        if (!string.IsNullOrEmpty(machineId))
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var world = live[i].Session.World;
                var users = world.GetAllUsers();
                if (users != null && users.Any(u => !u.IsLocal && u.MachineID?.Value == machineId))
                    return live[i];
            }
        }

        SessionAssetTransferer? joined = null, focused = null;
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var world = live[i].Session.World;
            if (!world.IsAuthority && live[i].Session.Connections?.HostConnection != null)
            {
                if (world.IsFocused)
                    return live[i];
                joined ??= live[i];
            }
            if (world.IsFocused)
                focused ??= live[i];
        }
        return joined ?? focused ?? live[^1];
    }

    // Request an asset by URI. Sends AssetRequest to the host (clients) or to the asset owner's connection (if
    // we are the authority).
    public void RequestAsset(Uri assetUri, Action<Uri, string> onGathered, Action<Uri, long, long>? onProgress = null)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                onGathered(assetUri, null!);
                return;
            }
            var uriStr = UriKey(assetUri);
            if (_fetches.TryGetValue(uriStr, out var waiting))
            {
                // Already in flight: join the queue rather than being forgotten.
                waiting.Waiters.Add(new GatherCallbacks(onGathered, onProgress));
                return;
            }

            IConnection target;
            if (Session.World.IsAuthority)
            {
                // Authority -> request from the machine that owns this local:// asset
                var machineId = ExtractMachineId(assetUri);
                var owner = Session.World.GetAllUsers()
                    ?.FirstOrDefault(u => u.MachineID?.Value == machineId);
                if (owner == null || !Session.Connections.TryGetConnection(owner, out target))
                {
                    LumoraLogger.Warn($"AssetTransferer: no peer for machine '{machineId}', cannot fetch {uriStr}");
                    onGathered(assetUri, null!);
                    return;
                }
            }
            else
            {
                target = Session.Connections.HostConnection;
            }

            if (target == null)
            {
                onGathered(assetUri, null!);
                return;
            }

            var fetch = new PendingFetch(target);
            fetch.Waiters.Add(new GatherCallbacks(onGathered, onProgress));
            _fetches[uriStr] = fetch;
            SendAssetRequest(assetUri, target);
            LumoraLogger.Debug($"AssetTransferer: requested {uriStr}");
        }
    }

    // Send a bare AssetRequest control message to target. Does NOT register a
    // completion callback - callers that want one register in _fetches themselves
    // (a client fetch) or track their own pending list (the host relay). -xlinka
    private void SendAssetRequest(Uri assetUri, IConnection target)
    {
        var msg = new ControlMessage(ControlMessage.Message.AssetRequest);
        msg.Targets.Add(target);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(UriKey(assetUri));
        msg.Payload = ms.ToArray();
        Session.Sync.EnqueueForTransmission(msg);
    }

    // Why an asset could not be had. Sent after the address, where a build that does not read it stops.
    private const byte NotHeld = 0;
    private const byte OwnerGone = 1;

    private void SendNotAvailable(string uriStr, IConnection target, bool ownerGone = false)
    {
        if (target == null)
            return;

        var msg = new ControlMessage(ControlMessage.Message.AssetNotAvailable);
        msg.Targets.Add(target);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(uriStr);
        w.Write(ownerGone ? OwnerGone : NotHeld);
        msg.Payload = ms.ToArray();
        Session.Sync.EnqueueForTransmission(msg);
    }

    // Addresses that just failed because their owner left, not because the owner said no. Read once by
    // whoever was waiting on the fetch, to tell "wait for them to come back" from "it does not exist".
    // Lock-free: it is read from inside another transferer's callback, and taking this one's lock there
    // would order two sessions' locks against each other. -xlinka
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _ownerGone = new();

    // True when this machine just lost a fetch of this address to its owner leaving, and the owner is one
    // this machine saw in the session: worth waiting for. Consumes the note.
    public static bool OwnerLeft(string uri)
    {
        if (!uri.StartsWith("local://", StringComparison.Ordinal))
            return false;
        var machineId = ExtractMachineId(uri);
        double now = Now;
        foreach (var t in Live())
        {
            if (!t._ownerGone.TryRemove(uri, out var at) || now - at >= 120)
                continue;
            t.NoteMachines();
            lock (t._seenMachines)
            {
                if (t._seenMachines.Contains(machineId))
                    return true;
            }
        }
        return false;
    }

    public void ProcessMessage(ControlMessage message)
    {
        lock (_lock)
        {
            if (_disposed || message.Sender?.IsOpen != true)
                return;
            // Per-message try/catch so a malformed payload from one peer doesn't take
            // down the asset transfer system for everyone. Bounds-check failures from
            // the decoders surface here as InvalidDataException.
            try
            {
                switch (message.ControlMessageType)
                {
                    case ControlMessage.Message.AssetRequest:       HandleAssetRequest(message);       break;
                    case ControlMessage.Message.AssetTransmissionStart: HandleTransmissionStart(message); break;
                    case ControlMessage.Message.AssetChunk:         HandleChunk(message);              break;
                    case ControlMessage.Message.AssetNextChunkRequest: HandleNextChunkRequest(message); break;
                    case ControlMessage.Message.AssetNotAvailable:  HandleNotAvailable(message);       break;
                }
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"AssetTransferer: dropped malformed {message.ControlMessageType} from {message.Sender?.Identifier}: {ex.Message}");
            }
            RefreshJobs();
        }
    }

    // Runs independently of world frames. Retries silent peers, expires idle receives and fills free slots.
    public void Update()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            double now = Now;
            if (now < _nextSweep)
                return;
            _nextSweep = now + SweepIntervalSeconds;
            NoteMachines();

            try
            {
                foreach (var job in _transmitJobs.Values.ToList())
                {
                    if (job.Busy)
                        continue;
                    var peer = PeerFor(job.Target);
                    if (job.Ready && job.OutBytes == 0)
                    {
                        job.LastHeard = now;
                        continue;
                    }
                    double silent = now - job.LastHeard;
                    var key = new JobID(job.Target, job.ID);
                    if (silent >= StallSeconds && _transmitJobsToInitialize.Any(pending => pending.Target == job.Target))
                    {
                        // Waiting files get a turn even when every active file is silent. Waiting for a newer
                        // ack here would deadlock the queue: none of those files can start to send one.
                        DropTransmit(key, dispose: false);
                        _transmitJobsToInitialize.Add(job);
                        continue;
                    }
                    // Nothing newer than what this file waits on has been acked: it is queued behind the rest of
                    // a full link, or the receiver froze, not a lost job.
                    double waitingOn = job.Ready ? job.OldestOut() : job.StartSentAt;
                    if (waitingOn <= 0 || peer.AckedSentAt <= waitingOn + OvertakeSlackSeconds)
                    {
                        if (silent >= DropSeconds)
                            GiveUpTransmit(key, job, $"nothing acked by {job.Target.Identifier} for {silent:0} s");
                        continue;
                    }
                    if (silent < StallSeconds)
                        continue;
                    if (job.Starts >= MaxStarts)
                    {
                        GiveUpTransmit(key, job, $"{job.Target.Identifier} answered none of {job.Starts} starts");
                        continue;
                    }
                    Restart(job, now, $"no answer for {silent:0}s");
                }

                // A long queue at the sender asks again for every asset in it at once, so one line per sweep.
                int asked = 0;
                string? firstAsked = null;
                List<string>? givenUp = null;
                foreach (var (uriStr, fetch) in _fetches)
                {
                    if (fetch.Target == null || !fetch.Target.IsOpen)
                        continue;
                    double waited = now - fetch.LastProgress;
                    double quiet = now - System.Math.Max(fetch.Since, _heardFrom.TryGetValue(fetch.Target, out var heard) ? heard : 0);
                    if (waited >= GiveUpSeconds && quiet >= GiveUpSeconds)
                    {
                        (givenUp ??= new List<string>()).Add(uriStr);
                        continue;
                    }
                    double wait = RequestRetrySeconds * System.Math.Min(1 << System.Math.Min(fetch.Asks - 1, 2), 4);
                    if (now - fetch.LastAsked < wait || waited < wait)
                        continue;
                    fetch.Asks++;
                    fetch.LastAsked = now;
                    asked++;
                    firstAsked ??= $"{uriStr} from {fetch.Target.Identifier} (ask {fetch.Asks})";
                    SendAssetRequest(new Uri(uriStr), fetch.Target);
                }
                if (asked > 0)
                    LumoraLogger.Warn($"AssetTransferer: nothing back yet on {asked} request(s), asked again; first {firstAsked}");
                if (givenUp != null)
                {
                    LumoraLogger.Warn($"AssetTransferer: giving up on {givenUp.Count} request(s), nothing from their sender for {GiveUpSeconds:0} s; first {givenUp[0]}");
                    foreach (var uriStr in givenUp)
                        FailFetch(uriStr);
                }

                List<JobID>? idle = null;
                foreach (var (key, job) in _receiveJobs)
                {
                    if (now - job.LastProgress >= ReceiveIdleSeconds)
                        (idle ??= new List<JobID>()).Add(key);
                }
                if (idle != null)
                {
                    foreach (var key in idle)
                    {
                        LumoraLogger.Debug($"AssetTransferer: letting go of an idle receive of {_receiveJobs[key].AssetUri} from {key.Connection?.Identifier}");
                        DropReceive(key);
                    }
                }
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"AssetTransferer: sweep failed: {ex.Message}");
            }

            RefreshJobs();
        }
    }

    // A fresh start under the same id lets the receiver resume retained ranges or request the whole file.
    private void Restart(FileTransmitJob job, double now, string why)
    {
        job.Starts++;
        job.LastHeard = now;
        var log = job.Starts == 2 ? (Action<string>)LumoraLogger.Warn : LumoraLogger.Debug;
        log($"AssetTransferer: {why} from {job.Target.Identifier} on {UriKey(job.AssetUri)} (job {job.ID}), sending its start again (start {job.Starts})");
        job.ClearFlight();
        job.StartSentAt = now;
        Session.Sync.EnqueueForTransmission(job.Initialize());
    }

    // Let go of an upload nobody is taking, with a no to the receiver so its fetch ends instead of waiting.
    private void GiveUpTransmit(JobID key, FileTransmitJob job, string why)
    {
        LumoraLogger.Warn($"AssetTransferer: dropping upload of {UriKey(job.AssetUri)} (job {job.ID}): {why}");
        DropTransmit(key);
        SendNotAvailable(UriKey(job.AssetUri), job.Target);
    }

    private void DropTransmit(JobID key, bool dispose = true)
    {
        if (!_transmitJobs.Remove(key, out var job))
            return;
        if (_peers.TryGetValue(job.Target, out var peer))
        {
            job.ClearFlight();
            int at = peer.Jobs.IndexOf(job);
            if (at >= 0)
            {
                peer.Jobs.RemoveAt(at);
                if (peer.Next > at)
                    peer.Next--;
            }
        }
        if (dispose && !job.Busy)
            job.Dispose();
    }

    private void DropReceive(JobID key)
    {
        if (_receiveJobs.Remove(key, out var job))
        {
            job.PendingChunks.Clear();
            job.PendingBytes = 0;
            if (!job.Writing)
                job.Dispose();
        }
    }

    public void ConnectionClosed(IConnection connection)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            foreach (var key in _transmitJobs.Keys.Where(k => k.Connection == connection).ToList())
                DropTransmit(key);
            _peers.Remove(connection);
            _heardFrom.Remove(connection);

            foreach (var key in _receiveJobs.Keys.Where(k => k.Connection == connection).ToList())
                DropReceive(key);

            // Everything we were fetching FROM the peer that just dropped fails, waiters and relays alike.
            // Keyed on who we asked rather than on receive jobs, which also catches a request the peer
            // never got round to starting. -xlinka
            // On the authority those are owners, and an owner leaving is not the asset ceasing to exist. A
            // joiner's only target is its host, and losing that is losing the session.
            var asked = _fetches.Where(kv => kv.Value.Target == connection).Select(kv => kv.Key).ToList();
            foreach (var uriStr in asked)
                FailFetch(uriStr, ownerGone: Session.World?.IsAuthority == true);

            // A downstream requester dropped - stop tracking it so we don't relay into a dead connection.
            // If that leaves a relay with nobody waiting, drop the whole entry; a fetch already under way
            // for it still finishes into this machine's store. -xlinka
            var emptiedRelays = new List<string>();
            foreach (var kvp in _pendingRelays)
            {
                kvp.Value.RemoveAll(c => c == connection);
                if (kvp.Value.Count == 0)
                    emptiedRelays.Add(kvp.Key);
            }
            foreach (var uriStr in emptiedRelays)
                _pendingRelays.Remove(uriStr);

            // Drop any pending outbound transfers aimed at the dead peer + forget its served-index. -xlinka
            _transmitJobsToInitialize.RemoveAll(j => j.Target == connection);
            _connectionIndexes.Remove(connection);

            PumpRelayBacklog();
            RefreshJobs();
        }
    }

    private void HandleAssetRequest(ControlMessage message)
    {
        string uriStr;
        using (var ms = new MemoryStream(message.Payload))
        using (var r = new BinaryReader(ms))
            uriStr = r.ReadBoundedString(NetworkLimits.MaxAssetUriBytes);

        var assetUri = new Uri(uriStr);

        // The same peer asking again for what we already have for it: it heard nothing for a while. A job
        // still waiting for a slot stays in line, and so does one whose chunks are still being acked: they
        // are on their way behind the rest of the link. One its receiver has gone quiet on is started over,
        // and the receiver resumes from what it holds. -xlinka
        var existing = FindTransmitJob(message.Sender, uriStr);
        if (existing != null)
        {
            if (!existing.Busy && _transmitJobs.ContainsKey(new JobID(existing.Target, existing.ID))
                && Now - existing.LastHeard >= StallSeconds)
                Restart(existing, Now, "asked again");
            return;
        }

        var localDB = Engine.Current?.LocalDB;
        string localPath = null!;
        if (assetUri.Scheme == "local" && localDB != null)
            localPath = localDB.GetFilePath(uriStr);

        if (localPath != null && File.Exists(localPath))
        {
            var id = _outboundIdPool++;
            _transmitJobsToInitialize.Add(new FileTransmitJob(localPath, assetUri, message.Sender, id));
            LumoraLogger.Debug($"AssetTransferer: queued outbound transfer for {uriStr} (job {id})");
            return;
        }

        // We don't have it ourselves. If we're the authority and this is a peer-owned local:// asset,
        // we can still get it: forward the request to the owning peer and relay the bytes back. That's
        // how an asset one user imported reaches another user who joined later, even when neither of
        // them is us. -xlinka
        var ourMachineId = localDB?.MachineId;
        var assetMachineId = ExtractMachineId(assetUri);
        bool canRelay = Session.World.IsAuthority
            && assetUri.Scheme == "local"
            && !string.IsNullOrEmpty(assetMachineId)
            && assetMachineId != ourMachineId;

        if (canRelay)
        {
            RelayFromOwner(assetUri, message.Sender);
            return;
        }

        if (IsDerived(uriStr))
            LumoraLogger.Debug($"AssetTransferer: no {uriStr} here");
        else
            LumoraLogger.Warn($"AssetTransferer: cannot serve {uriStr} - not found locally");
        SendNotAvailable(uriStr, message.Sender);
    }

    private FileTransmitJob? FindTransmitJob(IConnection target, string uriStr)
    {
        foreach (var job in _transmitJobs.Values)
        {
            if (job.Target == target && UriKey(job.AssetUri) == uriStr)
                return job;
        }
        foreach (var job in _transmitJobsToInitialize)
        {
            if (job.Target == target && UriKey(job.AssetUri) == uriStr)
                return job;
        }
        return null;
    }

    // Host-as-relay: fetch a peer-owned asset from its owner and forward it to requester.
    // Coalesces multiple requesters of the same asset onto a single fetch; the bytes are handed to all of
    // them when they arrive (see ServePendingRelays). -xlinka
    private void RelayFromOwner(Uri assetUri, IConnection requester)
    {
        var uriStr = UriKey(assetUri);

        // Already gathering this exact asset - either another requester's relay, one waiting its turn, or
        // our own in-flight client fetch. Pile this requester on and let the one completion serve everyone.
        // This is also what stops a second request from opening a duplicate receive job. -xlinka
        bool alreadyInFlight = _pendingRelays.ContainsKey(uriStr) || _fetches.ContainsKey(uriStr);
        AddPendingRelay(uriStr, requester);
        if (alreadyInFlight)
            return;

        if (ResolveOwner(uriStr) == null)
        {
            LumoraLogger.Warn($"AssetTransferer: no connected peer owns {uriStr}, cannot relay");
            FailPendingRelays(uriStr, ownerGone: true);
            return;
        }

        if (_relaysStarted.Count >= MaxConcurrentRelays)
        {
            if (_relayBacklog.Count >= MaxQueuedRelays)
            {
                LumoraLogger.Warn($"AssetTransferer: {MaxQueuedRelays} relays already waiting, refusing {uriStr}");
                FailPendingRelays(uriStr);
                return;
            }
            _relayBacklog.Enqueue(uriStr);
            return;
        }

        StartRelay(uriStr);
    }

    private IConnection? ResolveOwner(string uriStr)
    {
        var machineId = ExtractMachineId(uriStr);
        var owner = Session.World.GetAllUsers()?.FirstOrDefault(u => u.MachineID?.Value == machineId);
        if (owner == null || !Session.Connections.TryGetConnection(owner, out var ownerConn))
            return null;
        return ownerConn;
    }

    private void StartRelay(string uriStr)
    {
        var ownerConn = ResolveOwner(uriStr);
        if (ownerConn == null)
        {
            FailPendingRelays(uriStr, ownerGone: true);
            return;
        }
        _relaysStarted.Add(uriStr);
        // Tracked like any request of ours, waiters or not, so a quiet owner is asked again and a
        // departed one fails everyone waiting instead of leaving the relay open for good.
        _fetches[uriStr] = new PendingFetch(ownerConn);
        SendAssetRequest(new Uri(uriStr), ownerConn);
        LumoraLogger.Debug($"AssetTransferer: relaying {uriStr} from {ownerConn.Identifier}");
    }

    // Starts the relays that were waiting, as far as the cap allows. One whose requesters all left, or
    // that something else is already fetching, is passed over.
    private void PumpRelayBacklog()
    {
        while (_relaysStarted.Count < MaxConcurrentRelays && _relayBacklog.Count > 0)
        {
            var uriStr = _relayBacklog.Dequeue();
            if (!_pendingRelays.ContainsKey(uriStr) || _fetches.ContainsKey(uriStr))
                continue;
            StartRelay(uriStr);
        }
    }

    private void AddPendingRelay(string uriStr, IConnection requester)
    {
        if (requester == null)
            return;

        if (_pendingRelays.TryGetValue(uriStr, out var list))
        {
            if (!list.Contains(requester))
                list.Add(requester);
        }
        else
        {
            _pendingRelays[uriStr] = new List<IConnection> { requester };
        }
    }

    private void ServePendingRelays(Uri assetUri, string localPath)
    {
        var uriStr = UriKey(assetUri);
        _relaysStarted.Remove(uriStr);
        if (_pendingRelays.TryGetValue(uriStr, out var requesters))
        {
            _pendingRelays.Remove(uriStr);

            // Each requester gets its own job (its own send cursor); the job reads the file as it sends.
            // -xlinka
            foreach (var requester in requesters)
            {
                if (requester == null || FindTransmitJob(requester, uriStr) != null)
                    continue;
                var id = _outboundIdPool++;
                _transmitJobsToInitialize.Add(new FileTransmitJob(localPath, assetUri, requester, id));
            }
            LumoraLogger.Debug($"AssetTransferer: relayed {uriStr} to {requesters.Count} requester(s)");
        }
        PumpRelayBacklog();
    }

    private void FailPendingRelays(string uriStr, bool ownerGone = false)
    {
        _relaysStarted.Remove(uriStr);
        if (_pendingRelays.TryGetValue(uriStr, out var requesters))
        {
            _pendingRelays.Remove(uriStr);
            foreach (var requester in requesters)
                SendNotAvailable(uriStr, requester, ownerGone);
        }
        PumpRelayBacklog();
    }

    // A fetch that cannot finish: everyone here waiting on it hears so, and so does everyone the host was
    // going to relay it to.
    private void FailFetch(string uriStr, bool ownerGone = false)
    {
        if (ownerGone)
            _ownerGone[uriStr] = Now;
        if (_fetches.Remove(uriStr, out var fetch))
        {
            var uri = new Uri(uriStr);
            foreach (var cb in fetch.Waiters)
                cb.Gathered(uri, null!);
        }
        foreach (var key in _receiveJobs.Where(kv => UriKey(kv.Value.AssetUri) == uriStr).Select(kv => kv.Key).ToList())
            DropReceive(key);
        FailPendingRelays(uriStr, ownerGone);
    }

    private void NoteProgress(string uriStr, double now)
    {
        if (_fetches.TryGetValue(uriStr, out var fetch))
            fetch.LastProgress = now;
    }

    private void HandleTransmissionStart(ControlMessage message)
    {
        // The temp name is chosen from the format the sender declares, which is only known once the
        // header is read, so the path is produced by callback rather than up front.
        var localDB = Engine.Current?.LocalDB;

        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);
        var job = new FileReceiveJob(message.Sender, r, format =>
            localDB?.GetTempFilePath() ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + format), seal: localDB != null);
        double now = Now;
        job.LastProgress = now;
        _heardFrom[message.Sender] = now;
        // A repeated start keeps verified ranges for the same content instead of throwing progress away.
        var key = new JobID(message.Sender, job.ID);
        if (_receiveJobs.TryGetValue(key, out var previous)
            && UriKey(previous.AssetUri) == UriKey(job.AssetUri) && previous.TotalBytes == job.TotalBytes
            && previous.Format == job.Format)
        {
            job.Dispose();
            job = previous;
            job.LastProgress = now;
        }
        else
        {
            DropReceive(key);
            _receiveJobs[key] = job;
        }
        NoteProgress(UriKey(job.AssetUri), now);

        if (job.Writing)
        {
            job.RestartPending = true;
            return;
        }
        if (job.IsDone)
        {
            // Nothing to pull: an empty file is whole the moment it starts.
            WriteChunk(key, job);
            return;
        }
        Session.Sync.EnqueueForTransmission(job.NextChunkRequest(message.Sender, -1));
        LumoraLogger.Debug($"AssetTransferer: receiving {job.AssetUri} as '{job.Format}' ({job.ID}, {job.TotalBytes} bytes)");
    }

    private void HandleChunk(ControlMessage message)
    {
        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);
        int id = r.ReadInt32();
        var key = new JobID(message.Sender, id);
        _heardFrom[message.Sender] = Now;
        if (!_receiveJobs.TryGetValue(key, out var job))
            return;

        if (job.PendingBytes + message.Payload.Length > 2 * PeerWindow)
        {
            DropReceive(key);
            throw new InvalidDataException("Too many asset bytes waiting for disk.");
        }
        job.PendingChunks.Enqueue((message.Payload, message.Arrived));
        job.PendingBytes += message.Payload.Length;
        WriteChunk(key, job);
    }

    // Each file has at most one write running, on a disk thread, taking whatever chunks have piled up for it
    // since the last. Its disk wait and final flush hold no session lock, so a slow destination cannot delay
    // another download's acknowledgements. -xlinka
    private void WriteChunk(JobID key, FileReceiveJob job)
    {
        if (job.Writing || (job.PendingChunks.Count == 0 && !job.IsDone))
            return;
        job.Writing = true;
        var batch = new List<(byte[] Payload, long Arrived)>(System.Math.Min(job.PendingChunks.Count, WriteBatchChunks));
        while (batch.Count < WriteBatchChunks && job.PendingChunks.TryDequeue(out var chunk))
        {
            job.PendingBytes -= chunk.Payload.Length;
            batch.Add(chunk);
        }
        Disk.Run(() =>
        {
            var offsets = new List<long>(batch.Count);
            string? path = null;
            Exception? failure = null;
            try
            {
                foreach (var chunk in batch)
                {
                    using var reader = new BinaryReader(new MemoryStream(chunk.Payload));
                    reader.ReadInt32();
                    offsets.Add(job.ApplyChunk(reader));
                }
                if (job.IsDone)
                {
                    path = job.FinalizeAndGetFile();
                    var db = Engine.Current?.LocalDB;
                    // Adopt once before either a local waiter or a relay uses the same file.
                    if (db != null)
                        path = db.AdoptGatheredAsset(UriKey(job.AssetUri), path, job.Format, job.SealKey)
                            ?? throw new IOException("Could not store the received asset.");
                }
            }
            catch (Exception ex) { failure = ex; }
            lock (_lock)
            {
                job.Writing = false;
                if (_disposed || !_receiveJobs.TryGetValue(key, out var current) || current != job)
                {
                    job.Dispose();
                    return;
                }
                if (failure != null)
                {
                    DropReceive(key);
                    LumoraLogger.Warn($"AssetTransferer: receiving {UriKey(job.AssetUri)} failed: {failure.Message}");
                    return;
                }
                double now = Now;
                job.LastProgress = now;
                var uri = UriKey(job.AssetUri);
                NoteProgress(uri, now);
                if (job.RestartPending)
                {
                    job.RestartPending = false;
                    Session.Sync.EnqueueForTransmission(job.NextChunkRequest(job.Source, -1));
                }
                for (int i = 0; i < offsets.Count; i++)
                {
                    long arrived = batch[i].Arrived;
                    int held = arrived <= 0 ? 0 : (int)System.Math.Clamp(
                        (now - arrived / (double)Stopwatch.Frequency) * 1e6, 0, int.MaxValue);
                    Session.Sync.EnqueueForTransmission(job.NextChunkRequest(job.Source, offsets[i], held));
                }
                if (path != null)
                {
                    _receiveJobs.Remove(key);
                    job.PendingChunks.Clear();
                    job.PendingBytes = 0;
                    if (_fetches.Remove(uri, out var fetch))
                        foreach (var cb in fetch.Waiters)
                            cb.Gathered(job.AssetUri, path);
                    ServePendingRelays(job.AssetUri, path);
                    RefreshJobs();
                    return;
                }
                if (_fetches.TryGetValue(uri, out var progress))
                    foreach (var cb in progress.Waiters)
                        cb.Progress(job.AssetUri, job.TotalBytes, job.ReceivedBytes);
                WriteChunk(key, job);
            }
        });
    }

    private void HandleNextChunkRequest(ControlMessage message)
    {
        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);
        int id = r.ReadInt32();
        long? offset = ms.Length - ms.Position >= sizeof(long) ? r.ReadInt64() : null;
        if (ms.Length - ms.Position >= sizeof(int))
            r.ReadInt32();
        long resume = ms.Length - ms.Position >= sizeof(long) ? r.ReadInt64() : 0;

        var key = new JobID(message.Sender, id);
        if (!_transmitJobs.TryGetValue(key, out var job))
            return;
        var peer = PeerFor(message.Sender);
        job.LastHeard = Now;

        if (!job.Ready)
        {
            // The first ask of this start. A receiver that names chunks says ready with -1, so an ack it
            // sent for the start this one replaced is not taken for it.
            if (offset is long named && named != -1)
                return;
            if (offset == -1)
                job.ResumeAt(resume);
            job.Ready = true;
            job.Lanes = offset == -1;
        }
        else
        {
            var (at, size) = job.Acked(offset);
            if (size > 0)
                peer.AckedSentAt = System.Math.Max(peer.AckedSentAt, at);
        }

        if (job.AllSent && job.Drained)
        {
            DropTransmit(key);
            LumoraLogger.Debug($"AssetTransferer: finished sending job {id}");
        }
        Pump(peer);
    }

    // Round the peer's files a chunk at a time, with a window share reserved for each. Shares shrink when
    // files join, but bytes already sent keep draining: they cannot spend a new file's allowance. Otherwise
    // silent files can fill the peer's entire window and prevent a healthy file from sending its first chunk.
    // -xlinka
    private void Pump(PeerSend peer)
    {
        var jobs = peer.Jobs;
        int passed = 0;
        double now = Now;
        long share = System.Math.Max(2L * ChunkSize, System.Math.Min(JobWindow, PeerWindow / System.Math.Max(1, jobs.Count)));
        while (jobs.Count > 0 && passed < jobs.Count)
        {
            if (peer.Next >= jobs.Count)
                peer.Next = 0;
            var job = jobs[peer.Next++];
            if (!job.Ready || job.AllSent)
            {
                passed++;
                continue;
            }
            if (job.Prepared.Count < ReadAheadChunks && !job.Busy && job.ReadTo < job.TotalBytes)
                ReadChunks(job);
            if (job.OutBytes + ChunkSize > share || !job.Prepared.TryDequeue(out var chunk))
            {
                passed++;
                continue;
            }
            passed = 0;
            long offset = job.SentBytes;
            int size = (int)System.Math.Min(ChunkSize, job.TotalBytes - offset);
            job.Advance(size);
            chunk.Bulk = job.Lanes;
            if (job.Lanes || !job.AllSent)
            {
                job.Track(offset, now, size);
            }
            Session.Sync.EnqueueForTransmission(chunk);
            if (job.AllSent && job.Drained)
            {
                DropTransmit(new JobID(job.Target!, job.ID));
                LumoraLogger.Debug($"AssetTransferer: finished sending job {job.ID}");
            }
        }
    }

    private void ReadChunks(FileTransmitJob job)
    {
        job.Reading = true;
        long offset = job.ReadTo;
        int count = ReadAheadChunks - job.Prepared.Count;
        int generation = job.Generation;
        Disk.Run(() =>
        {
            List<ControlMessage>? chunks = null;
            Exception? failure = null;
            try { chunks = job.ReadChunks(offset, count); }
            catch (Exception ex) { failure = ex; }
            lock (_lock)
            {
                job.Reading = false;
                var key = new JobID(job.Target, job.ID);
                bool live = !_disposed && _transmitJobs.TryGetValue(key, out var current) && current == job;
                if (!live || generation != job.Generation || offset != job.ReadTo)
                {
                    if (chunks != null)
                        foreach (var stale in chunks)
                            stale.Dispose();
                    if (!live && !_transmitJobsToInitialize.Contains(job))
                        job.Dispose();
                    if (live)
                        Pump(PeerFor(job.Target));
                    return;
                }
                if (failure != null)
                {
                    LumoraLogger.Warn($"AssetTransferer: reading {UriKey(job.AssetUri)} failed: {failure.Message}");
                    DropTransmit(key);
                    SendNotAvailable(UriKey(job.AssetUri), job.Target);
                    RefreshJobs();
                    return;
                }
                foreach (var chunk in chunks!)
                {
                    job.Prepared.Enqueue(chunk);
                    job.ReadTo += System.Math.Min(ChunkSize, job.TotalBytes - job.ReadTo);
                }
                Pump(PeerFor(job.Target));
            }
        });
    }

    private void HandleNotAvailable(ControlMessage message)
    {
        string uriStr;
        bool ownerGone = false;
        using (var ms = new MemoryStream(message.Payload))
        using (var r = new BinaryReader(ms))
        {
            uriStr = r.ReadBoundedString(NetworkLimits.MaxAssetUriBytes);
            if (ms.Position < ms.Length)
                ownerGone = r.ReadByte() == OwnerGone;
        }

        // Only the peer we asked can turn us down. Anyone else saying it would fail a fetch that is going
        // fine, and on the host every relay waiting on it.
        if (!_fetches.TryGetValue(uriStr, out var failed) || (failed.Target != null && failed.Target != message.Sender))
            return;

        if (IsDerived(uriStr))
            LumoraLogger.Debug($"AssetTransferer: {message.Sender?.Identifier} has no {uriStr}");
        else
            LumoraLogger.Warn($"AssetTransferer: peer reported AssetNotAvailable for {uriStr}{(ownerGone ? " (its owner is not in the session)" : "")}");
        FailFetch(uriStr, ownerGone);
    }

    // Pull the owner machine id out of a local://{machineId}/{hash} URI. Read from the ORIGINAL string,
    // not Uri.Host - Uri.Host lowercases the authority, which would mangle the case-sensitive id stamped on
    // User.MachineID and break owner resolution. -xlinka
    private static string ExtractMachineId(Uri assetUri)
    {
        var s = assetUri.OriginalString;
        const string prefix = "local://";
        if (!s.StartsWith(prefix, StringComparison.Ordinal))
            return assetUri.Host;
        return ExtractMachineId(s);
    }

    private static string ExtractMachineId(string s)
    {
        const string prefix = "local://";
        int start = prefix.Length;
        int slash = s.IndexOf('/', start);
        return slash > start ? s.Substring(start, slash - start) : s.Substring(start);
    }

    private void RefreshJobs()
    {
        // Least-recently-served selection among the peers with room: of all pending transfers, start the
        // one whose target connection has waited longest (a never-served connection sorts first). This
        // stops a single peer that floods requests from monopolizing the outbound slots, and the per-peer
        // caps stop a peer that has gone quiet from collecting them. -xlinka
        Dictionary<IConnection, int>? active = null;
        while (_transmitJobs.Count < MaxTransmitJobs && _transmitJobsToInitialize.Count > 0)
        {
            if (active == null)
            {
                active = new Dictionary<IConnection, int>();
                foreach (var running in _transmitJobs.Values)
                {
                    active.TryGetValue(running.Target, out var load);
                    active[running.Target] = load + 1;
                }
            }

            int index = -1;
            int lowest = int.MaxValue;
            for (int i = 0; i < _transmitJobsToInitialize.Count; i++)
            {
                var candidate = _transmitJobsToInitialize[i];
                if (active.TryGetValue(candidate.Target, out var busy)
                    && busy >= MaxTransmitJobsPerPeer)
                    continue;
                if (!_connectionIndexes.TryGetValue(candidate.Target, out var served))
                    served = -1;
                if (served < lowest)
                {
                    index = i;
                    lowest = served;
                }
            }

            if (index < 0)
                break;

            var job = _transmitJobsToInitialize[index];
            _transmitJobsToInitialize.RemoveAt(index);

            _connectionIndexes.Remove(job.Target);
            _connectionIndexes[job.Target] = _globalConnectionIndex++;
            if (_globalConnectionIndex == int.MaxValue)
                _globalConnectionIndex = 0;

            job.LastHeard = Now;
            job.Starts++;
            var key = new JobID(job.Target, job.ID);
            _transmitJobs[key] = job;
            PeerFor(job.Target).Jobs.Add(job);
            active.TryGetValue(job.Target, out var had);
            active[job.Target] = had + 1;
            PrepareFile(job);
        }
    }

    private void PrepareFile(FileTransmitJob job)
    {
        job.Loading = true;
        job.Ready = false;
        Disk.Run(() =>
        {
            bool loaded = job.Load(Engine.Current?.LocalDB);
            lock (_lock)
            {
                job.Loading = false;
                var key = new JobID(job.Target, job.ID);
                if (_disposed || !_transmitJobs.TryGetValue(key, out var current) || current != job)
                {
                    job.Dispose();
                    return;
                }
                if (!loaded)
                {
                    DropTransmit(key);
                    SendNotAvailable(UriKey(job.AssetUri), job.Target);
                    RefreshJobs();
                    return;
                }
                job.LastHeard = job.StartSentAt = Now;
                Session.Sync.EnqueueForTransmission(job.Initialize());
                if (job.AllSent)
                    DropTransmit(key);
            }
        });
    }

    // Leaving a session answers everything still asked of it. Clearing the asks without a word left every
    // gather waiting on them waiting for good: an asset half way over when its world closed never failed,
    // so nothing that wanted it ever fell back. The answers go out after the lock, since a waiter may
    // turn straight round and ask another session. -xlinka
    public void Dispose()
    {
        lock (_live)
            _live.Remove(this);
        List<(Uri Uri, List<GatherCallbacks> Waiters)> unanswered;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _incomingSignal.Set();
            unanswered = _fetches.Select(kv => (new Uri(kv.Key), kv.Value.Waiters.ToList())).ToList();
            foreach (var job in _transmitJobs.Values)
                if (!job.Busy)
                    job.Dispose();
            foreach (var job in _transmitJobsToInitialize)
                job.Dispose();
            foreach (var job in _receiveJobs.Values)
            {
                job.PendingChunks.Clear();
                job.PendingBytes = 0;
                if (!job.Writing)
                    job.Dispose();
            }
            _transmitJobs.Clear();
            _receiveJobs.Clear();
            _fetches.Clear();
            _pendingRelays.Clear();
            _relaysStarted.Clear();
            _relayBacklog.Clear();
            _transmitJobsToInitialize.Clear();
            _connectionIndexes.Clear();
            _peers.Clear();
            _heardFrom.Clear();
        }

        foreach (var (uri, waiters) in unanswered)
        {
            foreach (var waiter in waiters)
            {
                try
                {
                    waiter.Gathered(uri, null!);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Warn($"AssetTransferer: answering a waiter for {uri.OriginalString} on leaving threw: {ex.Message}");
                }
            }
        }
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Persistence;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

// Where the sync stands, for the inventory's status line.
public readonly struct InventorySyncStatus
{
    public readonly int Pending;
    public readonly int Syncing;
    public readonly int Failed;
    // Of the blob going up now, -1 when none is.
    public readonly int Percent;
    // Textures of the save going up now whose variants are still to go.
    public readonly int Variants;
    public readonly bool OutOfSpace;
    public readonly string? LastError;
    // Saves are held until the account's email is verified.
    public readonly bool WaitingForEmail;

    public InventorySyncStatus(int pending, int syncing, int failed, int percent, int variants, bool outOfSpace, string? lastError,
        bool waitingForEmail = false)
    {
        WaitingForEmail = waitingForEmail;
        Pending = pending;
        Syncing = syncing;
        Failed = failed;
        Percent = percent;
        Variants = variants;
        OutOfSpace = outOfSpace;
        LastError = lastError;
    }

    // Worth drawing in the warning color: storage is full, or saves are parked and nothing else is moving.
    public bool IsProblem => OutOfSpace || (WaitingForEmail && Pending > 0) || (Variants == 0 && Pending + Syncing == 0 && Failed > 0);
}

// Saves go up from here. Inventory keeps every save on this machine first, as a file and a row of the
// local database, and this queue takes the signed in account's rows up one at a time, oldest first: the
// files the save names, the variants of its textures, the save itself and its picture, then the item.
// A failure the service may get over (no connection, a busy or broken server) is tried again later,
// waiting longer each time; one it will not get over (a refusal, a full storage) parks the row until a
// retry, a sign in or the next run. Rows outlive a sign out and a restart, and only the account a row
// was saved under ever sends it. -xlinka
public static class InventorySync
{
    public const string Table = "inventory";
    private const string PictureExtension = ".jpg";
    private const string ClearPictureExtension = ".png";

    // A world's picture is a JPEG of the view; an item's is a PNG with a clear ground. Each is filed and sent
    // as what it is, so nothing that trusts the name decodes it wrong.
    private static string PictureExtensionOf(byte[] picture)
        => picture.Length >= 8 && picture[0] == 0x89 && picture[1] == 0x50 && picture[2] == 0x4E && picture[3] == 0x47
            ? ClearPictureExtension
            : PictureExtension;

    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
    };
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, InventoryRow> Rows = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Wake = new(0, 1);
    // Held from a save's files being written until its row names them, and while a save that went up drops
    // its copies, so one never takes a file the other is about to name.
    private static readonly SemaphoreSlim Keeping = new(1, 1);

    private static IInventoryService? _service;
    // The database the rows were read from.
    private static LocalDB? _db;
    // The account the running queue sends for, once known.
    private static string? _account;
    private static CancellationTokenSource? _run;
    private static string? _current;
    private static CancellationTokenSource? _currentHalt;
    private static TaskCompletionSource? _currentDone;
    private static int _percent = -1;
    private static int _variants;
    private static long _revision;
    private static long _storage;
    // The out of space flag as last raised, 1 when set.
    private static int _full;
    // The room the service last gave the account, and whose.
    private static string? _roomOf;
    private static long _room;
    // What the last save that went up without all of its parts has to tell the person, until a screen shows it.
    private static string? _notice;
    // The inventory service refused a save because the account's email is not verified: nothing is tried until a read
    // of the account says it is, so a queue of saves never knocks on the same closed door one after another.
    private static bool _emailHeld;
    // Parts the service lists as missing that this run already tried to put back, each once.
    private static readonly HashSet<string> Repairing = new(StringComparer.Ordinal);

    // Anything changed: a save came or went, one moved on, a byte went up. Raised off the main thread.
    public static event Action? Changed;

    public static IInventoryService? Service => _service;
    public static bool SignedIn => _service?.SignedIn == true;

    public static string? Account
    {
        get
        {
            lock (Gate)
                return _account;
        }
    }

    // Goes up when a save comes, goes, is renamed or moved, or the account changes: whenever a listing
    // read before is out of date. Progress and the queue's own steps leave it alone.
    public static long Revision => Interlocked.Read(ref _revision);

    // Goes up when the account's storage on the service may read differently: a save finished going up or
    // was parked, a save or an item was deleted, or the out of space flag came or went.
    public static long StorageRevision => Interlocked.Read(ref _storage);

    public static InventorySyncStatus Status
    {
        get
        {
            lock (Gate)
            {
                int pending = 0, syncing = 0, failed = 0;
                bool full = false;
                InventoryRow? lastFailed = null;
                if (_account != null)
                {
                    foreach (var row in Rows.Values)
                    {
                        if (row.Account != _account)
                            continue;
                        // A parked save keeps the flag while it tries again, until one gets through.
                        full |= row.OutOfSpace && row.State != InventoryRowState.Synced;
                        switch (row.State)
                        {
                            case InventoryRowState.Pending:
                                pending++;
                                break;
                            case InventoryRowState.Syncing:
                                syncing++;
                                break;
                            case InventoryRowState.Failed:
                                failed++;
                                if (lastFailed == null || row.SavedUtc > lastFailed.SavedUtc)
                                    lastFailed = row;
                                break;
                        }
                    }
                }
                return new InventorySyncStatus(pending, syncing, failed, syncing > 0 ? _percent : -1, _variants, full, lastFailed?.LastError,
                    _emailHeld);
            }
        }
    }

    // The status line, worded the way the inventory screen shows it.
    public static string Describe(in InventorySyncStatus status)
    {
        if (status.WaitingForEmail && status.Pending + status.Syncing > 0)
            return AccountEmailLocale.SyncWaiting.Resolve();
        if (status.OutOfSpace)
            return "Out of space";
        if (status.Variants > 0)
            return $"Uploading variants ({status.Variants})";
        int waiting = status.Pending + status.Syncing;
        if (waiting > 0)
        {
            var line = waiting == 1 ? "Syncing 1 item" : $"Syncing {waiting} items";
            return status.Percent >= 0 ? $"{line} ({status.Percent}%)" : line;
        }
        if (status.Failed > 0)
            return $"Sync error: {status.LastError ?? "a save did not go up"}";
        return "All synced";
    }

    // The note a save that went up without all of its parts left, once: whoever shows it takes it.
    public static string? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    // Parts one of the account's own items lists that the service no longer has, put back from this machine in
    // the background when it has a copy, the asset database's or the download cache's. No save is needed: the
    // item already lists them and only the bytes are gone. Each hash is tried once a run, so a listing that
    // keeps naming one never loops; a listing read after any came back no longer shows the item broken. -xlinka
    internal static void Repair(IReadOnlyList<string> parts)
    {
        var service = _service;
        var db = Engine.Current?.LocalDB;
        if (service == null || !service.SignedIn || db == null || parts.Count == 0 || AccountEmail.SavesHeld)
            return;
        var fresh = new List<string>(parts.Count);
        lock (Gate)
        {
            foreach (var hash in parts)
            {
                if (Repairing.Add(hash))
                    fresh.Add(hash);
            }
        }
        if (fresh.Count == 0)
            return;
        var cache = Engine.Current?.ContentCache;
        _ = Task.Run(async () =>
        {
            int back = 0;
            foreach (var hash in fresh)
            {
                try
                {
                    var (bytes, extension, _) = await CloudAssetPacker.LocalCopyAsync(db, cache, hash).ConfigureAwait(false);
                    if (bytes == null)
                        continue;
                    var answer = await service.UploadAsync(hash, bytes, extension, null, CancellationToken.None).ConfigureAwait(false);
                    if (answer.Success)
                        back++;
                    else
                        LumoraLogger.Warn($"Inventory: a missing part ({hash}) did not go back up: {answer.Message ?? ((int)answer.Status).ToString()}");
                }
                catch (Exception ex)
                {
                    LumoraLogger.Warn($"Inventory: a missing part ({hash}) could not be put back: {ex.Message}");
                }
            }
            if (back == 0)
                return;
            LumoraLogger.Log($"Inventory: put back {back} missing part(s) from this machine.");
            NoteStorage();
            Bump();
        });
    }

    public static void Attach(LumoraClient client) => Use(new LumoraInventoryService(client));

    // Which service the queue talks to: the real one from start up, something standing in for it in a
    // check, nothing on the way out. Whatever ran stops, and the rows are read from the database again.
    public static void Use(IInventoryService? service)
    {
        IInventoryService? old;
        CancellationTokenSource? stop;
        lock (Gate)
        {
            old = _service;
            _service = service;
            stop = _run;
            _run = null;
            Rows.Clear();
            _db = null;
            _account = null;
        }
        stop?.Cancel();
        if (old != null)
        {
            old.AccountChanged -= OnAccountChanged;
            old.BackOnline -= OnBackOnline;
        }
        if (service != null)
        {
            service.AccountChanged += OnAccountChanged;
            service.BackOnline += OnBackOnline;
        }
        OnAccountChanged();
    }

    // Every save kept here, of any account, as it stands now.
    public static List<InventoryRow> Snapshot()
    {
        lock (Gate)
        {
            var rows = new List<InventoryRow>(Rows.Count);
            foreach (var row in Rows.Values)
                rows.Add(row.Clone());
            return rows;
        }
    }

    // A parked save, or one waiting out a failure, sent again now.
    public static void Retry(string id) => RetryWhere(row => row.Id == id);

    // The account's email is verified now, or the account changed: what waited on it goes up.
    internal static void EmailCleared()
    {
        bool was;
        lock (Gate)
        {
            was = _emailHeld;
            _emailHeld = false;
        }
        if (!was)
            return;
        LumoraLogger.Log("Inventory: the email is verified; the saves that waited go up now.");
        Signal();
        RaiseChanged();
    }

    public static void RetryAll() => RetryWhere(_ => true);

    // Storage was freed, so what was parked for want of it is worth another try.
    internal static void SpaceFreed()
    {
        NoteStorage();
        RetryWhere(row => row.OutOfSpace);
    }

    // What the service last said the account has room for. More room than it said before means space was
    // freed in a way this machine never saw (the service clearing out uploads nothing lists, a delete on
    // another computer, a bigger allowance), so what was parked for want of it tries again. -xlinka
    internal static void QuotaSeen(string? account, long usedBytes, long limitBytes)
    {
        bool more;
        lock (Gate)
        {
            if (account == null || account != _account)
                return;
            long room = limitBytes - usedBytes;
            more = _roomOf == account && room > _room;
            _roomOf = account;
            _room = room;
        }
        if (more)
            RetryWhere(row => row.OutOfSpace);
    }

    private static void RetryWhere(Func<InventoryRow, bool> which)
    {
        var again = new List<InventoryRow>();
        lock (Gate)
        {
            foreach (var row in Rows.Values)
            {
                if (row.Account != _account || row.Held || row.State == InventoryRowState.Synced
                    || row.State == InventoryRowState.Syncing || !which(row))
                    continue;
                Requeue(row);
                again.Add(row);
            }
        }
        if (again.Count == 0)
            return;
        foreach (var row in again)
            _ = PersistAsync(row);
        Signal();
        RaiseChanged();
    }

    // SAVING

    // A save, kept on this machine and nowhere else yet: the tree becomes a file of the local database, a
    // row says what it is, and the queue is woken. Nothing here waits on the network, so a save works the
    // same with no connection at all. Off the caller's thread, which is the world's. -xlinka
    internal static Task<InventoryResult> SaveAsync(DataTreeDictionary tree, string extension, string name, string kind, InventoryPlace place,
        byte[]? picture, string? mode, string? itemId = null)
        => Task.Run(() => SaveHereAsync(tree, extension, name, kind, place, picture, mode, itemId));

    private static async Task<InventoryResult> SaveHereAsync(DataTreeDictionary tree, string extension, string name, string kind, InventoryPlace place,
        byte[]? picture, string? mode, string? itemId)
    {
        var service = _service;
        if (service == null || !service.SignedIn)
            return Inventory.NotSignedIn();
        var account = Account ?? service.Account;
        if (account == null)
            return InventoryResult.Failure("Still signing in. Try again in a moment.");
        // Refused here rather than kept on this machine: a save the service will not take until the email is verified
        // would only pile up. Offline, or before the first read lands, it is kept as ever and waits in the queue.
        await AccountEmail.EnsureKnownAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        if (AccountEmail.SavesHeld && (AccountEmail.Account == null || AccountEmail.Account == account))
            return new InventoryResult(false, AccountEmailLocale.SaveRefused.Resolve()) { Code = AccountEmail.NotVerifiedCode };
        var db = Engine.Current?.LocalDB;
        if (db == null || await Task.WhenAny(db.Ready, Task.Delay(ReadyWait)).ConfigureAwait(false) != db.Ready)
            return InventoryResult.Failure("The local cache isn't up yet.");
        Load(db);
        string? owner = string.IsNullOrEmpty(place.OwnerId) || place.OwnerId == account ? null : place.OwnerId;
        string? folderId = place.FolderId;

        // Over an item: a save of it still here is stopped and held, then replaced, so nothing it does after
        // can write its stored row back. It keeps the name and folder the item has now. -xlinka
        InventoryRow? previous = null;
        if (itemId != null)
        {
            previous = await HoldAsync(itemId).ConfigureAwait(false);
            if (previous != null)
                (name, folderId, owner) = (previous.Name, previous.Folder, previous.Owner);
            else
                (name, folderId) = await ListedAsAsync(service, itemId, name, folderId, owner).ConfigureAwait(false);
        }

        List<string> missing;
        long localBytes;
        byte[] bytes;
        try
        {
            (missing, localBytes) = await CloudAssetPacker.CheckFilesAsync(tree, db).ConfigureAwait(false);
            bytes = DataTreeConverter.SaveToBytes(tree);
        }
        catch (Exception ex)
        {
            if (previous != null)
                Release(previous);
            return InventoryResult.Failure($"Couldn't pack \"{name}\": {ex.Message}");
        }
        InventoryRow row;
        await Keeping.WaitAsync().ConfigureAwait(false);
        try
        {
            var uri = await db.SaveAssetAsync(bytes, extension).ConfigureAwait(false);
            if (uri == null)
            {
                if (previous != null)
                    Release(previous);
                return InventoryResult.Failure($"Couldn't keep \"{name}\" on this machine.");
            }
            string? thumbnail = picture == null ? null : await db.SaveAssetAsync(picture, PictureExtensionOf(picture)).ConfigureAwait(false);

            row = new InventoryRow
            {
                Id = itemId ?? Guid.NewGuid().ToString(),
                Account = account,
                Name = name,
                Kind = kind,
                Folder = string.IsNullOrEmpty(folderId) ? Inventory.Root : folderId!,
                Owner = owner,
                Mode = mode,
                LocalUri = uri,
                ThumbnailUri = thumbnail,
                // The whole save, as the inventory shows it once it has synced: the tree, its picture and the
                // files it names.
                Size = bytes.LongLength + (picture?.LongLength ?? 0) + localBytes,
                SavedUtc = DateTime.UtcNow,
                State = InventoryRowState.Pending,
            };
            if (!await db.PutRowAsync(Table, row.Id, row.ToFields()).ConfigureAwait(false))
            {
                // Told it failed, so a later write of the index must not carry it after all. A save it was to
                // replace is written back as it was.
                if (previous != null)
                {
                    await PersistAsync(previous).ConfigureAwait(false);
                    Release(previous);
                }
                else
                    await db.DeleteRowAsync(Table, row.Id).ConfigureAwait(false);
                return InventoryResult.Failure($"Couldn't keep \"{name}\" on this machine.");
            }
            lock (Gate)
            {
                if (ReferenceEquals(_db, db))
                    Rows[row.Id] = row;
                if (previous != null)
                {
                    previous.Gone = true;
                    previous.Held = false;
                }
            }
        }
        finally
        {
            Keeping.Release();
        }
        if (previous != null)
            await DropCopiesAsync(db, previous).ConfigureAwait(false);
        Bump();
        Signal();
        if (missing.Count > 0)
            LumoraLogger.Warn($"Inventory: \"{name}\" saved without {missing.Count} file(s) missing on this machine: {string.Join("; ", missing)}");
        return InventoryResult.Success(missing.Count == 0 ? $"Saved \"{name}\"{Inventory.SyncingTail}" : Inventory.LeftOut(name, missing), row.Id);
    }

    // The name and folder an item on the service has now, looked for where it was, so a save over it keeps a
    // rename made since. Not found there, or not answered, it goes where it was.
    private static async Task<(string name, string? folderId)> ListedAsAsync(IInventoryService service, string itemId, string name, string? folderId,
        string? owner)
    {
        try
        {
            var listed = await service.ListAsync(string.IsNullOrEmpty(folderId) ? Inventory.Root : folderId!, owner).ConfigureAwait(false);
            if (!listed.Failed && listed.Data != null)
            {
                foreach (var item in listed.Data.Items)
                {
                    if (string.Equals(item.Id, itemId, StringComparison.Ordinal))
                        return (item.Name, item.FolderId ?? Inventory.Root);
                }
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Inventory: couldn't look up \"{name}\" before saving over it: {ex.Message}");
        }
        return (name, folderId);
    }

    // LISTING

    // Lays the signed in account's saves that are not on the service yet over a listing of one folder.
    // A save the service lists already shows once: as the service has it once it is done going up, as it
    // is here while it still has changes to send. -xlinka
    internal static void MergeInto(InventoryListing listing, string folderId, string? ownerId = null)
        => Merge(listing, row => row.GoesTo(ownerId) && string.Equals(row.Folder, folderId, StringComparison.Ordinal));

    internal static void MergeSearch(InventoryListing listing, string query, string? ownerId = null)
        => Merge(listing, row => row.GoesTo(ownerId) && row.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    // A listing read before a share or unshare is out of date: the badges it drew changed.
    internal static void Touch() => Bump();

    private static void Merge(InventoryListing listing, Func<InventoryRow, bool> wanted)
    {
        List<InventoryRow>? done = null;
        lock (Gate)
        {
            if (_account == null)
                return;
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < listing.Entries.Count; i++)
                at[listing.Entries[i].Id] = i;
            foreach (var row in Rows.Values)
            {
                if (row.Account != _account || !wanted(row))
                    continue;
                bool listed = at.TryGetValue(row.Id, out int index);
                if (row.State == InventoryRowState.Synced && listed)
                {
                    // The service has it, and from here on the service's listing is the one that counts.
                    (done ??= new List<InventoryRow>()).Add(row);
                    continue;
                }
                if (listed)
                    listing.Entries[index] = row.ToEntry();
                else
                    listing.Entries.Add(row.ToEntry());
            }
        }
        if (done != null)
        {
            foreach (var row in done)
                _ = ForgetAsync(row);
        }
    }

    // A save of the signed in account kept on this machine, by its id.
    internal static InventoryRow? Find(string id)
    {
        lock (Gate)
            return Rows.TryGetValue(id, out var row) && row.Account == _account ? row.Clone() : null;
    }

    // EDITS
    //
    // A save that is not on the service yet is renamed, moved or deleted here, and the next try sends it as
    // it is then. One that is going up is stopped first and waited for; if it went up meanwhile, the edit
    // is the service's, and these answer null so the caller asks the service instead. -xlinka

    internal static async Task<InventoryResult?> RenameAsync(string id, string name)
    {
        var row = await HoldAsync(id).ConfigureAwait(false);
        if (row == null)
            return null;
        lock (Gate)
        {
            row.Name = name;
            Requeue(row);
        }
        await PersistAsync(row).ConfigureAwait(false);
        Release(row);
        Bump();
        return InventoryResult.Success($"Renamed to \"{name}\".");
    }

    internal static async Task<InventoryResult?> MoveAsync(string id, string folderId, string folderName)
    {
        var row = await HoldAsync(id).ConfigureAwait(false);
        if (row == null)
            return null;
        string name;
        lock (Gate)
        {
            row.Folder = string.IsNullOrEmpty(folderId) ? Inventory.Root : folderId;
            name = row.Name;
            Requeue(row);
        }
        await PersistAsync(row).ConfigureAwait(false);
        Release(row);
        Bump();
        return InventoryResult.Success($"Moved \"{name}\" to {folderName}.");
    }

    internal static async Task<InventoryResult?> DeleteAsync(string id)
    {
        var row = await HoldAsync(id).ConfigureAwait(false);
        if (row == null)
            return null;
        IInventoryService? service;
        lock (Gate)
            service = _service;
        await ForgetAsync(row).ConfigureAwait(false);
        // It may have reached the service with the answer lost on the way back, and that copy goes too.
        if (row.Sent && service != null)
            _ = DeleteSentAsync(service, id);
        NoteStorage();
        Bump();
        return InventoryResult.Success($"Deleted \"{row.Name}\".");
    }

    // A copy the service did hold took up room, which is free again once it is gone.
    private static async Task DeleteSentAsync(IInventoryService service, string id)
    {
        var answer = await service.DeleteItemAsync(id).ConfigureAwait(false);
        if (answer.Success)
            SpaceFreed();
    }

    // The folder went on the service with everything in it, so the saves here that were going into it go
    // with it rather than turn up at the top later.
    internal static async Task DropFolderAsync(string folderId)
    {
        var ids = new List<string>();
        lock (Gate)
        {
            foreach (var row in Rows.Values)
            {
                if (row.Account == _account && row.Folder == folderId)
                    ids.Add(row.Id);
            }
        }
        foreach (var id in ids)
            await DeleteAsync(id).ConfigureAwait(false);
    }

    // The folder went into another on the service (a merge), so the saves here that were going into it go there
    // instead of being refused for a folder that is gone.
    internal static async Task RefolderAsync(string folderId, string intoId)
    {
        var ids = new List<string>();
        lock (Gate)
        {
            foreach (var row in Rows.Values)
            {
                if (row.Account == _account && row.Folder == folderId)
                    ids.Add(row.Id);
            }
        }
        foreach (var id in ids)
            await MoveAsync(id, intoId, string.Empty).ConfigureAwait(false);
    }

    // Takes a save out of the queue's hands for an edit: an upload of it under way is stopped and waited
    // for, and the queue leaves the row alone until Release. Null when there is no such save here, or it
    // is on the service by the time the upload stopped. -xlinka
    private static async Task<InventoryRow?> HoldAsync(string id)
    {
        InventoryRow? row;
        CancellationTokenSource? halt = null;
        Task? done = null;
        lock (Gate)
        {
            if (!Rows.TryGetValue(id, out row) || row.Account != _account || row.Held)
                return null;
            row.Held = true;
            if (_current == id)
            {
                halt = _currentHalt;
                done = _currentDone?.Task;
            }
        }
        if (halt != null)
        {
            try
            {
                halt.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        if (done != null)
            await done.ConfigureAwait(false);
        bool synced;
        lock (Gate)
            synced = row.State == InventoryRowState.Synced;
        if (!synced)
            return row;
        await ForgetAsync(row).ConfigureAwait(false);
        return null;
    }

    private static void Release(InventoryRow row)
    {
        lock (Gate)
            row.Held = false;
        Signal();
    }

    // THE QUEUE

    private static void OnAccountChanged()
    {
        var service = _service;
        CancellationTokenSource? stop;
        CancellationTokenSource? run = null;
        lock (Gate)
        {
            stop = _run;
            _account = null;
            _notice = null;
            _emailHeld = false;
            _run = null;
            if (service != null && service.SignedIn)
                _run = run = new CancellationTokenSource();
        }
        stop?.Cancel();
        Bump();
        if (run != null)
        {
            var token = run.Token;
            _ = Task.Run(() => RunAsync(service!, token));
        }
    }

    // The connection is back: whatever was waiting out a failure goes now.
    private static void OnBackOnline()
    {
        bool any = false;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            foreach (var row in Rows.Values)
            {
                if (row.Account != _account || row.State != InventoryRowState.Pending || row.NextAttemptUtc <= now)
                    continue;
                row.NextAttemptUtc = now;
                any = true;
            }
        }
        if (any)
            Signal();
    }

    private static async Task RunAsync(IInventoryService service, CancellationToken stop)
    {
        try
        {
            string? account = service.Account;
            for (int tries = 1; account == null; tries++)
            {
                account = await service.ResolveAccountAsync(stop).ConfigureAwait(false);
                if (account == null)
                    await Task.Delay(BackoffFor(tries), stop).ConfigureAwait(false);
            }
            var db = await DatabaseAsync(stop).ConfigureAwait(false);
            lock (Gate)
            {
                if (stop.IsCancellationRequested)
                    return;
                _account = account;
                // Everything of this account that is not up yet goes back in the queue, parked rows too. One
                // still going up for the queue this one replaces rejoins it when that try ends.
                foreach (var row in Rows.Values)
                {
                    if (row.Account == account && row.State != InventoryRowState.Synced && row.State != InventoryRowState.Syncing && !row.Held)
                        Requeue(row);
                }
            }
            Bump();

            while (!stop.IsCancellationRequested)
            {
                InventoryRow? next;
                TimeSpan wait;
                lock (Gate)
                    next = Head(account, out wait);
                if (next == null)
                {
                    await Wake.WaitAsync(wait, stop).ConfigureAwait(false);
                    continue;
                }
                await AttemptAsync(service, db, next, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Inventory sync: the queue stopped: {ex.Message}");
        }
    }

    // The database in use and its rows. The engine opens it after the queue may already be running.
    private static async Task<LocalDB> DatabaseAsync(CancellationToken stop)
    {
        while (true)
        {
            var db = Engine.Current?.LocalDB;
            if (db != null)
            {
                await db.Ready.WaitAsync(stop).ConfigureAwait(false);
                Load(db);
                return db;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
        }
    }

    // Reads the rows once per database. A synced row has done its job by the time they are read again, the
    // service lists the item, so it is dropped here with whatever copies of it the last run did not get to;
    // one that was going up when the last run ended waits its turn again. -xlinka
    private static void Load(LocalDB db)
    {
        List<InventoryRow>? done = null;
        lock (Gate)
        {
            if (ReferenceEquals(_db, db))
                return;
            _db = db;
            Rows.Clear();
            foreach (var (id, fields) in db.GetRows(Table))
            {
                var row = InventoryRow.FromFields(id, fields);
                if (row == null)
                    continue;
                if (row.State == InventoryRowState.Synced)
                {
                    (done ??= new List<InventoryRow>()).Add(row);
                    continue;
                }
                if (row.State == InventoryRowState.Syncing)
                    row.State = InventoryRowState.Pending;
                Rows[id] = row;
            }
        }
        if (done != null)
        {
            foreach (var row in done)
            {
                _ = db.DeleteRowAsync(Table, row.Id);
                _ = DropCopiesAsync(db, row);
            }
        }
    }

    // The oldest save of the account still to go up, or how long until it may be tried again. Strictly
    // oldest first: while that one waits out a failure the rest wait behind it, since a try at any of them
    // would most likely fail the same way. -xlinka
    private static InventoryRow? Head(string account, out TimeSpan wait)
    {
        wait = Timeout.InfiniteTimeSpan;
        if (_emailHeld)
            return null;
        InventoryRow? head = null;
        foreach (var row in Rows.Values)
        {
            if (row.Account != account || row.State != InventoryRowState.Pending || row.Held)
                continue;
            if (head == null || row.SavedUtc < head.SavedUtc
                || (row.SavedUtc == head.SavedUtc && string.CompareOrdinal(row.Id, head.Id) < 0))
                head = row;
        }
        if (head == null)
            return null;
        var now = DateTime.UtcNow;
        if (head.NextAttemptUtc <= now)
            return head;
        wait = head.NextAttemptUtc - now;
        return null;
    }

    private static async Task AttemptAsync(IInventoryService service, LocalDB db, InventoryRow row, CancellationToken stop)
    {
        using var halt = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            row.State = InventoryRowState.Syncing;
            _current = row.Id;
            _currentHalt = halt;
            _currentDone = done;
            _percent = -1;
            _variants = 0;
        }
        RaiseChanged();
        try
        {
            Outcome outcome;
            try
            {
                outcome = await UploadAsync(service, db, row, halt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (halt.IsCancellationRequested)
            {
                outcome = Outcome.Cancelled;
            }
            catch (Exception ex)
            {
                outcome = Outcome.Retry(ex.Message);
            }
            // A call that was called off answers rather than throws; either way it was not a failure.
            if (halt.IsCancellationRequested && outcome.Kind != OutcomeKind.Done)
                outcome = Outcome.Cancelled;
            Apply(row, outcome);
            if (outcome.Kind == OutcomeKind.Held)
                AccountEmail.Refused(saves: true, joins: false);
            await PersistAsync(row).ConfigureAwait(false);
            if (outcome.Kind == OutcomeKind.Done)
                await DropCopiesAsync(db, row).ConfigureAwait(false);
            if (outcome.Kind is OutcomeKind.Done or OutcomeKind.Refused or OutcomeKind.NoRoom)
                NoteStorage();
        }
        finally
        {
            lock (Gate)
            {
                if (_currentDone == done)
                {
                    _current = null;
                    _currentHalt = null;
                    _currentDone = null;
                    _percent = -1;
                    _variants = 0;
                }
            }
            done.TrySetResult();
            Signal();
            RaiseChanged();
        }
    }

    // One try at one save. Its tree is read back from this machine and packed: every file it names goes up
    // unless the store has it, and the URLs are rewritten to the cloud's. Then the variants of its
    // textures, the packed tree and the picture, and last the item, once, listing all of it. -xlinka
    private static async Task<Outcome> UploadAsync(IInventoryService service, LocalDB db, InventoryRow row, CancellationToken ct)
    {
        var bytes = await db.ReadAssetBytesAsync(row.LocalUri).ConfigureAwait(false);
        if (bytes == null)
            return Outcome.Refused($"The saved copy of \"{row.Name}\" is no longer on this machine.");
        DataTreeDictionary tree;
        try
        {
            tree = DataTreeConverter.LoadFromBytes(bytes) as DataTreeDictionary ?? throw new InvalidDataException("not a saved object");
        }
        catch (Exception ex)
        {
            return Outcome.Refused($"The saved copy of \"{row.Name}\" could not be read: {ex.Message}");
        }

        var sink = new Sink(service);
        var problems = new List<string>();
        var dropped = new List<string>();
        var lost = new List<string>();
        var manifest = await CloudAssetPacker.PackAsync(tree, sink, db, sink.MissingAsync, problems, dropped, ct, lost,
            Engine.Current?.ContentCache).ConfigureAwait(false);
        if (problems.Count > 0)
            return sink.Verdict(Inventory.NotUploaded(row.Name, problems));
        if (dropped.Count > 0)
            LumoraLogger.Warn($"Inventory: \"{row.Name}\" goes up without {dropped.Count} file(s) missing on this machine: {string.Join("; ", dropped)}");
        if (lost.Count > 0)
            LumoraLogger.Warn($"Inventory: \"{row.Name}\" goes up naming {lost.Count} part(s) missing on the server and on this machine: {string.Join("; ", lost)}");

        var entries = new List<AssetManifestEntry>(manifest.Count);
        foreach (var entry in manifest)
            entries.Add(Listed(entry, entry.Type));
        entries.AddRange(await VariantsAsync(service, db, manifest, ct).ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();

        var packed = DataTreeConverter.SaveToBytes(tree);
        var (hash, error) = await sink.StoreAsync(packed, Inventory.ExtensionFor(row.Kind), ct).ConfigureAwait(false);
        if (hash == null)
            return sink.Verdict(error ?? "The save did not go up.");
        string? thumbnail = null;
        if (!string.IsNullOrEmpty(row.ThumbnailUri))
        {
            var picture = await db.ReadAssetBytesAsync(row.ThumbnailUri!).ConfigureAwait(false);
            if (picture != null)
            {
                var (shot, shotError) = await sink.StoreAsync(picture, PictureExtensionOf(picture), ct).ConfigureAwait(false);
                if (shot == null)
                    return sink.Verdict(shotError ?? "Its picture did not go up.");
                thumbnail = shot;
            }
        }
        ct.ThrowIfCancellationRequested();

        // From the item on nothing is cut off halfway: an edit that comes in now waits for the answer, so the
        // service never holds an item this machine believes it does not.
        var put = await PutAsync(service, row, packed.LongLength, hash, thumbnail, entries).ConfigureAwait(false);
        if (!put.Success && put.Error == AccountEmail.NotVerifiedCode)
            return Outcome.Held(put.Message ?? AccountEmailLocale.SaveRefused.Resolve());
        if (!put.Success)
            return Verdict(put, "The item was refused.");

        Inventory.Keep(hash, packed, row.IsWorld);
        return Outcome.Done(hash, lost.Count == 0 ? null : InventoryPartsLocale.Lost(row.Name, lost.Count).Resolve());
    }

    private static async Task<ApiResponse> PutAsync(IInventoryService service, InventoryRow row, long size, string hash, string? thumbnail,
        List<AssetManifestEntry> entries)
    {
        lock (Gate)
            row.Sent = true;
        var answer = await service.PutItemAsync(row.Id, row.Name, row.Kind, row.Folder, hash, thumbnail, size, row.Mode, entries,
            row.Owner, CancellationToken.None).ConfigureAwait(false);
        // A service that does not know pictures yet keeps one as an object.
        if (answer.Status == HttpStatusCode.BadRequest && answer.Error == "bad-kind" && row.Kind == Inventory.ImageKind)
        {
            lock (Gate)
                row.Kind = Inventory.ObjectKind;
            Bump();
            answer = await service.PutItemAsync(row.Id, row.Name, row.Kind, row.Folder, hash, thumbnail, size, row.Mode, entries,
                row.Owner, CancellationToken.None).ConfigureAwait(false);
        }
        if (answer.Error == AccountEmail.NotVerifiedCode)
            return answer;
        // Saved into someone else's folder that stopped letting this account in before the save got there (unshared,
        // taken down to view only, out of the group, no storage set for it there): it lands at the top of the
        // account's own inventory instead of being lost. -xlinka
        if (answer.Status == HttpStatusCode.Forbidden && !string.IsNullOrEmpty(row.Owner))
        {
            LumoraLogger.Warn($"Inventory: \"{row.Name}\" can no longer go where it was saved, so it goes in at the top of your inventory.");
            lock (Gate)
            {
                row.Owner = null;
                row.Folder = Inventory.Root;
            }
            Bump();
            return await service.PutItemAsync(row.Id, row.Name, row.Kind, Inventory.Root, hash, thumbnail, size, row.Mode, entries,
                null, CancellationToken.None).ConfigureAwait(false);
        }
        if (answer.Status != HttpStatusCode.NotFound || (answer.Error != null && answer.Error != "folder") || row.Folder == Inventory.Root)
            return answer;
        // The folder went away, deleted on another computer say: the save lands at the top rather than nowhere.
        LumoraLogger.Warn($"Inventory: the folder \"{row.Name}\" was saved into is gone, so it goes in at the top.");
        lock (Gate)
            row.Folder = Inventory.Root;
        Bump();
        return await service.PutItemAsync(row.Id, row.Name, row.Kind, Inventory.Root, hash, thumbnail, size, row.Mode, entries,
            row.Owner, CancellationToken.None).ConfigureAwait(false);
    }

    // The variants of every texture the save took up, made on this machine and stored beside it, each once
    // its texture is up. A texture the service has variants of already, a build that cannot make them yet,
    // a texture someone else put up first (only its first uploader may register them), or a variant that
    // will not go up leaves the save with fewer, which costs nothing that matters: a machine that loads the
    // save makes its own. So nothing here decides how the try ends, and it gets a sink of its own whose
    // refusals count for nothing.
    //
    // Several textures go at once: a texture's turn is mostly round trips waiting on the service, one after
    // another, and a ladder one still has to make waits for the two generation workers whatever this does.
    // -xlinka
    private static async Task<List<AssetManifestEntry>> VariantsAsync(IInventoryService service, LocalDB db, List<CloudAssetEntry> manifest,
        CancellationToken ct)
    {
        var sink = new Sink(service);
        var made = new List<AssetManifestEntry>();
        var textures = new List<CloudAssetEntry>();
        foreach (var entry in manifest)
        {
            if (entry.Type == "texture" && !string.IsNullOrEmpty(entry.LocalUri))
                textures.Add(entry);
        }
        var sent = new IReadOnlyList<CloudAssetEntry>?[textures.Count];
        int left = textures.Count;
        SetVariants(left);
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = CloudAssetPacker.UploadWidth, CancellationToken = ct };
            await Parallel.ForEachAsync(System.Linq.Enumerable.Range(0, textures.Count), options, async (i, token) =>
            {
                try
                {
                    sent[i] = await CloudVariantUploader.UploadAsync(db, textures[i].LocalUri, textures[i].Hash, sink, token).ConfigureAwait(false);
                }
                catch (NotImplementedException)
                {
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LumoraLogger.Warn($"Inventory: the variants of \"{textures[i].Name}\" did not go up: {ex.Message}");
                }
                SetVariants(Interlocked.Decrement(ref left));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            SetVariants(0);
        }
        foreach (var entries in sent)
        {
            if (entries == null)
                continue;
            foreach (var entry in entries)
                made.Add(Listed(entry, "variant"));
        }
        return made;
    }

    private static AssetManifestEntry Listed(CloudAssetEntry entry, string type) => new()
    {
        Hash = entry.Hash,
        Name = entry.Name,
        Extension = entry.Extension,
        Type = type,
        SizeBytes = entry.SizeBytes,
    };

    private static void Apply(InventoryRow row, Outcome outcome)
    {
        string name;
        int attempts;
        bool firstHold = false;
        lock (Gate)
        {
            name = row.Name;
            switch (outcome.Kind)
            {
                case OutcomeKind.Done:
                    if (row.OutOfSpace)
                    {
                        // A save parked for want of space got through, so the ones sent back with it are no
                        // longer known to be out of it.
                        foreach (var other in Rows.Values)
                        {
                            if (other.Account == row.Account && other.State != InventoryRowState.Failed)
                                other.OutOfSpace = false;
                        }
                    }
                    row.State = InventoryRowState.Synced;
                    row.CloudHash = outcome.Hash;
                    row.Attempts = 0;
                    row.LastError = null;
                    row.OutOfSpace = false;
                    row.NextAttemptUtc = DateTime.MinValue;
                    if (outcome.Message != null)
                        _notice = outcome.Message;
                    break;
                case OutcomeKind.Cancelled:
                    row.State = InventoryRowState.Pending;
                    break;
                case OutcomeKind.Held:
                    row.State = InventoryRowState.Pending;
                    row.LastError = outcome.Message;
                    row.NextAttemptUtc = DateTime.MinValue;
                    firstHold = !_emailHeld;
                    _emailHeld = true;
                    break;
                case OutcomeKind.Retry:
                    row.State = InventoryRowState.Pending;
                    row.Attempts++;
                    row.LastError = outcome.Message;
                    row.NextAttemptUtc = DateTime.UtcNow + BackoffFor(row.Attempts);
                    break;
                default:
                    row.State = InventoryRowState.Failed;
                    row.Attempts++;
                    row.LastError = outcome.Message;
                    row.OutOfSpace = outcome.Kind == OutcomeKind.NoRoom;
                    break;
            }
            attempts = row.Attempts;
        }
        switch (outcome.Kind)
        {
            case OutcomeKind.Done:
                LumoraLogger.Debug($"Inventory: \"{name}\" is on the service.");
                break;
            case OutcomeKind.Retry:
                LumoraLogger.Warn($"Inventory: \"{name}\" did not go up, trying again in {BackoffFor(attempts).TotalSeconds:0} s: {outcome.Message}");
                break;
            case OutcomeKind.Refused:
            case OutcomeKind.NoRoom:
                LumoraLogger.Warn($"Inventory: \"{name}\" is parked: {outcome.Message}");
                break;
            case OutcomeKind.Held when firstHold:
                LumoraLogger.Log("Inventory: saves wait until this account's email is verified; nothing goes up meanwhile.");
                break;
        }
    }

    private static TimeSpan BackoffFor(int attempts) => Backoff[System.Math.Clamp(attempts - 1, 0, Backoff.Length - 1)];

    // A save parked for want of space keeps the flag while it tries again, so the line goes on saying out
    // of space until one of them gets through.
    private static void Requeue(InventoryRow row)
    {
        row.State = InventoryRowState.Pending;
        row.Attempts = 0;
        row.NextAttemptUtc = DateTime.MinValue;
    }

    // Writes the row as it stands. A row an edit removed meanwhile is removed again after, so a late write
    // can never bring a deleted save back on the next run.
    private static async Task PersistAsync(InventoryRow row)
    {
        LocalDB? db;
        Dictionary<string, string> fields;
        lock (Gate)
        {
            db = _db;
            if (db == null || row.Gone || !Rows.TryGetValue(row.Id, out var live) || !ReferenceEquals(live, row))
                return;
            fields = row.ToFields();
        }
        await db.PutRowAsync(Table, row.Id, fields).ConfigureAwait(false);
        bool gone;
        lock (Gate)
            gone = row.Gone;
        if (gone)
            await db.DeleteRowAsync(Table, row.Id).ConfigureAwait(false);
    }

    private static async Task ForgetAsync(InventoryRow row)
    {
        LocalDB? db;
        bool replaced;
        lock (Gate)
        {
            row.Gone = true;
            row.Held = false;
            bool listed = Rows.TryGetValue(row.Id, out var live);
            if (listed && ReferenceEquals(live, row))
                Rows.Remove(row.Id);
            // A save over the same item holds the stored row now.
            replaced = listed && !ReferenceEquals(live, row);
            db = _db;
        }
        if (db != null && !replaced)
            await db.DeleteRowAsync(Table, row.Id).ConfigureAwait(false);
    }

    // A save that is up is the service's, so its tree and its picture here are only a second copy: they go,
    // unless a save not up yet names the same file. A record filed under another extension than a save
    // gives it came in some other way, an import of the same bytes say, and stays. -xlinka
    private static async Task DropCopiesAsync(LocalDB db, InventoryRow row)
    {
        await Keeping.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Needed(db, row, row.LocalUri) && KeptAs(db, row.LocalUri, Inventory.ExtensionFor(row.Kind)))
                await db.DeleteAssetAsync(row.LocalUri).ConfigureAwait(false);
            if (row.ThumbnailUri is { } picture && !Needed(db, row, picture)
                && (KeptAs(db, picture, PictureExtension) || KeptAs(db, picture, ClearPictureExtension)))
                await db.DeleteAssetAsync(picture).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Inventory: the copy of \"{row.Name}\" kept on this machine could not be dropped: {ex.Message}");
        }
        finally
        {
            Keeping.Release();
        }
    }

    // True while another save still to go up names the file, or when the rows read are another database's.
    private static bool Needed(LocalDB db, InventoryRow done, string uri)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_db, db))
                return true;
            foreach (var other in Rows.Values)
            {
                if (!ReferenceEquals(other, done) && other.State != InventoryRowState.Synced
                    && (other.LocalUri == uri || other.ThumbnailUri == uri))
                    return true;
            }
        }
        return false;
    }

    private static bool KeptAs(LocalDB db, string uri, string extension)
        => string.Equals(db.GetExtension(uri), extension, StringComparison.OrdinalIgnoreCase);

    private static void SetVariants(int count)
    {
        lock (Gate)
        {
            if (_variants == count)
                return;
            _variants = count;
        }
        RaiseChanged();
    }

    private static void SetPercent(int percent)
    {
        lock (Gate)
        {
            if (_percent == percent)
                return;
            _percent = percent;
        }
        RaiseChanged();
    }

    private static void Signal()
    {
        try
        {
            Wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static void Bump()
    {
        Interlocked.Increment(ref _revision);
        RaiseChanged();
    }

    private static void NoteStorage() => Interlocked.Increment(ref _storage);

    private static void RaiseChanged()
    {
        int full = Status.OutOfSpace ? 1 : 0;
        if (Interlocked.Exchange(ref _full, full) != full)
            NoteStorage();
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Inventory sync: a listener failed: {ex.Message}");
        }
    }

    // What a failed call means for the save: try again later (no connection, a busy or broken server, a
    // call that timed out), park it for want of space, or park it because the service said no. An item id
    // another account holds (409) is a no: sending it again gets the same answer.
    private static OutcomeKind Classify(ApiResponse answer)
    {
        int code = (int)answer.Status;
        if (answer.Error == AccountEmail.NotVerifiedCode)
            return OutcomeKind.Held;
        if (code == 413)
            return OutcomeKind.NoRoom;
        if (code == 0 || code == 408 || code == 429 || code >= 500)
            return OutcomeKind.Retry;
        return OutcomeKind.Refused;
    }

    private static Outcome Verdict(ApiResponse answer, string fallback)
        => Outcome.Of(Classify(answer), string.IsNullOrWhiteSpace(answer.Message) ? fallback : answer.Message!);

    private enum OutcomeKind
    {
        Done,
        Cancelled,
        Retry,
        Refused,
        NoRoom,
        Held,
    }

    private readonly struct Outcome
    {
        public readonly OutcomeKind Kind;
        public readonly string? Message;
        public readonly string? Hash;

        private Outcome(OutcomeKind kind, string? message, string? hash)
        {
            Kind = kind;
            Message = message;
            Hash = hash;
        }

        public static Outcome Cancelled => new(OutcomeKind.Cancelled, null, null);
        // A note is for the person: the save went up, but not whole.
        public static Outcome Done(string hash, string? note = null) => new(OutcomeKind.Done, note, hash);
        public static Outcome Retry(string message) => new(OutcomeKind.Retry, message, null);
        public static Outcome Refused(string message) => new(OutcomeKind.Refused, message, null);
        public static Outcome Held(string message) => new(OutcomeKind.Held, message, null);
        public static Outcome Of(OutcomeKind kind, string message) => new(kind, message, null);
    }

    // One try's way up: blobs through the store, each variant registered against the blob it was made
    // from. It keeps the gravest refusal it got, so the try can tell a dropped line from a full storage or
    // a plain no. -xlinka
    private sealed class Sink : ICloudUploadSink
    {
        private readonly IInventoryService _service;
        private readonly object _gate = new();
        private ApiResponse? _failure;
        public long Total;
        public long Done;

        public Sink(IInventoryService service) => _service = service;

        public async Task<IReadOnlyCollection<string>?> MissingAsync(IReadOnlyList<string> hashes, CancellationToken ct)
        {
            var answer = await _service.MissingAsync(hashes, ct).ConfigureAwait(false);
            return answer.Success && answer.Data != null ? answer.Data : null;
        }

        public async Task<(string? Hash, string? Error)> StoreAsync(byte[] bytes, string extension, CancellationToken ct)
        {
            var hash = ContentHash.FromBytes(bytes);
            var answer = await _service.UploadAsync(hash, bytes, extension, new Sent(this, bytes.LongLength), ct).ConfigureAwait(false);
            if (answer.Success)
                return (hash, null);
            Note(answer);
            return (null, answer.Message ?? $"refused ({(int)answer.Status})");
        }

        public async Task<IReadOnlyList<string>?> ListVariantsAsync(string baseHash, CancellationToken ct)
        {
            var answer = await _service.ListVariantsAsync(baseHash, ct).ConfigureAwait(false);
            if (answer.Failed || answer.Data == null)
                return null;
            var ids = new List<string>(answer.Data.Count);
            foreach (var variant in answer.Data)
                ids.Add(variant.VariantId);
            return ids;
        }

        // The service's own status goes back as it came, so the uploader can tell a blob it lacks (404) from
        // a texture it will take no variants of from this account (403).
        public async Task<(bool Ok, int Status, string? Error)> RegisterVariantAsync(string baseHash, string variantId, string resultHash,
            long size, CancellationToken ct)
        {
            var answer = await _service.RegisterVariantAsync(baseHash, variantId, resultHash, size, ct).ConfigureAwait(false);
            if (answer.Success)
                return (true, (int)answer.Status, null);
            Note(answer);
            return (false, (int)answer.Status, answer.Message ?? $"refused ({(int)answer.Status})");
        }

        // What the try comes to when files did not go up: by the gravest answer the store gave, or parked
        // when nothing was even asked, since the trouble is then on this machine.
        public Outcome Verdict(string message)
        {
            ApiResponse? failure;
            lock (_gate)
                failure = _failure;
            return failure == null ? Outcome.Refused(message) : Outcome.Of(Classify(failure), message);
        }

        private void Note(ApiResponse answer)
        {
            lock (_gate)
            {
                if (_failure == null || Grave(answer) > Grave(_failure))
                    _failure = answer;
            }
        }

        private static int Grave(ApiResponse answer) => Classify(answer) switch
        {
            OutcomeKind.Held => 4,
            OutcomeKind.NoRoom => 3,
            OutcomeKind.Refused => 2,
            _ => 1,
        };
    }

    // One blob's part of what its sink has going up. Blobs go several at once, so the percent is of all of
    // them together, never of whichever one reported last.
    private sealed class Sent : IProgress<long>
    {
        private readonly Sink _sink;
        private long _reported;

        public Sent(Sink sink, long total)
        {
            _sink = sink;
            Interlocked.Add(ref sink.Total, total);
        }

        public void Report(long value)
        {
            long step = value - Interlocked.Exchange(ref _reported, value);
            long sent = Interlocked.Add(ref _sink.Done, step);
            long total = Interlocked.Read(ref _sink.Total);
            SetPercent(total <= 0 ? 100 : (int)System.Math.Clamp(sent * 100 / total, 0, 100));
        }
    }
}

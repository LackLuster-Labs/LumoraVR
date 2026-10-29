// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumora.Core;

public enum InventoryRowState
{
    Pending,
    Syncing,
    Synced,
    Failed,
}

// One save kept on this machine until the service has it: what the inventory shows for it meanwhile, and
// how far the sync has got with it. Filed as a row of the local database, so it outlives the run. -xlinka
public sealed class InventoryRow
{
    // Made here, and the item's id on the service too, so sending it twice can only ever make one item.
    public string Id = "";
    // The account it was saved under. Only that account's sign in sends it.
    public string Account = "";
    public string Name = "";
    // Avatar, Object or World.
    public string Kind = "";
    public string Folder = Inventory.Root;
    // Whose inventory it goes into: null for the account's own, else a group's id or the account whose shared
    // folder it was saved into.
    public string? Owner;
    // The mode a world was saved in.
    public string? Mode;
    // The saved tree and the world's picture, as files of the local database.
    public string LocalUri = "";
    public string? ThumbnailUri;
    public long Size;
    public DateTime SavedUtc;
    public InventoryRowState State;
    public int Attempts;
    public string? LastError;
    public DateTime NextAttemptUtc;
    // The packed tree's hash on the service, once it is there.
    public string? CloudHash;
    // Parked because the account's storage is full.
    public bool OutOfSpace;
    // The item went out at least once, so the service may hold it even when no answer came back.
    public bool Sent;

    // An edit has it: the queue leaves it alone until the edit is done. Never written down, and neither is
    // Gone, which marks a row that was deleted while something still held it.
    internal bool Held;
    internal bool Gone;

    public InventoryRow Clone() => (InventoryRow)MemberwiseClone();

    public bool IsWorld => string.Equals(Kind, Inventory.WorldKind, StringComparison.OrdinalIgnoreCase);

    // How the inventory lists it before the service does. The hashes are the local files' own, so a
    // world's picture is the same picture under the same name once it has gone up.
    internal InventoryEntry ToEntry() => new(Id, Name, IsWorld ? InventoryEntryKind.World : InventoryEntryKind.Item, SavedUtc, Size, 0,
        IsWorld ? Mode ?? string.Empty : Kind, KeyOf(LocalUri), ThumbnailUri == null ? null : KeyOf(ThumbnailUri), Folder, null)
    {
        OwnerId = Owner,
    };

    // Whether it goes into the inventory a listing is of; null is the account's own either way.
    internal bool GoesTo(string? ownerId)
        => string.IsNullOrEmpty(Owner) ? string.IsNullOrEmpty(ownerId) || ownerId == Account : string.Equals(Owner, ownerId, StringComparison.Ordinal);

    private static string KeyOf(string localUri)
    {
        int slash = localUri.StartsWith("local://", StringComparison.Ordinal) ? localUri.IndexOf('/', 8) : -1;
        return slash < 0 ? localUri : localUri.Substring(slash + 1);
    }

    internal Dictionary<string, string> ToFields()
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["account"] = Account,
            ["name"] = Name,
            ["kind"] = Kind,
            ["folder"] = Folder,
            ["local"] = LocalUri,
            ["size"] = Size.ToString(CultureInfo.InvariantCulture),
            ["saved"] = SavedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["state"] = State.ToString(),
            ["attempts"] = Attempts.ToString(CultureInfo.InvariantCulture),
            ["next"] = NextAttemptUtc.ToString("o", CultureInfo.InvariantCulture),
            ["full"] = OutOfSpace ? "1" : "0",
            ["sent"] = Sent ? "1" : "0",
        };
        if (Mode != null)
            fields["mode"] = Mode;
        if (!string.IsNullOrEmpty(Owner))
            fields["owner"] = Owner!;
        if (ThumbnailUri != null)
            fields["thumbnail"] = ThumbnailUri;
        if (LastError != null)
            fields["error"] = LastError;
        if (CloudHash != null)
            fields["cloud"] = CloudHash;
        return fields;
    }

    // Null for a row that does not say where its save is.
    internal static InventoryRow? FromFields(string id, IReadOnlyDictionary<string, string> fields)
    {
        string Text(string key) => fields.TryGetValue(key, out var value) ? value : string.Empty;
        string? Maybe(string key) => fields.TryGetValue(key, out var value) ? value : null;
        DateTime When(string key) => DateTime.TryParse(Text(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at.ToUniversalTime() : DateTime.MinValue;

        var row = new InventoryRow
        {
            Id = id,
            Account = Text("account"),
            // A save kept by an older build may carry markup or a slash the service refuses.
            Name = Inventory.CleanName(Text("name")),
            Kind = Text("kind"),
            Folder = string.IsNullOrEmpty(Text("folder")) ? Inventory.Root : Text("folder"),
            Owner = Maybe("owner"),
            Mode = Maybe("mode"),
            LocalUri = Text("local"),
            ThumbnailUri = Maybe("thumbnail"),
            Size = long.TryParse(Text("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
            SavedUtc = When("saved"),
            State = Enum.TryParse(Text("state"), out InventoryRowState state) ? state : InventoryRowState.Pending,
            Attempts = int.TryParse(Text("attempts"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts) ? attempts : 0,
            LastError = Maybe("error"),
            NextAttemptUtc = When("next"),
            CloudHash = Maybe("cloud"),
            OutOfSpace = Text("full") == "1",
            Sent = Text("sent") == "1",
        };
        return string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.LocalUri) || string.IsNullOrEmpty(row.Account) ? null : row;
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core;

// Deleting in a group: its builders may change what is there but not throw it away, so a builder's delete is a
// request, and a moderator or anyone above says yes or no. Until then the thing stays, marked for everyone, and
// the one who asked may take the request back. -xlinka
public static partial class Inventory
{
    public static async Task<InventoryResult> RequestDeleteAsync(InventoryEntry entry)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        if (string.IsNullOrEmpty(entry.OwnerId))
            return InventoryResult.Failure("Only things in a group need a request to delete.");
        var asked = await client.RequestInventoryDelete(entry.OwnerId!, entry.IsFolder ? "folder" : "item", entry.Id).ConfigureAwait(false);
        if (!asked.Success)
            return InventoryResult.Failure(asked.Message ?? "Couldn't ask for that to be deleted.");
        InventorySync.Touch();
        return InventoryResult.Success($"Asked the group's moderators to delete \"{entry.Name}\". It stays until one of them decides.");
    }

    // The requests waiting in a group: every one for a moderator and up, a builder's own for a builder.
    public static async Task<(List<InventoryDeleteRequest>? requests, string? problem)> DeleteRequestsAsync(string groupId)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return (null, NotSignedIn().Message);
        var listed = await client.GetInventoryDeleteRequests(groupId).ConfigureAwait(false);
        return listed.Success ? (listed.Data ?? new List<InventoryDeleteRequest>(), null) : (null, listed.Message ?? "Couldn't read the requests.");
    }

    public static async Task<InventoryResult> ApproveDeleteAsync(string groupId, InventoryDeleteRequest request)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        var done = await client.ApproveInventoryDelete(groupId, request.Id).ConfigureAwait(false);
        if (!done.Success)
            return InventoryResult.Failure(done.Message ?? "Couldn't delete that.");
        InventorySync.Touch();
        InventorySync.SpaceFreed();
        bool deleted = done.Data.ValueKind == System.Text.Json.JsonValueKind.Object
            && done.Data.TryGetProperty("deleted", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;
        return InventoryResult.Success(deleted
            ? $"Deleted \"{request.Name}\", as {request.RequestedByName} asked."
            : $"\"{request.Name}\" was already gone; the request is cleared.");
    }

    // A moderator saying no, or the asker taking it back; the thing stays either way.
    public static async Task<InventoryResult> DropDeleteRequestAsync(string groupId, string requestId, string name, bool mine)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        var dropped = await client.DropInventoryDeleteRequest(groupId, requestId).ConfigureAwait(false);
        if (!dropped.Success)
            return InventoryResult.Failure(dropped.Message ?? "Couldn't clear that request.");
        InventorySync.Touch();
        return InventoryResult.Success(mine ? $"You no longer ask to delete \"{name}\"." : $"Kept \"{name}\"; the request is cleared.");
    }
}

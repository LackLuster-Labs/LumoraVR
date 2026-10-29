// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Threading.Tasks;
using Lumora.Core.Management;
using Lumora.Nexus.Cloud;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Social;

// Telling the moderators about an account, through the client the engine signs in with. Only a signed-in
// account can, and never about itself; the service refuses both anyway, this just never asks.
public static class Reports
{
    public static bool CanReport(string? targetId, string? selfId)
        => !string.IsNullOrEmpty(targetId) && !string.IsNullOrEmpty(selfId) && !string.Equals(targetId, selfId, StringComparison.Ordinal);

    // The session a world is, for the report's context: its name and id, or neither when it is no session.
    // Every session counts, private ones too: this goes to the moderators, not to anybody's friends list.
    public static (string? WorldName, string? SessionId) Where(World? world)
    {
        if (world == null || world.IsDestroyed || world.IsDisposed || world.IsUserspace)
            return (null, null);
        var session = world.Session;
        string? id = session != null && !session.IsDisposed ? session.SessionId : null;
        if (id == null || !SessionIdentifier.IsValid(id))
            return (null, null);
        string name = WorldManager.IsLocalHome(world) ? "Local" : world.WorldName?.Value?.Trim() ?? string.Empty;
        return (name.Length > 0 ? name : world.Name, id);
    }

    public static async Task<ApiResponse<ReportReceipt>> SendAsync(ReportSubmission report, IProgress<float>? progress = null)
    {
        var client = Engine.Current?.CDNClient;
        if (client == null || !client.IsAuthenticated)
            return ApiResponse<ReportReceipt>.Fail(HttpStatusCode.Unauthorized, "Not signed in");
        try
        {
            return await client.SubmitReport(report, progress).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ApiResponse<ReportReceipt>.FromException(ex);
        }
    }
}

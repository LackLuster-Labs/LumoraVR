// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A user's link to the session, refreshed once a second. Their own machine measures the round trip to the
// host in milliseconds (0 on the host, -1 until it is known), bytes a second down and up now and at the
// highest this session, and bytes down and up in total. Packet loss is the host's: the share, 0 to 1, of
// what it sent them lately that had to go again. Zeros for a user the script cannot see.
[Node("Users/Info/Network", Capability.UsersRead, Description = "A user's link to the session, refreshed once a second.",
    Keywords = "ping, latency, lag, bandwidth, packet loss")]
public sealed class UserNetwork : ReadNode
{
    [return: Port("ping")]
    public static int Run(ScriptContext ctx, User? user, out float packetLoss, out float download, out float upload,
        out float peakDownload, out float peakUpload, out ulong downloaded, out ulong uploaded)
    {
        packetLoss = download = upload = peakDownload = peakUpload = 0f;
        downloaded = uploaded = 0;
        if (!ScriptReads.Visible(ctx, user))
            return 0;
        packetLoss = user!.PacketLoss.Value;
        download = user.DownloadSpeed.Value;
        upload = user.UploadSpeed.Value;
        peakDownload = user.DownloadSpeedMax.Value;
        peakUpload = user.UploadSpeedMax.Value;
        downloaded = user.DownloadedBytes.Value;
        uploaded = user.UploadedBytes.Value;
        return user.Ping.Value;
    }
}

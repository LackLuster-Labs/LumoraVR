// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Networking;

/// <summary>
/// Hard upper bounds for any size/count field that crosses the network trust
/// boundary. Anything a peer can declare must be checked against one of these
/// before allocation. Treat the values as defensive ceilings — the legitimate
/// traffic should never come close.
/// </summary>
public static class NetworkLimits
{
    /// <summary>Maximum size of a single ControlMessage payload, in bytes.</summary>
    public const int MaxControlMessagePayload = 1 * 1024 * 1024; // 1 MB

    /// <summary>Maximum size of a single StreamMessage data blob (legacy + sync), in bytes.</summary>
    public const int MaxStreamMessageData = 256 * 1024; // 256 KB

    /// <summary>Maximum number of StreamEntry items in one legacy StreamMessage.</summary>
    public const int MaxStreamEntriesPerMessage = 256;

    /// <summary>Maximum size of a single legacy StreamEntry data blob, in bytes.</summary>
    public const int MaxStreamEntryData = 64 * 1024; // 64 KB

    /// <summary>Maximum declared total size of a peer-to-peer asset transfer, in bytes.</summary>
    public const int MaxAssetTransferTotalBytes = 256 * 1024 * 1024; // 256 MB

    /// <summary>Maximum size of a single asset transfer chunk, in bytes.</summary>
    public const int MaxAssetChunkBytes = 256 * 1024; // 256 KB

    /// <summary>Maximum decompressed size of a LAN session announcement, in bytes.</summary>
    public const int MaxLanAnnouncementBytes = 64 * 1024; // 64 KB

    /// <summary>Maximum number of pending (post-connect, pre-JoinRequest) peers a host accepts.</summary>
    public const int MaxPendingConnections = 64;

    /// <summary>Maximum number of pending peers from a single source IP.</summary>
    public const int MaxPendingPerIP = 4;

    /// <summary>Maximum byte length of an asset URI string carried in a control message.</summary>
    public const int MaxAssetUriBytes = 4 * 1024; // 4 KB

    /// <summary>Maximum size of a single RawFrameMessage payload, in bytes. Sized for
    /// codec frames (e.g. Opus 60 ms ≈ 960 B at 128 kbps); 4 KB leaves generous headroom.</summary>
    public const int MaxRawFrameBytes = 4 * 1024;
}

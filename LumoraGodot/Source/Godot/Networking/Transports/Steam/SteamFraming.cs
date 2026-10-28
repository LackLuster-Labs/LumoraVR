// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Steam is a desktop-only transport. The Steamworks binding has no Android build, and a type that
// merely MENTIONS a Steamworks type cannot be loaded there: the whole engine init failed on a Pico 4
// with 'Could not load type of field SteamNetworkManager:_listenerMap', long before any runtime
// 'is this Android' check could run. Compile the transport out on mobile entirely. -xlinka
#if GODOT_PC

namespace Lumora.Godot.Networking.Transports.Steam;

// The rules a Steam channel frames by, kept apart from every Steamworks type so they can be checked on their
// own. A reliable frame bigger than one Steam message goes out as a 4-byte length and then its bytes in
// message-sized pieces; the receiver collects the pieces in a buffer of that length and hands the frame on
// whole once it is full. A reliable frame of exactly four bytes is sent the same way, or its receiver would
// take it for a length. Unreliable frames go as they are up to MaxUnreliableSize; a bigger one is sent as a
// reliable frame. -xlinka
internal static class SteamFraming
{
    // What SendMessageToConnection accepts in one message.
    public const int MaxMessageSize = 524288;

    // The most the library sends unreliably. A bigger unreliable message it sends reliably on its own, without a
    // word, so it arrived flagged reliable and, on a channel whose frame was still waiting for room, between that
    // frame's pieces: the receiver took it for one and the connection dropped or the frame came out wrong. -xlinka
    public const int MaxUnreliableSize = 16500;

    public const int PrefixLength = 4;

    // The largest frame a peer may announce. The announced length is the peer's to choose and the buffer is
    // allocated from it before a byte of the frame arrives, so without a ceiling one message could ask this
    // machine for two gigabytes. The biggest real frames are a joiner's world slices, which a decompression
    // cap holds far below this. -xlinka
    public const int MaxReassembledBytes = 64 * 1024 * 1024;

    public enum Arrival
    {
        // A frame on its own: hand it on as it is.
        Whole,
        // The length of a frame whose pieces follow.
        Prefix,
        // One piece of the frame being collected.
        Piece,
    }

    public static bool NeedsPrefix(long count, bool reliable)
        => reliable && (count == PrefixLength || count > MaxMessageSize);

    public static int PieceCount(long count)
        => count <= 0 ? 0 : (int)((count + MaxMessageSize - 1) / MaxMessageSize);

    // Messages on one connection handle arrive in order, and the reliable ones after a length are that frame's
    // pieces until it is full: each a whole message but the last, which is what the frame still lacks. A reliable
    // message of any other size in between is not a piece but something an older build let the library turn
    // reliable (see MaxUnreliableSize), and it is handed on as it is.
    public static Arrival Classify(in ReassemblyState state, bool reliable, int payloadSize)
    {
        if (!reliable)
            return Arrival.Whole;
        if (state.buffer != null)
            return payloadSize == MaxMessageSize || payloadSize == state.expectingBytes - state.receivedBytes ? Arrival.Piece : Arrival.Whole;
        return payloadSize == PrefixLength ? Arrival.Prefix : Arrival.Whole;
    }

    // False for a length no sender of ours announces: four bytes, or more than one message holds, up to
    // the ceiling. The connection that sent it is broken either way.
    public static bool Begin(ref ReassemblyState state, int announced)
    {
        bool valid = announced == PrefixLength || (announced > MaxMessageSize && announced <= MaxReassembledBytes);
        if (!valid)
        {
            Reset(ref state);
            return false;
        }
        state.buffer = new byte[announced];
        state.expectingBytes = announced;
        state.receivedBytes = 0;
        return true;
    }

    // Where in the frame a piece of this size goes, or -1 when it would run past the announced length.
    public static int Place(ref ReassemblyState state, int payloadSize)
    {
        if (state.buffer == null || state.receivedBytes < 0 || state.expectingBytes < 0 || payloadSize <= 0
            || payloadSize > state.expectingBytes - state.receivedBytes)
            return -1;
        int at = state.receivedBytes;
        state.receivedBytes += payloadSize;
        return at;
    }

    public static bool IsComplete(in ReassemblyState state)
        => state.buffer != null && state.receivedBytes == state.expectingBytes;

    public static byte[] Take(ref ReassemblyState state, out int length)
    {
        var frame = state.buffer;
        length = state.receivedBytes;
        Reset(ref state);
        return frame;
    }

    public static void Reset(ref ReassemblyState state)
    {
        state.buffer = null!;
        state.expectingBytes = -1;
        state.receivedBytes = -1;
    }
}
#endif

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Text;

namespace Lumora.Nexus.Transport.Framed;

// The frames our stream transports (TCP, QUIC) carry. A byte stream has no messages of its own, so every
// message is cut into frames of at most MaxFragment bytes, each tagged with the lane it belongs to: a large
// world state on one lane never holds a pose on another for longer than one frame. A frame is four bytes of
// header, then its payload:
//
//   kind (1) | lane (1) | payload length (2, little endian) | payload
//
// Lanes stand in for LNL's channels: 0 foreground and 1 background, both reliable and ordered; 2 unreliable,
// which the sender drops rather than queues behind a backlog; 3 and up bulk, reliable and in any order with each
// other. Each lane is ordered within itself and owes nothing to the others. -xlinka
internal static class FramedWire
{
    public const int HeaderBytes = 4;
    public const int MaxFragment = 16 * 1024;
    public const int MaxFramePayload = ushort.MaxValue;

    public const byte LaneForeground = 0;
    public const byte LaneBackground = 1;
    public const byte LaneUnreliable = 2;
    public const byte FirstBulkLane = 3;
    public const int MaxLanes = 16;

    // Data frames: Part when more of the message follows, Last for its final frame.
    public const byte KindPart = 0;
    public const byte KindLast = 1;
    public const byte KindHello = 2;
    public const byte KindPing = 3;
    public const byte KindPong = 4;
    public const byte KindClose = 5;
    // Total bytes read so far, from a receiver to a paced sender.
    public const byte KindAck = 6;

    public const byte Version = 1;
    private const int MaxAppIdBytes = 64;
    public const int MaxCloseReasonBytes = 200;

    // What a message may grow to: the largest frame the session decodes, plus the crypto envelope.
    public const int MaxMessageBytes = NetworkLimits.MaxDecompressedFrameBytes + 64;

    // Before the crypto handshake is done only its hellos may arrive, and they are small.
    public const int MaxHandshakeMessageBytes = 4096;

    public static void WriteHeader(Span<byte> into, byte kind, byte lane, int length)
    {
        into[0] = kind;
        into[1] = lane;
        BinaryPrimitives.WriteUInt16LittleEndian(into.Slice(2, 2), (ushort)length);
    }

    public static byte[] Control(byte kind, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderBytes + payload.Length];
        WriteHeader(frame, kind, 0, payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static byte[] Hello(string appId)
    {
        var id = Encoding.UTF8.GetBytes(appId);
        var payload = new byte[2 + id.Length];
        payload[0] = Version;
        payload[1] = (byte)id.Length;
        id.CopyTo(payload, 2);
        return Control(KindHello, payload);
    }

    public static bool TryReadHello(ReadOnlySpan<byte> payload, out byte version, out string appId)
    {
        version = 0;
        appId = "";
        if (payload.Length < 2)
            return false;
        version = payload[0];
        int length = payload[1];
        if (length > MaxAppIdBytes || payload.Length != 2 + length)
            return false;
        try
        {
            appId = Encoding.UTF8.GetString(payload.Slice(2, length));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static byte[] CloseFrame(string reason)
    {
        var bytes = Encoding.UTF8.GetBytes(reason ?? "");
        int length = Math.Min(bytes.Length, MaxCloseReasonBytes);
        return Control(KindClose, bytes.AsSpan(0, length));
    }

    public static string ReadCloseReason(ReadOnlySpan<byte> payload)
    {
        try
        {
            return Encoding.UTF8.GetString(payload.Slice(0, Math.Min(payload.Length, MaxCloseReasonBytes)));
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    public static bool IsData(byte kind) => kind is KindPart or KindLast;
}

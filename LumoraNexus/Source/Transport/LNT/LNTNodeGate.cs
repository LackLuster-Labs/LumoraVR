// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud;

namespace Lumora.Nexus.Transport.LNT;

// The TCP gate of an official NAT node (PROTOCOL.md, section 10). A joiner says which session it wants; the node
// rings the host over the host's own registration, the host dials the gate back with the nonce it was given, and
// the node joins the two sockets byte for byte. Each side reads one line of answer and nothing more, since the
// session's own frames follow on the same socket. -xlinka
internal static class LNTNodeGate
{
    public const string Version = "2";
    public const string Call = "tcp-call";
    public const string ReadyLine = "ready";
    private const int MaxLineBytes = 256;
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(10);

    // From the node's listing; a node listing none has no gate.
    public static int TcpPortOf(NetworkNode node) => node.TcpPort;

    public static bool HasGate(NetworkNode node) => node.TcpPort > 0;

    public static async Task<Socket> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
                await socket.ConnectAsync(new IPEndPoint(literal, port), cancellationToken).ConfigureAwait(false);
            else
                await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            LNTSockets.Configure(socket);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<Socket> JoinAsync(string node, int port, string sessionId, CancellationToken cancellationToken)
    {
        var socket = await ConnectAsync(node, port, cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteLineAsync(socket, $"join;{Version};{sessionId}", cancellationToken).ConfigureAwait(false);
            var answer = await ReadLineAsync(socket, cancellationToken).ConfigureAwait(false);
            if (answer != ReadyLine)
                throw new IOException($"node answered '{answer}'");
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<Socket> AnswerAsync(IPAddress node, int port, string sessionId, string nonce, CancellationToken cancellationToken)
    {
        var socket = await ConnectAsync(node.ToString(), port, cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteLineAsync(socket, $"host;{Version};{sessionId};{nonce}", cancellationToken).ConfigureAwait(false);
            var answer = await ReadLineAsync(socket, cancellationToken).ConfigureAwait(false);
            if (answer != ReadyLine)
                throw new IOException($"node answered '{answer}'");
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static async Task WriteLineAsync(Socket socket, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        int sent = 0;
        while (sent < bytes.Length)
            sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, cancellationToken).ConfigureAwait(false);
    }

    // One byte at a time: whatever follows the line belongs to the session.
    private static async Task<string> ReadLineAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnswerWithin);
        var line = new byte[MaxLineBytes];
        var one = new byte[1];
        int length = 0;
        try
        {
            while (true)
            {
                int read = await socket.ReceiveAsync(one.AsMemory(), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException(length == 0 ? "node closed the connection" : "node closed mid-answer");
                if (one[0] == (byte)'\n')
                    return Encoding.ASCII.GetString(line, 0, length).TrimEnd('\r');
                if (length == MaxLineBytes)
                    throw new IOException("node answer too long");
                line[length++] = one[0];
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"node did not answer within {AnswerWithin.TotalSeconds:F0} s");
        }
    }
}

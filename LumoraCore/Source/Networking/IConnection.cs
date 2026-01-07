using System;
using System.Net;

namespace Lumora.Core.Networking;

/// <summary>
/// Represents a network connection (client or peer).
/// Interface abstraction for different transport layers.
/// </summary>
public interface IConnection : IDisposable
{
    /// <summary>
    /// Whether the connection is currently open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Reason for connection failure (if any).
    /// </summary>
    string FailReason { get; }

    /// <summary>
    /// Remote IP address.
    /// </summary>
    IPAddress IP { get; }

    /// <summary>
    /// Connection address/URI.
    /// </summary>
    Uri Address { get; }

    /// <summary>
    /// Unique identifier for this connection.
    /// </summary>
    string Identifier { get; }

    /// <summary>
    /// Total bytes received from this connection.
    /// </summary>
    ulong ReceivedBytes { get; }

    /// <summary>
    /// Event fired when connection is closed.
    /// </summary>
    event Action<IConnection> Closed;

    /// <summary>
    /// Event fired when connection succeeds.
    /// </summary>
    event Action<IConnection> Connected;

    /// <summary>
    /// Event fired when connection fails.
    /// </summary>
    event Action<IConnection> ConnectionFailed;

    /// <summary>
    /// Event fired when new data is received.
    /// </summary>
    event Action<byte[], int> DataReceived;

    /// <summary>
    /// Initiate connection.
    /// </summary>
    void Connect(Action<string> statusCallback);

    /// <summary>
    /// Close the connection.
    /// </summary>
    void Close();

    /// <summary>
    /// Send data to this connection.
    /// </summary>
    void Send(byte[] data, int length, bool reliable, bool background);
}

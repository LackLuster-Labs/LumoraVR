using System;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Utilities for generating and validating session identifiers.
/// </summary>
public static class SessionIdentifier
{
    /// <summary>
    /// Prefix for all session IDs.
    /// </summary>
    public const string SessionPrefix = "S-";

    /// <summary>
    /// Generate a new unique session identifier.
    /// </summary>
    /// <returns>A new session ID in format S-{guid}</returns>
    public static string Generate()
    {
        return SessionPrefix + Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Generate a session ID from an existing GUID.
    /// </summary>
    public static string FromGuid(Guid guid)
    {
        return SessionPrefix + guid.ToString();
    }

    /// <summary>
    /// Check if a string is a valid session identifier.
    /// </summary>
    /// <param name="sessionId">The string to validate</param>
    /// <returns>True if valid session ID format</returns>
    public static bool IsValid(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        if (!sessionId.StartsWith(SessionPrefix))
            return false;

        string guidPart = sessionId.Substring(SessionPrefix.Length);
        return Guid.TryParse(guidPart, out _);
    }

    /// <summary>
    /// Normalize a session ID to lowercase.
    /// </summary>
    public static string Normalize(string sessionId)
    {
        return sessionId?.ToLowerInvariant();
    }

    /// <summary>
    /// Extract the GUID portion from a session ID.
    /// </summary>
    /// <param name="sessionId">The session ID</param>
    /// <param name="guid">The extracted GUID</param>
    /// <returns>True if extraction succeeded</returns>
    public static bool TryGetGuid(string sessionId, out Guid guid)
    {
        guid = Guid.Empty;

        if (!IsValid(sessionId))
            return false;

        string guidPart = sessionId.Substring(SessionPrefix.Length);
        return Guid.TryParse(guidPart, out guid);
    }

    /// <summary>
    /// Compare two session IDs for equality (case-insensitive).
    /// </summary>
    public static bool AreEqual(string sessionId1, string sessionId2)
    {
        if (sessionId1 == null || sessionId2 == null)
            return sessionId1 == sessionId2;

        return string.Equals(sessionId1, sessionId2, StringComparison.OrdinalIgnoreCase);
    }
}

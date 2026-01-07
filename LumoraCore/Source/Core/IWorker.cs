using System;
using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// Interface for all workers in the world (Slots, Components, etc.)
/// Matches the engine's worker reflection pattern.
/// </summary>
public interface IWorker : IWorldElement
{
    /// <summary>
    /// Type of this worker
    /// </summary>
    Type WorkerType { get; }

    /// <summary>
    /// Full type name of this worker
    /// </summary>
    string WorkerTypeName { get; }

    /// <summary>
    /// Try to get a field by name
    /// </summary>
    IField TryGetField(string name);

    /// <summary>
    /// Try to get a typed field by name
    /// </summary>
    IField<T> TryGetField<T>(string name);

    /// <summary>
    /// Get all referenced objects from this worker
    /// </summary>
    IEnumerable<IWorldElement> GetReferencedObjects(bool assetRefOnly, bool persistentOnly = true);
}

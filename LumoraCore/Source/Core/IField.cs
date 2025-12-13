using System;

namespace Lumora.Core;

/// <summary>
/// Interface for elements that expose a typed value.
/// </summary>
public interface IValue<T> : IChangeable
{
    T Value { get; set; }
}

/// <summary>
/// Base interface for all field types.
/// </summary>
public interface IField : IChangeable, ILinkable, IInitializable
{
    object BoxedValue { get; set; }
    Type ValueType { get; }
    bool CanWrite { get; }
}

/// <summary>
/// Typed field interface combining IField with IValue.
/// </summary>
public interface IField<T> : IField, IValue<T>
{
}

/// <summary>
/// Delegate for field value change events.
/// </summary>
public delegate void SyncFieldEvent<T>(IField<T> field);

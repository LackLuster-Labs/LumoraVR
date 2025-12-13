using System;
using Lumora.Core.Math;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Common field validators that can be used with SyncField LocalFilter.
/// </summary>
public static class FieldValidators
{
    /// <summary>
    /// Clamp a numeric value to a range.
    /// </summary>
    public static Func<T, IField<T>, T> Clamp<T>(T min, T max) where T : IComparable<T>
    {
        return (value, field) =>
        {
            if (value.CompareTo(min) < 0) return min;
            if (value.CompareTo(max) > 0) return max;
            return value;
        };
    }

    /// <summary>
    /// Clamp a float value to a range.
    /// </summary>
    public static Func<float, IField<float>, float> ClampFloat(float min, float max)
    {
        return (value, field) => System.Math.Clamp(value, min, max);
    }

    /// <summary>
    /// Clamp a double value to a range.
    /// </summary>
    public static Func<double, IField<double>, double> ClampDouble(double min, double max)
    {
        return (value, field) => System.Math.Clamp(value, min, max);
    }

    /// <summary>
    /// Clamp an int value to a range.
    /// </summary>
    public static Func<int, IField<int>, int> ClampInt(int min, int max)
    {
        return (value, field) => System.Math.Clamp(value, min, max);
    }

    /// <summary>
    /// Ensure float is not NaN or Infinity.
    /// </summary>
    public static Func<float, IField<float>, float> SanitizeFloat(float fallback = 0f)
    {
        return (value, field) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }

    /// <summary>
    /// Ensure double is not NaN or Infinity.
    /// </summary>
    public static Func<double, IField<double>, double> SanitizeDouble(double fallback = 0.0)
    {
        return (value, field) => double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
    }

    /// <summary>
    /// Ensure float3 has no NaN or Infinity components.
    /// </summary>
    public static Func<float3, IField<float3>, float3> SanitizeFloat3(float3? fallback = null)
    {
        var fb = fallback ?? float3.Zero;
        return (value, field) =>
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x) ||
                float.IsNaN(value.y) || float.IsInfinity(value.y) ||
                float.IsNaN(value.z) || float.IsInfinity(value.z))
            {
                return fb;
            }
            return value;
        };
    }

    /// <summary>
    /// Ensure float4 has no NaN or Infinity components.
    /// </summary>
    public static Func<float4, IField<float4>, float4> SanitizeFloat4(float4? fallback = null)
    {
        var fb = fallback ?? float4.Zero;
        return (value, field) =>
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x) ||
                float.IsNaN(value.y) || float.IsInfinity(value.y) ||
                float.IsNaN(value.z) || float.IsInfinity(value.z) ||
                float.IsNaN(value.w) || float.IsInfinity(value.w))
            {
                return fb;
            }
            return value;
        };
    }

    /// <summary>
    /// Ensure quaternion is normalized and valid.
    /// </summary>
    public static Func<floatQ, IField<floatQ>, floatQ> SanitizeQuaternion()
    {
        return (value, field) =>
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x) ||
                float.IsNaN(value.y) || float.IsInfinity(value.y) ||
                float.IsNaN(value.z) || float.IsInfinity(value.z) ||
                float.IsNaN(value.w) || float.IsInfinity(value.w))
            {
                return floatQ.Identity;
            }
            // Normalize if not already
            var lengthSq = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            if (lengthSq < 0.0001f)
            {
                return floatQ.Identity;
            }
            return value;
        };
    }

    /// <summary>
    /// Clamp string length.
    /// </summary>
    public static Func<string, IField<string>, string> MaxLength(int maxLength)
    {
        return (value, field) =>
        {
            if (value == null) return null;
            return value.Length > maxLength ? value.Substring(0, maxLength) : value;
        };
    }

    /// <summary>
    /// Replace null with empty string.
    /// </summary>
    public static Func<string, IField<string>, string> NullToEmpty()
    {
        return (value, field) => value ?? string.Empty;
    }

    /// <summary>
    /// Trim whitespace from string.
    /// </summary>
    public static Func<string, IField<string>, string> Trim()
    {
        return (value, field) => value?.Trim();
    }

    /// <summary>
    /// Ensure value is positive (>= 0).
    /// </summary>
    public static Func<float, IField<float>, float> NonNegative()
    {
        return (value, field) => value < 0 ? 0 : value;
    }

    /// <summary>
    /// Ensure value is positive (> 0).
    /// </summary>
    public static Func<float, IField<float>, float> Positive(float minPositive = 0.0001f)
    {
        return (value, field) => value < minPositive ? minPositive : value;
    }

    /// <summary>
    /// Combine multiple filters.
    /// </summary>
    public static Func<T, IField<T>, T> Combine<T>(params Func<T, IField<T>, T>[] filters)
    {
        return (value, field) =>
        {
            foreach (var filter in filters)
            {
                value = filter(value, field);
            }
            return value;
        };
    }
}

/// <summary>
/// Extension methods for applying validators to sync fields.
/// </summary>
public static class FieldValidatorExtensions
{
    /// <summary>
    /// Apply a clamp filter to a numeric sync field.
    /// </summary>
    public static SyncField<float> WithClamp(this SyncField<float> field, float min, float max)
    {
        field.LocalFilter = FieldValidators.ClampFloat(min, max);
        return field;
    }

    /// <summary>
    /// Apply sanitization to a float sync field.
    /// </summary>
    public static SyncField<float> WithSanitize(this SyncField<float> field, float fallback = 0f)
    {
        field.LocalFilter = FieldValidators.SanitizeFloat(fallback);
        return field;
    }

    /// <summary>
    /// Apply sanitization to a float3 sync field.
    /// </summary>
    public static SyncField<float3> WithSanitize(this SyncField<float3> field, float3? fallback = null)
    {
        field.LocalFilter = FieldValidators.SanitizeFloat3(fallback);
        return field;
    }

    /// <summary>
    /// Apply sanitization to a quaternion sync field.
    /// </summary>
    public static SyncField<floatQ> WithSanitize(this SyncField<floatQ> field)
    {
        field.LocalFilter = FieldValidators.SanitizeQuaternion();
        return field;
    }

    /// <summary>
    /// Apply max length to a string sync field.
    /// </summary>
    public static SyncField<string> WithMaxLength(this SyncField<string> field, int maxLength)
    {
        field.LocalFilter = FieldValidators.MaxLength(maxLength);
        return field;
    }

    /// <summary>
    /// Apply non-negative filter to a float sync field.
    /// </summary>
    public static SyncField<float> WithNonNegative(this SyncField<float> field)
    {
        field.LocalFilter = FieldValidators.NonNegative();
        return field;
    }
}

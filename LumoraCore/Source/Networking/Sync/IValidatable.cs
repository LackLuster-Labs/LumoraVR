using System.Collections.Generic;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Interface for elements that support custom validation.
/// Elements implementing this can provide custom validation logic
/// for incoming changes.
/// </summary>
public interface IValidatable
{
    /// <summary>
    /// Validate a proposed change to this element.
    /// Called before applying incoming delta changes.
    /// </summary>
    /// <param name="context">Validation context with rules and state</param>
    /// <returns>Validity result for this change</returns>
    MessageValidity ValidateChange(ValidationContext context);
}

/// <summary>
/// Context passed during validation containing rules and related data.
/// </summary>
public class ValidationContext
{
    /// <summary>
    /// The sync message being validated.
    /// </summary>
    public BinaryMessageBatch Message { get; }

    /// <summary>
    /// User who sent the change.
    /// </summary>
    public User SenderUser => Message?.SenderUser;

    /// <summary>
    /// The sync element being validated.
    /// </summary>
    public SyncElement Element { get; }

    /// <summary>
    /// Cross-record validation rules.
    /// </summary>
    public List<ValidationGroup.Rule> Rules { get; }

    /// <summary>
    /// World context.
    /// </summary>
    public World World { get; }

    /// <summary>
    /// Whether we are the authority for this world.
    /// </summary>
    public bool IsAuthority => World?.IsAuthority == true;

    /// <summary>
    /// Additional context data for custom validators.
    /// </summary>
    public Dictionary<string, object> Data { get; }

    public ValidationContext(
        BinaryMessageBatch message,
        SyncElement element,
        List<ValidationGroup.Rule> rules,
        World world)
    {
        Message = message;
        Element = element;
        Rules = rules ?? new List<ValidationGroup.Rule>();
        World = world;
        Data = new Dictionary<string, object>();
    }

    /// <summary>
    /// Accept the change.
    /// </summary>
    public static MessageValidity Accept() => MessageValidity.Valid;

    /// <summary>
    /// Reject the change due to conflict.
    /// </summary>
    public static MessageValidity Conflict() => MessageValidity.Conflict;

    /// <summary>
    /// Ignore the change (don't apply but don't conflict).
    /// </summary>
    public static MessageValidity Ignore() => MessageValidity.Ignore;
}

/// <summary>
/// Attribute to mark fields that require validation.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field)]
public class ValidatedAttribute : System.Attribute
{
    /// <summary>
    /// Minimum value (for numeric types).
    /// </summary>
    public double? Min { get; set; }

    /// <summary>
    /// Maximum value (for numeric types).
    /// </summary>
    public double? Max { get; set; }

    /// <summary>
    /// Whether null/empty is allowed (for strings/refs).
    /// </summary>
    public bool AllowNull { get; set; } = true;

    /// <summary>
    /// Custom validator type to use.
    /// </summary>
    public System.Type ValidatorType { get; set; }
}

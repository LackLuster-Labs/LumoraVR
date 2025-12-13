using System;
using System.Collections.Generic;
using System.IO;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Validation context for processing delta records with cross-record rules.
/// </summary>
public class ValidationGroup
{
    public struct Rule
    {
        public RefID OtherMessage { get; }
        public bool MustExist { get; }
        public Func<BinaryReader, bool> CustomValidation { get; }

        public Rule(RefID otherMessage, bool mustExist, Func<BinaryReader, bool> customValidation = null)
        {
            OtherMessage = otherMessage;
            MustExist = mustExist;
            CustomValidation = customValidation;
        }
    }

    public int RequestingRecordIndex { get; private set; }
    public SyncElement RequestingSyncElement { get; private set; }
    public List<Rule> ValidationRules { get; private set; }

    /// <summary>
    /// The message batch being validated.
    /// </summary>
    public BinaryMessageBatch Message { get; private set; }

    /// <summary>
    /// The world context for validation.
    /// </summary>
    public World World { get; private set; }

    public void Set(int requestingRecordIndex, SyncElement requestingElement, List<Rule> rules,
        BinaryMessageBatch message = null, World world = null)
    {
        RequestingRecordIndex = requestingRecordIndex;
        RequestingSyncElement = requestingElement;
        ValidationRules = rules;
        Message = message;
        World = world;
    }

    public void Clear()
    {
        RequestingRecordIndex = 0;
        RequestingSyncElement = null;
        ValidationRules?.Clear();
        Message = null;
        World = null;
    }

    /// <summary>
    /// Create a ValidationContext from this group's current state.
    /// </summary>
    public ValidationContext CreateContext()
    {
        return new ValidationContext(Message, RequestingSyncElement, ValidationRules, World);
    }

    /// <summary>
    /// Validate using IValidatable if supported.
    /// </summary>
    public MessageValidity ValidateElement()
    {
        if (RequestingSyncElement is IValidatable validatable)
        {
            return validatable.ValidateChange(CreateContext());
        }
        return MessageValidity.Valid;
    }
}

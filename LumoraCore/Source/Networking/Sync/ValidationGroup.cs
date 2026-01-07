using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Validation group for sync message validation
/// </summary>
public class ValidationGroup
{
    public int RequestingRecordIndex { get; private set; }
    public SyncElement RequestingSyncElement { get; private set; }
    public List<Rule> ValidationRules { get; private set; }

    public ValidationGroup()
    {
        ValidationRules = new List<Rule>();
    }

    public void Set(int requestingRecordIndex, SyncElement requestingElement, List<Rule> rules, BinaryMessageBatch batch, World world)
    {
        RequestingRecordIndex = requestingRecordIndex;
        RequestingSyncElement = requestingElement;
        ValidationRules = new List<Rule>(rules);
    }

    public class Rule
    {
        public RefID OtherMessage;
        public bool MustExist;
        public Func<BinaryReader, bool> CustomValidation;

        public Rule(RefID otherMessage, bool mustExist = false, Func<BinaryReader, bool> customValidation = null)
        {
            OtherMessage = otherMessage;
            MustExist = mustExist;
            CustomValidation = customValidation;
        }
    }
}

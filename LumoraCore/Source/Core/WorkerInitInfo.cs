using System;
using System.Collections.Generic;
using System.Reflection;

namespace Lumora.Core;

public sealed class WorkerInitInfo
{
    public FieldInfo[] SyncMemberFields;
    public MethodInfo[] ListedMethods;
    public bool[] SyncMemberNonpersistent;
    public bool[] SyncMemberNondrivable;
    public bool[] SyncMemberDontCopy;
    public string[] SyncMemberNames;
    public Dictionary<string, int> SyncMemberNameToIndex;
    public Dictionary<string, List<string>> OldSyncMemberNames;
    public object[] DefaultValues;
    public Type ConnectorType;
    public bool HasUpdateMethods;
    public bool HasAudioUpdateMethod;
    public bool HasAudioConfigurationChangedMethod;
    public bool HasLinkedMethod;
    public bool HasUnlinkedMethod;
    public bool ReceivesAnyWorldEvent;
    public bool[] ReceivesWorldEvent;
    public bool PreserveWithAssets;
    public bool SingleInstancePerSlot;
    public bool DontDuplicate;
    public int DefaultUpdateOrder;
    public bool RegisterGlobally;
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

internal static class WorkerInitializer
{
    private static readonly ConcurrentDictionary<Type, WorkerInitInfo> Cache = new();

    internal static WorkerInitInfo GetInitInfo(Type workerType)
    {
        if (workerType == null)
            throw new ArgumentNullException(nameof(workerType));

        if (!Cache.TryGetValue(workerType, out var info))
        {
            info = Initialize(workerType);
            Cache.TryAdd(workerType, info);
        }

        return info;
    }

    internal static WorkerInitInfo GetInitInfo(IWorker worker)
    {
        if (worker == null)
            throw new ArgumentNullException(nameof(worker));

        return GetInitInfo(worker.GetType());
    }

    private static WorkerInitInfo Initialize(Type workerType)
    {
        var info = new WorkerInitInfo();
        var fields = new List<FieldInfo>();
        GatherWorkerFields(workerType, fields);

        info.SyncMemberFields = fields.ToArray();
        info.SyncMemberNames = new string[info.SyncMemberFields.Length];
        info.SyncMemberNonpersistent = new bool[info.SyncMemberFields.Length];
        info.SyncMemberNondrivable = new bool[info.SyncMemberFields.Length];
        info.SyncMemberDontCopy = new bool[info.SyncMemberFields.Length];
        info.DefaultValues = new object[info.SyncMemberFields.Length];
        info.SyncMemberNameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < info.SyncMemberFields.Length; i++)
        {
            var field = info.SyncMemberFields[i];

            info.SyncMemberNonpersistent[i] = field.GetCustomAttribute<NonPersistentAttribute>() != null;
            info.SyncMemberNondrivable[i] = field.GetCustomAttribute<NonDrivableAttribute>() != null;
            info.SyncMemberDontCopy[i] = field.GetCustomAttribute<DontCopyAttribute>() != null;

            string name = field.GetCustomAttribute<NameOverrideAttribute>()?.Name;
            if (string.IsNullOrEmpty(name))
            {
                name = field.Name;
                if (name.EndsWith("_Field", StringComparison.Ordinal) && name != "_Field")
                {
                    name = name[..name.LastIndexOf("_Field", StringComparison.Ordinal)];
                }
            }

            info.SyncMemberNames[i] = name;
            info.SyncMemberNameToIndex[name] = i;

            foreach (var oldName in field.GetCustomAttributes<OldNameAttribute>(inherit: false))
            {
                if (info.OldSyncMemberNames == null)
                {
                    info.OldSyncMemberNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                }

                if (!info.OldSyncMemberNames.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    info.OldSyncMemberNames.Add(name, list);
                }

                if (oldName.OldNames != null)
                {
                    foreach (var item in oldName.OldNames)
                    {
                        list.Add(item);
                    }
                }
            }

            if (field.GetCustomAttribute<DefaultValueAttribute>() is { } defaultValue)
            {
                info.DefaultValues[i] = defaultValue.Default;
            }
        }

        if (typeof(ComponentBase<>).IsAssignableFromGeneric(workerType))
        {
            var methodOrigin = workerType.FindGenericBaseClass(typeof(ComponentBase<>));
            var hasUpdateMethods = workerType.OverridesMethod("OnCommonUpdate", methodOrigin) |
                                   workerType.OverridesMethod("OnBehaviorUpdate", methodOrigin);

            if (typeof(Component).IsAssignableFrom(workerType))
            {
                hasUpdateMethods |= workerType.OverridesMethod("OnUpdate", typeof(Component)) |
                                    workerType.OverridesMethod("OnFixedUpdate", typeof(Component)) |
                                    workerType.OverridesMethod("OnLateUpdate", typeof(Component));
            }

            info.HasUpdateMethods = hasUpdateMethods;
            info.HasLinkedMethod = workerType.OverridesMethod("OnLinked", methodOrigin);
            info.HasUnlinkedMethod = workerType.OverridesMethod("OnUnlinked", methodOrigin);
            info.HasAudioUpdateMethod = workerType.OverridesMethod("OnAudioUpdate", methodOrigin);
            info.HasAudioConfigurationChangedMethod = workerType.OverridesMethod("OnAudioConfigurationChanged", methodOrigin);

            var eventCount = Enum.GetValues(typeof(World.WorldEvent)).Length;
            info.ReceivesWorldEvent = new bool[eventCount];
            for (int i = 0; i < eventCount; i++)
            {
                var worldEvent = (World.WorldEvent)i;
                if (workerType.OverridesMethod(worldEvent.ToString(), methodOrigin))
                {
                    info.ReceivesWorldEvent[i] = true;
                    info.ReceivesAnyWorldEvent = true;
                }
            }
        }

        info.SingleInstancePerSlot = workerType.GetCustomAttribute<SingleInstancePerSlotAttribute>(inherit: true) != null;
        info.DontDuplicate = workerType.GetCustomAttribute<DontDuplicateAttribute>(inherit: true) != null;
        info.PreserveWithAssets = workerType.GetCustomAttribute<PreserveWithAssetsAttribute>(inherit: true) != null;
        info.RegisterGlobally = workerType.GetCustomAttribute<GloballyRegisteredAttribute>(inherit: true) != null;
        info.DefaultUpdateOrder = workerType.GetCustomAttribute<DefaultUpdateOrderAttribute>()?.Order ?? 0;

        return info;
    }

    private static void GatherWorkerFields(Type workerType, List<FieldInfo> fields)
    {
        if (workerType.BaseType != typeof(Worker))
        {
            GatherWorkerFields(workerType.BaseType, fields);
        }

        var declared = workerType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => IsValidField(f, workerType));

        fields.AddRange(declared);
    }

    private static bool IsValidField(FieldInfo field, Type workerType)
    {
        if (!typeof(ISyncMember).IsAssignableFrom(field.FieldType))
        {
            return false;
        }

        if (field.FieldType.IsInterface)
        {
            return false;
        }

        if (!field.IsInitOnly)
        {
            return false;
        }

        if (field.FieldType.IsAbstract)
        {
            throw new Exception($"Field {field.Name} on Worker {workerType} is abstract type {field.FieldType}");
        }

        return true;
    }

    private static bool OverridesMethod(this Type type, string methodName, Type methodOrigin)
    {
        if (type == null || methodOrigin == null)
        {
            return false;
        }

        var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return method != null && method.DeclaringType != methodOrigin;
    }

    private static Type FindGenericBaseClass(this Type type, Type genericBase)
    {
        var current = type;
        while (current != null && current != typeof(object))
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericBase)
            {
                return current;
            }

            current = current.BaseType;
        }

        return null;
    }

    private static bool IsAssignableFromGeneric(this Type genericType, Type toCheck)
    {
        if (genericType == null || toCheck == null)
        {
            return false;
        }

        var current = toCheck;
        while (current != null && current != typeof(object))
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericType)
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }
}

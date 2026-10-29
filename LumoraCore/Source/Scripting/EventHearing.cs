// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What an event node's own Listen is handed, for an event the node hears by itself rather than through the
// script runtime's own sources: the object its source binding names (the script's own slot when it has none),
// and Raise, which queues the node's call with a payload the way every other event is queued. What Listen
// subscribes to it hands back through Until, and that is undone when the script stops or its graph changes.
//
//     [Node("World/Slots/Events/On Child Added")]
//     [SourcedEvent]
//     public sealed class OnChildAdded : EventNode<ChildAdded>
//     {
//         public static void Listen(EventHearing<ChildAdded> hearing) { ... hearing.Raise(new ChildAdded(child)); ... }
//     }
//
// Raise may be called from any handler on the world's thread; the call itself runs in the script phase. -xlinka
public sealed class EventHearing<T> where T : struct
{
    private readonly ScriptHostRuntime _runtime;
    private readonly string _node;
    private readonly List<Action> _undo;

    internal EventHearing(ScriptHostRuntime runtime, string node, object? source, List<Action> undo)
    {
        _runtime = runtime;
        _node = node;
        Source = source;
        _undo = undo;
    }

    // What the source binding holds as it is now, the script's own slot when there is none.
    public object? Source { get; }

    public Slot? Slot => Source as Slot;

    // The slot the script sits on.
    public Slot? Home => _runtime.Host.Slot;

    // Whether the script runs on every machine for whoever is there, rather than on the host.
    public bool IsLocal => _runtime.IsLocal;

    public void Raise(T payload) => _runtime.Enqueue(runner => runner.Raise(_node, payload));

    // The same, with the payload worked out when the call runs rather than now, for what can only be judged then;
    // null runs nothing.
    public void Raise(Func<T?> atCall)
        => _runtime.Enqueue(runner => atCall() is { } payload
            ? runner.Raise(_node, payload)
            : new ScriptCallResult(ScriptCallStatus.NoSuchEvent, _node, "nothing to hear after all", 0));

    // Runs when the script stops listening: take back whatever Listen subscribed.
    public void Until(Action undo) => _undo.Add(undo);
}

// Finds and calls the node's own Listen. One looked up per node class, once.
internal static class EventListeners
{
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _listen = new();

    public static bool TryListen(ScriptHostRuntime runtime, ScriptEventEntry entry, object? source, List<Action> undo)
    {
        var nodeType = entry.Overload.Definition.ClrType;
        var listen = _listen.GetOrAdd(nodeType, static type =>
        {
            var method = type.GetMethod("Listen", BindingFlags.Public | BindingFlags.Static);
            if (method == null || method.ReturnType != typeof(void))
                return null;
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType.IsGenericType
                && parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(EventHearing<>) ? method : null;
        });
        if (listen == null)
            return false;
        var hearingType = typeof(EventHearing<>).MakeGenericType(entry.PayloadType);
        if (listen.GetParameters()[0].ParameterType != hearingType)
            return false;
        var hearing = Activator.CreateInstance(hearingType, BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object?[] { runtime, entry.NodeId, source, undo }, null);
        try
        {
            listen.Invoke(null, new[] { hearing });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            runtime.Runner.Context.Warn(ScriptWarning.Fault, $"{entry.Overload.Definition.Name} could not listen: {ex.InnerException.Message}");
        }
        return true;
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Wasm;

namespace Lumora.Script;

// What a compile produced. On failure Module is null and the diagnostics say which nodes to fix; a graph
// that fails to compile does not run.
public sealed class CompileResult
{
    public string GraphHash { get; }
    public byte[]? Module { get; }
    public byte[] Manifest { get; }
    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; }
    public IReadOnlyList<NodeOverload> Imports { get; }
    public IReadOnlyList<ScriptEventEntry> Events { get; }
    public IReadOnlyList<ScriptTimerEntry> Timers { get; }
    public IReadOnlyList<ScriptVariableEntry> Variables { get; }
    public IReadOnlyList<ScriptBindingEntry> Bindings { get; }
    public IReadOnlyList<ScriptDisplayEntry> Displays { get; }

    // Source map tag to node id: a trap reports the tag of the node it happened in.
    public IReadOnlyList<string> TagNodes { get; }

    // One per wired flow exit, for the editor's live view.
    public IReadOnlyList<ScriptTraceEntry> Traces { get; init; } = Array.Empty<ScriptTraceEntry>();

    // One per Once, Toggle and Cooldown, for a live graph swap to carry over.
    public IReadOnlyList<ScriptMemoryEntry> FlowMemory { get; init; } = Array.Empty<ScriptMemoryEntry>();

    // One per await node, with the export each answer runs.
    public IReadOnlyList<ScriptAwaitEntry> Awaits { get; init; } = Array.Empty<ScriptAwaitEntry>();

    // One per Function node, with the export it lowers to.
    public IReadOnlyList<ScriptFunctionEntry> Functions { get; init; } = Array.Empty<ScriptFunctionEntry>();

    // One per Call Script Function node, with the import that starts its call.
    public IReadOnlyList<ScriptCallEntry> ScriptCalls { get; init; } = Array.Empty<ScriptCallEntry>();

    // One per drive node, in the order the drives function works them out.
    public IReadOnlyList<ScriptDriveEntry> Drives { get; init; } = Array.Empty<ScriptDriveEntry>();

    // When every drive is worked out from constants and stored values through plain-number math alone, the
    // stored values they read; the drives come out the same for as long as those hold the same lanes. Null
    // when any drive reads the world, the time, a handle or a remembered value.
    public IReadOnlyList<string>? DriveSources { get; init; }

    // Whether those drives also read the world through Fixed, Tree or Field reads, whose answers the instance
    // notes as the drives run and checks before running them again.
    public bool DrivesRead { get; init; }

    // Globals that keep a handle from one call to the next, so the thing it names is not let go between them.
    public IReadOnlyList<string> HandleRoots { get; init; } = Array.Empty<string>();

    // The module as the engine's validator decoded it when the compiler checked its own output. Nothing
    // writes a module once it is decoded, so every instance of this graph runs this one, from any thread.
    internal WasmModule? Decoded { get; init; }

    // Makes that module's native code on this thread, for a compile on a worker, so the script's first call
    // does not wait for it. False where modules run in the interpreter.
    public bool PrepareNative()
    {
        foreach (var entry in Events)
            WarmPayload(entry.PayloadType);
        return Decoded?.CompileNative() ?? false;
    }

    // The payload's lane writer, made here rather than as its first event is raised.
    private static void WarmPayload(Type payload)
    {
        if (payload.IsValueType)
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PayloadLanes<>).MakeGenericType(payload).TypeHandle);
    }

    // Whether the module runs as native code now, and why not when its code could not be made.
    public bool IsNative => Decoded?.IsNative ?? false;
    public string? NativeRefusal => Decoded?.NativeRefusal;

    internal CompileResult(string graphHash, byte[]? module, byte[] manifest, IReadOnlyList<CompileDiagnostic> diagnostics,
        IReadOnlyList<NodeOverload> imports, IReadOnlyList<ScriptEventEntry> events, IReadOnlyList<ScriptTimerEntry> timers,
        IReadOnlyList<ScriptVariableEntry> variables,
        IReadOnlyList<ScriptBindingEntry> bindings, IReadOnlyList<string> tagNodes,
        IReadOnlyList<ScriptDisplayEntry>? displays = null)
    {
        GraphHash = graphHash;
        Module = module;
        Manifest = manifest;
        Diagnostics = diagnostics;
        Imports = imports;
        Events = events;
        Timers = timers;
        Variables = variables;
        Bindings = bindings;
        TagNodes = tagNodes;
        Displays = displays ?? Array.Empty<ScriptDisplayEntry>();
    }

    public bool Success => Module != null && Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);

    public IEnumerable<CompileDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    // A compile that could not run at all, and why.
    public static CompileResult Unrun(string graphHash, string message)
        => Failed(graphHash, new[] { new CompileDiagnostic(DiagnosticSeverity.Error, null, message) });

    internal static CompileResult Failed(string graphHash, IReadOnlyList<CompileDiagnostic> diagnostics)
        => new(graphHash, null, Array.Empty<byte>(), diagnostics, Array.Empty<NodeOverload>(), Array.Empty<ScriptEventEntry>(),
            Array.Empty<ScriptTimerEntry>(), Array.Empty<ScriptVariableEntry>(), Array.Empty<ScriptBindingEntry>(), Array.Empty<string>());
}

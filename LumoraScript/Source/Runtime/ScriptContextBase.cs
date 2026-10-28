// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lumora.Script;

// What a running script instance carries into every host call: its handle table, and the hooks a host
// uses to scope, allow or count what nodes do. LumoraCore's ScriptContext derives from this; a node
// asks for that type as its first parameter and the registry recognises anything deriving from here.
//
// HANDLES. A script only ever holds an index into this table, never an object. Handle 0 is null.
// Handles made before the first call (bindings, constant strings) are pinned for the instance's life.
// Anything made during a call is released when the call ends unless a stored variable still holds it,
// so a counter formatting a new string on every press does not fill the table.
//
// WARNINGS. Nothing a script does past a threshold stops it. Each kind of warning is counted and logged
// the first time, then at most once every WarnSeconds with how many came in between. -xlinka
public abstract class ScriptContextBase
{
    // Past these a script is warned about: handles made in one call, text they hold between them, and
    // one text's length.
    public const int WarnHandles = 65536;
    public const long WarnTextBytes = 64L * 1024 * 1024;
    public const int WarnTextLength = 64 * 1024;

    public const double WarnSeconds = 10.0;

    private static readonly int WarningKinds = Enum.GetValues<ScriptWarning>().Length;

    private object?[] _handles = new object?[32];
    private bool[] _pinned = new bool[32];
    private int _next = 1;
    private readonly Stack<int> _free = new();
    private readonly Dictionary<object, int> _index = new(HandleKeyComparer.Instance);
    private bool _pinning = true;

    // Handles made since the instance started running and not released yet. A collect looks at these and
    // nothing else, so it costs what the call made, not the most the table ever held.
    private readonly List<int> _live = new();

    // The roots of one collect, kept between calls: a collect runs after every call a script makes.
    private readonly HashSet<int> _keep = new();
    private long _textBytes;

    private readonly int[] _warnings = new int[WarningKinds];
    private readonly int[] _unlogged = new int[WarningKinds];
    private readonly double[] _loggedAt = Never(WarningKinds);

    public int HandleCount => _index.Count;

    public long TextBytes => _textBytes;

    // How far the table's numbers reach, for whoever walks every handle.
    internal int HandleEnd => _next;

    // What the lists, dictionaries and JSON this instance makes may hold between them before it is warned.
    public long CollectionWarnBytes { get; init; } = ScriptCollectionBudget.WarnBytes;
    private ScriptCollectionBudget? _collections;
    public ScriptCollectionBudget Collections => _collections ??= new ScriptCollectionBudget(CollectionWarnBytes, this);

    public int Warnings { get; private set; }

    public string? LastWarning { get; private set; }

    public int WarningsOf(ScriptWarning kind) => _warnings[(int)kind];

    // What the instance's await nodes have out and the answers waiting for it; set when the instance links.
    public ScriptAwaits? Awaits { get; internal set; }

    // The call of a waiting Function the module said the next Delay or await starts in, taken by whichever
    // comes first; 0 for none.
    private int _word;

    internal void SetWord(int word) => _word = word;

    internal int TakeWord()
    {
        int word = _word;
        _word = 0;
        return word;
    }

    public int RefusalCount { get; private set; }

    // What the last refusal was, for whoever is working out why a script did nothing.
    public string? LastRefusal { get; private set; }

    // Why the action being called did not do what it was asked, the first reason it gave. Only asked for right
    // after an action whose failed exit is wired, and cleared right before it.
    private string? _failure;

    // Why the last call that left by its failed exit failed, kept until another one does. It outlives the turn
    // so a Display wired to it, which reads in a turn of its own, shows the failure being debugged. Empty
    // until something has failed that way.
    public string FailureReason { get; private set; } = string.Empty;

    public void Fail(string reason) => _failure ??= string.IsNullOrEmpty(reason) ? "it failed" : reason;

    // Set by the clear the module makes right before an action whose failed exit is wired, and taken by that
    // action's call: a bad input it throws on leaves by failed there instead of stopping the call.
    private bool _failWatched;

    internal void ClearFailure()
    {
        _failure = null;
        _failWatched = true;
    }

    internal bool TakeFailWatch()
    {
        bool watched = _failWatched;
        _failWatched = false;
        return watched;
    }

    internal bool TakeFailure()
    {
        if (_failure == null)
            return false;
        FailureReason = _failure;
        _failure = null;
        return true;
    }

    internal void ResetFailures()
    {
        _failure = null;
        _failWatched = false;
    }

    // Seconds on the clock timers are due by. The world runtime answers world time, so a paused or
    // throttled world's timers follow it.
    public virtual double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Log lines a second past which the script is warned; every line is written.
    public const int WarnLogLinesPerSecond = 10;

    private Random? _random;
    private double _logWindow = double.NegativeInfinity;
    private int _logLines;

    // One generator per instance. Unseeded it starts somewhere new every run; a seed makes a run repeat.
    public Random Random => _random ??= new Random();

    public void SeedRandom(int seed) => _random = new Random(seed);

    public void Log(string? text)
    {
        double now = Now;
        if (now - _logWindow >= 1.0)
        {
            _logWindow = now;
            _logLines = 0;
        }
        if (++_logLines == WarnLogLinesPerSecond + 1)
            Warn(ScriptWarning.Log, $"more than {WarnLogLinesPerSecond} log lines in a second");
        WriteLog(text ?? string.Empty);
    }

    // Where a log line goes. Nowhere, unless the host says.
    protected virtual void WriteLog(string text)
    {
    }

    public void Warn(ScriptWarning kind, string text)
    {
        int index = (int)kind;
        _warnings[index]++;
        Warnings++;
        LastWarning = text;
        double now = Now;
        if (now - _loggedAt[index] < WarnSeconds)
        {
            _unlogged[index]++;
            return;
        }
        _loggedAt[index] = now;
        int since = _unlogged[index];
        _unlogged[index] = 0;
        WriteWarning(since > 0 ? $"{text} ({since} more since the last)" : text);
    }

    // Where a warning the rate lets through goes. Nowhere, unless the host says.
    protected virtual void WriteWarning(string text)
    {
    }

    private static double[] Never(int count)
    {
        var times = new double[count];
        Array.Fill(times, double.NegativeInfinity);
        return times;
    }

    public int AddHandle(object? value)
    {
        if (value == null)
            return 0;
        if (_index.TryGetValue(value, out int existing))
            return existing;

        int handle;
        if (_free.Count > 0)
        {
            handle = _free.Pop();
        }
        else
        {
            handle = _next++;
            if (handle >= _handles.Length)
            {
                Array.Resize(ref _handles, _handles.Length * 2);
                Array.Resize(ref _pinned, _pinned.Length * 2);
            }
        }

        _handles[handle] = value;
        _pinned[handle] = _pinning;
        _index.Add(value, handle);
        if (value is string text)
            CountText(text);
        if (!_pinning)
        {
            _live.Add(handle);
            if (_live.Count == WarnHandles)
                Warn(ScriptWarning.Handles, $"more than {WarnHandles:N0} handles made in one call");
        }
        return handle;
    }

    private void CountText(string text)
    {
        bool under = _textBytes < WarnTextBytes;
        _textBytes += (long)text.Length * sizeof(char);
        if (under && _textBytes >= WarnTextBytes)
            Warn(ScriptWarning.TextHeld, $"more than {WarnTextBytes >> 20} MiB of text held at once");
        if (text.Length > WarnTextLength)
            Warn(ScriptWarning.LongText, $"a text {text.Length:N0} characters long");
    }

    public object? GetHandle(int handle)
        => handle > 0 && handle < _next ? _handles[handle] : null;

    // Binding slot n becomes handle n + 1, before anything else is made. An empty slot stays an empty
    // handle, and an object bound twice keeps both numbers. The module bakes these numbers in.
    internal void PinBindings(IReadOnlyList<object?> values)
    {
        if (_next != 1)
            throw new InvalidOperationException("bindings are pinned before the instance makes any handle");

        foreach (var value in values)
        {
            int handle = _next++;
            if (handle >= _handles.Length)
            {
                Array.Resize(ref _handles, Math.Max(_handles.Length * 2, handle + 1));
                Array.Resize(ref _pinned, _handles.Length);
            }
            _handles[handle] = value;
            _pinned[handle] = true;
            if (value != null && _index.TryAdd(value, handle) && value is string text)
                _textBytes += (long)text.Length * sizeof(char);
        }
    }

    public T? Resolve<T>(int handle) where T : class => GetHandle(handle) as T;

    // A handle's object as a node is handed it: nothing once it is gone, the same as an input nobody bound.
    // The table still holds the object, so a stored value keeps its handle, but no node acts on a thing the
    // world has already destroyed.
    public object? GetLive(int handle)
    {
        var value = GetHandle(handle);
        return value != null && IsGone(value) ? null : value;
    }

    // Whether an object has left the world it lived in. The base knows no world, so nothing is ever gone.
    protected internal virtual bool IsGone(object value) => false;

    // How a failure says an action's input has gone.
    protected internal virtual string GoneWords(string port, object value) => $"the {port} is gone";

    internal void EndPinning() => _pinning = false;

    // Whether a call made handles a collect could let go of.
    internal bool HasLiveHandles => _live.Count > 0;

    // Releases every handle that is neither pinned nor named by one of the roots.
    internal void Collect(ReadOnlySpan<int> roots)
    {
        if (_live.Count == 0)
            return;

        var keep = _keep;
        keep.Clear();
        foreach (int root in roots)
            keep.Add(root);

        int kept = 0;
        for (int i = 0; i < _live.Count; i++)
        {
            int handle = _live[i];
            if (keep.Contains(handle))
            {
                _live[kept++] = handle;
                continue;
            }
            var value = _handles[handle]!;
            if (value is string text)
                _textBytes -= (long)text.Length * sizeof(char);
            _index.Remove(value);
            _handles[handle] = null;
            _free.Push(handle);
        }
        _live.RemoveRange(kept, _live.Count - kept);
    }

    // Opened around every host call a node makes. The world runtime uses it to run the node as the
    // script's principal, so no node body can forget to.
    protected internal virtual IDisposable? EnterCall(NodeOverload overload) => null;

    // Asked once per import at link time.
    protected internal virtual bool AllowsCapability(Capability capability) => true;

    // What a For Each walks: how many items the host lists for a source, and one of them by index.
    // Nothing by default; a host that runs scripts says what its sources hold.
    protected internal virtual int EachCount(EachSource source, object? of) => 0;

    protected internal virtual object? EachItem(EachSource source, object? of, int index) => null;

    // A searching For Each: the host searches once with the node's query floats and hands back what it
    // found, which the loop then walks through EachCount and EachItem. Nothing by default.
    protected internal virtual object? EachQuery(EachSource source, ReadOnlySpan<float> query) => null;

    // A Call Script Function: run function, with these ports, in a script on target, and answer the ticket with
    // what it gave back or why it could not. Never run in this call: the other script runs it in its own turn.
    // The base knows no other scripts, so every call fails.
    protected internal virtual void CallScriptFunction(CallTicket ticket, object? target, string function, FunctionSignature signature,
        object?[] arguments)
        => ticket.Fail("this script cannot call other scripts here");

    // WHAT THE DRIVES READ. While a drives call records, each read node it calls notes what its answer hangs on,
    // as its Evaluation says: nothing for a Fixed read, the tree for a Tree read, its field for a Field read, and
    // for a Changing read that it cannot be told. If nothing noted has moved by the next frame, the drives would
    // work out what they hold already. -xlinka
    internal bool Recording { get; private set; }
    private bool _unsettled;
    private bool _readsTree;
    private long _treeStamp;
    private readonly List<(object Field, ulong Stamp)> _fieldsRead = new();

    // The tree is always watched: what the drives read hangs on it whether or not a Tree read noted it, since a
    // pure node can tell whether an object it is handed is gone.
    internal void BeginRecording()
    {
        Recording = true;
        _unsettled = false;
        _readsTree = true;
        _fieldsRead.Clear();
        _treeStamp = TreeStamp;
    }

    // Whether everything read can be told apart from a change: no Changing read, no refusal.
    internal bool EndRecording()
    {
        Recording = false;
        return !_unsettled;
    }

    internal void Note(Evaluation evaluation, object? watched)
    {
        switch (evaluation)
        {
            case Evaluation.Pure:
            case Evaluation.Fixed:
                return;
            case Evaluation.Tree:
                _readsTree = true;
                return;
            case Evaluation.Field when watched != null && FieldStamp(watched) is { } stamp:
                _fieldsRead.Add((watched, stamp));
                _readsTree = true;
                return;
            default:
                _unsettled = true;
                return;
        }
    }

    internal void NoteUnsettled() => _unsettled = true;

    // Whether everything the last recorded call read still answers the same.
    internal bool ReadsCurrent()
    {
        if (_readsTree && TreeStamp != _treeStamp)
            return false;
        foreach (var (field, stamp) in _fieldsRead)
        {
            if (FieldStamp(field) != stamp)
                return false;
        }
        return true;
    }

    // A number that moves whenever anything a Tree read could see changes. The base knows no world, so it never
    // moves.
    protected internal virtual long TreeStamp => 0;

    // A number that moves whenever this field changes, or null when that cannot be told. The base knows no fields.
    protected internal virtual ulong? FieldStamp(object field) => null;

    // A node turning its own call down without throwing, for a refusal that can come every frame: the
    // invoker answers it as it answers a thrown one.
    private string? _turnedDown;

    public void TurnDown(string reason) => _turnedDown = reason;

    internal string? TakeTurnedDown()
    {
        var reason = _turnedDown;
        _turnedDown = null;
        return reason;
    }

    // A node's write was refused by the host's permission gate. The node's outputs read as defaults and
    // the flow carries on: a refusal is an answer, not a crash.
    protected internal virtual void OnRefused(NodeOverload overload, UnauthorizedAccessException refusal)
    {
        RefusalCount++;
        LastRefusal = $"{overload.Definition.Name}: {refusal.Message}";
    }

    private sealed class HandleKeyComparer : IEqualityComparer<object>
    {
        public static readonly HandleKeyComparer Instance = new();

        public new bool Equals(object? x, object? y)
            => x is string a && y is string b ? string.Equals(a, b, StringComparison.Ordinal) : ReferenceEquals(x, y);

        public int GetHashCode(object obj)
            => obj is string s ? StringComparer.Ordinal.GetHashCode(s) : RuntimeHelpers.GetHashCode(obj);
    }
}

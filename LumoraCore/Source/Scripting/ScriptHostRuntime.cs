// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Components;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Components.Touch;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What a running ScriptHost holds: its runner, the events waiting for the script phase, its fault record,
// and its subscriptions. A Shared script's runs on the authority; a Local script's runs on every machine,
// for whoever is there. Events never run where they are raised: a touch control reacts inside its own
// author's scope, and a script started there would run with that scope instead of its own. They queue,
// and the scheduler runs them.
//
// A Local script hears what this machine can hear: presses on panel buttons and beams from whoever is
// here, not touch controls, whose reactions run on the authority; people coming and going by watching
// the user list, since this machine is told only about its own user.
//
// A script with drives works their values out every frame wherever it runs, and every other machine runs
// a drives-only copy that hears nothing and only does that. -xlinka
internal sealed class ScriptHostRuntime : IDisposable
{
    public const int WarnQueue = 256;
    public const double DisplaySeconds = 0.1;

    // How long a node that faulted stays marked in the open graph after its last fault.
    public const double FaultMarkSeconds = 8.0;
    private const int MaxFaultText = 90;

    private readonly Queue<Func<ScriptRunner, ScriptCallResult>> _queue = new();
    private readonly List<(int Timer, int Word)> _dueTimers = new();
    private readonly List<TouchControl> _controls = new();
    private readonly List<(Slot Slot, ScriptEventEntry Entry)> _listeners = new();
    private readonly List<Slot> _watched = new();
    private readonly Dictionary<string, ScriptGraphNode> _shownNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Message, double At)> _faultMarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _tracesByNode = new(StringComparer.Ordinal);
    private readonly System.Text.StringBuilder _traceText = new();
    private int[] _publishedTraces = Array.Empty<int>();
    private readonly List<(string NodeId, float Seconds, double Due, int Count)> _intervals = new();
    private double _nextDisplays = double.NegativeInfinity;
    private readonly double _displayPhase = (System.Threading.Interlocked.Increment(ref _started) & 7) / 8.0 * DisplaySeconds;
    private static int _started;
    private readonly Action<TouchControl, TouchResponse, TouchContact> _onResponded;
    private readonly Action<Slot, Component> _onComponentsChanged;
    private readonly ScriptEventSources _sources;
    private readonly long _memory;
    private readonly HashSet<User> _knownUsers = new();
    private readonly List<User> _userScratch = new();
    private readonly bool _tracksUsers;
    private bool _disposed;
    private int _due;

    public ScriptHost Host { get; }
    public ScriptRunner Runner { get; }
    public string GraphHash { get; }
    public IReadOnlyList<object?> Bindings { get; }
    public bool WantsUpdate { get; }
    public ScriptRuntimeStats Stats { get; } = new();

    public int QueuedEvents => _queue.Count;

    public ScriptPrincipal Principal => Runner.Context.Principal;

    // What this script took of the frame running now; the scheduler reads it as the frame ends.
    public double FrameMs { get; set; }

    // The events and answers waiting as this frame's phase began are what runs this frame. True when any are.
    internal bool TakeDue()
    {
        _due = _queue.Count + Runner.Instance.WaitingAnswers;
        return _due > 0;
    }

    internal bool TryTakeDue()
    {
        if (_due <= 0 || !HasQueued)
            return false;
        _due--;
        return true;
    }

    public bool IsLocal { get; }

    // Only the drives: a copy on a machine the script's events do not run on.
    public bool DrivesOnly { get; }

    private readonly ScriptDriveApplier? _drives;

    public bool DrivesHere => _drives != null;

    public void ApplyDrives(double now) => _drives?.Apply(now);

    // Working memory this script holds, counted with its author's other scripts.
    public long MemoryHeld => _memory;

    // A deleted host is marked at once and torn down at the end of the frame; nothing runs in between.
    public bool CanRun => !_disposed && !Runner.Stopped && !Host.IsDestroyed;

    // Answers to the script's awaits wait in its instance until they run, behind the queued events, so a
    // live graph swap hands the ones not yet run to the new instance instead of dropping them here.
    public bool HasQueued => _queue.Count > 0 || Runner.Instance.WaitingAnswers > 0;

    public ScriptEventSources Sources => _sources;

    public bool HasDisplays => !IsLocal && Runner.Compiled.Displays.Count > 0;

    // What the open graph shows of a Shared script, its Displays, the flow along its wires and the node it
    // last faulted in, is worked out only while someone could be looking: while the graph is open. A Local
    // script's view reads this machine's runtime directly.
    //
    // Looked for ten times a second, not every frame: finding the open graph walks the host's children, and a
    // world of seven hundred closed scripts walked them all on every frame to find nothing. Each runtime
    // looks at its own point in the tenth of a second, so the looks spread over the frames.
    public bool DisplaysDue(double now)
    {
        if (IsLocal || now < _nextDisplays)
            return false;
        if (ScriptGraphEditing.FindSpace(Host) is { IsShown: true })
            return true;
        _nextDisplays = double.IsNegativeInfinity(_nextDisplays) ? now + _displayPhase : now + DisplaySeconds;
        return false;
    }

    // How many times flow has left by each wired exit, for a Local script's view on this machine.
    public int ReadTrace(int index) => Runner.Instance.ReadTrace(index);

    // Why this runtime last faulted in a node, while that is recent.
    public bool TryGetFaultMark(string nodeId, double now, out string message)
    {
        message = string.Empty;
        if (!_faultMarks.TryGetValue(nodeId, out var mark) || now - mark.At > FaultMarkSeconds)
            return false;
        message = mark.Message;
        return true;
    }

    private ScriptHostRuntime(ScriptHost host, ScriptRunner runner, string graphHash, IReadOnlyList<object?> bindings, long memory, bool local,
        bool drivesOnly)
    {
        Host = host;
        Runner = runner;
        GraphHash = graphHash;
        Bindings = bindings;
        _memory = memory;
        IsLocal = local;
        DrivesOnly = drivesOnly;
        if (runner.Instance.HasDrives)
            _drives = new ScriptDriveApplier(this);
        if (local && !drivesOnly && (runner.Instance.Handles(typeof(UserJoined)) || runner.Instance.Handles(typeof(UserLeft))) && host.World is { } world)
        {
            _tracksUsers = true;
            world.GetAllUsers(_userScratch);
            foreach (var user in _userScratch)
            {
                if (!user.IsDestroyed)
                    _knownUsers.Add(user);
            }
        }
        WantsUpdate = !drivesOnly && runner.Instance.Handles(typeof(FrameTick));
        var traces = runner.Compiled.Traces;
        for (int i = 0; i < traces.Count; i++)
        {
            if (!_tracesByNode.TryGetValue(traces[i].NodeId, out var owned))
                _tracesByNode[traces[i].NodeId] = owned = new List<int>();
            owned.Add(i);
        }
        double start = host.World?.Time.TotalTime ?? 0.0;
        foreach (var entry in drivesOnly ? Array.Empty<ScriptEventEntry>() : runner.Instance.Events)
        {
            if (entry.IntervalSeconds > 0f)
                _intervals.Add((entry.NodeId, entry.IntervalSeconds, start + entry.IntervalSeconds, 0));
        }
        _sources = new ScriptEventSources(this);
        _onResponded = OnResponded;
        _onComponentsChanged = (_, component) =>
        {
            if (component is TouchControl)
                HookControls();
        };
    }

    // A shader property is bound as itself and run as the field its port takes (ShaderPropertyFields).
    private static IReadOnlyList<object?> AsDeclared(CompileResult compiled, IReadOnlyList<object?> bindings)
    {
        object?[]? adapted = null;
        for (int i = 0; i < bindings.Count && i < compiled.Bindings.Count; i++)
        {
            var value = ShaderPropertyFields.Adapt(bindings[i], compiled.Bindings[i].Type.ClrType);
            if (ReferenceEquals(value, bindings[i]))
                continue;
            if (adapted == null)
            {
                adapted = new object?[bindings.Count];
                for (int j = 0; j < bindings.Count; j++)
                    adapted[j] = bindings[j];
            }
            adapted[i] = value;
        }
        return adapted ?? bindings;
    }

    // Replacing is the script this one takes over from, which gives its memory back once this one runs.
    // Cached says the compile was waiting in the cache when the host asked for it.
    // Local is where it runs: on this machine, not the host. An Author script on its author's machine runs
    // here like a Local one but writes as its author.
    public static ScriptHostRuntime Start(ScriptHost host, CompileResult compiled, bool cached, IReadOnlyList<object?> bindings,
        ScriptPrincipal principal, ScriptHostRuntime? replacing = null, bool local = false, bool drivesOnly = false, bool authorMachine = false)
    {
        ScriptEnvironment.TryGetCompileRecord(compiled.GraphHash, out var record);
        bindings = AsDeclared(compiled, bindings);
        var runner = ScriptRunner.Start(host.World, compiled, record, cached, principal, bindings, self: host.Slot,
            local: local && !authorMachine, authorMachine: authorMachine);
        runner.Context.Host = host;
        long memory = runner.Instance.MemorySize;
        long freed = replacing != null && replacing.Principal.AuthorKey == principal.AuthorKey ? replacing.MemoryHeld : 0;
        if (host.World.Scripts.Ledger.Hold(principal, memory, freed))
            runner.Context.Warn(ScriptWarning.Memory,
                $"its author's scripts hold more than {ScriptAuthorLedger.WarnMemoryBytes >> 20} MiB of working memory between them");
        var runtime = new ScriptHostRuntime(host, runner, runner.Compiled.GraphHash, bindings, memory, local, drivesOnly);
        var stats = runtime.Stats;
        stats.Nodes = compiled.TagNodes.Count;
        stats.ModuleBytes = runner.Compiled.Module?.Length ?? 0;
        stats.CompileMs = runner.CompileMs;
        stats.CompileCached = runner.CompileCached;
        stats.LoadMs = runner.LoadMs;
        stats.StartedAt = host.World.Time.TotalTime;
        if (!drivesOnly)
            runtime.Resubscribe();
        host.World.Scripts.Add(runtime);
        return runtime;
    }

    public void Enqueue(Func<ScriptRunner, ScriptCallResult> call)
    {
        if (!CanRun || DrivesOnly)
            return;
        _queue.Enqueue(call);
        if (_queue.Count == WarnQueue + 1)
            Runner.Context.Warn(ScriptWarning.Queue, $"more than {WarnQueue} events waiting");
    }

    public Func<ScriptRunner, ScriptCallResult> Dequeue()
    {
        if (_queue.Count > 0)
            return _queue.Dequeue();
        if (!Runner.Instance.TryTakeAnswer(out var answer))
            return static _ => new ScriptCallResult(ScriptCallStatus.NoSuchEvent, null, "nothing waiting", 0);
        return runner => runner.RunAnswer(answer);
    }

    // Queues every Delay and every Every N Seconds that is due, behind whatever events are already waiting.
    public void QueueDueTimers(double now)
    {
        if (!CanRun || DrivesOnly)
            return;
        _sources.PollTriggers(now);
        _sources.FollowBeams(now);
        _sources.PollSynced();
        _sources.PollStays();
        _sources.PollWorld();
        if (_tracksUsers)
            WatchUsers();
        for (int i = 0; i < _intervals.Count; i++)
        {
            var (node, seconds, due, count) = _intervals[i];
            if (now < due)
                continue;
            int fired = count + 1;
            // A frame that ran past the next beat as well starts the beat again from now, so the late fire is
            // not followed by another a frame later. -xlinka
            double next = due + seconds;
            _intervals[i] = (node, seconds, next > now ? next : now + seconds, fired);
            Enqueue(runner => runner.Raise(node, new IntervalElapsed(fired)));
        }
        if (Runner.Instance.PendingTimers == 0)
            return;
        _dueTimers.Clear();
        Runner.Instance.TakeDueTimers(now, _dueTimers);
        foreach (var (timer, word) in _dueTimers)
            Enqueue(runner => runner.RunTimer(timer, word));
    }

    public void RecordFault(double now, in ScriptCallResult result)
    {
        if (result.NodeId != null)
        {
            string message = result.Message ?? result.Status.ToString();
            _faultMarks[result.NodeId] = (message.Length > MaxFaultText ? message[..(MaxFaultText - 3)] + "..." : message, now);
            // At once, not with the next publish.
            if (!IsLocal && ScriptGraphEditing.FindSpace(Host) is { IsShown: true } space)
            {
                foreach (var node in space.Nodes)
                {
                    if (node.NodeId.Value == result.NodeId)
                        node.ShowFault(_faultMarks[result.NodeId].Message);
                }
            }
        }
        Host.RuntimeFaulted(this, result.ToString());
        Runner.Context.Warn(ScriptWarning.Fault, result.ToString());
    }

    // Each press event listens on its bound source, else on the slot the script sits on. The controls on
    // those slots are followed as they come and go.
    private void Resubscribe()
    {
        Unsubscribe();
        var home = Host.Slot;
        if (_disposed || home == null || home.IsDestroyed)
            return;

        foreach (var entry in Runner.Instance.Events)
        {
            if (entry.PayloadType != typeof(ButtonPressed) && entry.PayloadType != typeof(ButtonReleased)
                && entry.PayloadType != typeof(ControlTouched) && !ScriptEventSources.IsPressStep(entry))
                continue;
            if (SourceOf(entry) is not { IsDestroyed: false } slot)
                continue;
            _listeners.Add((slot, entry));
            if (!_watched.Contains(slot))
                _watched.Add(slot);
        }
        for (int i = 0; !IsLocal && i < _watched.Count; i++)
        {
            var slot = _watched[i];
            slot.OnComponentAdded += _onComponentsChanged;
            slot.OnComponentRemoved += _onComponentsChanged;
            foreach (var control in slot.GetComponents<TouchControl>())
            {
                control.Responded += _onResponded;
                _controls.Add(control);
            }
        }
        _sources.Subscribe(Runner.Instance.Events, BoundSource);
    }

    // The touch controls on the watched slots, hooked again as they come and go. Only these: starting every
    // source over would hear all that is already inside a trigger or under a beam come in again. -xlinka
    private void HookControls()
    {
        foreach (var control in _controls)
            control.Responded -= _onResponded;
        _controls.Clear();
        if (_disposed || IsLocal)
            return;
        foreach (var slot in _watched)
        {
            if (slot.IsDestroyed)
                continue;
            foreach (var control in slot.GetComponents<TouchControl>())
            {
                control.Responded += _onResponded;
                _controls.Add(control);
            }
        }
    }

    private void Unsubscribe()
    {
        foreach (var control in _controls)
            control.Responded -= _onResponded;
        _controls.Clear();
        for (int i = 0; !IsLocal && i < _watched.Count; i++)
        {
            _watched[i].OnComponentAdded -= _onComponentsChanged;
            _watched[i].OnComponentRemoved -= _onComponentsChanged;
        }
        _watched.Clear();
        _listeners.Clear();
        _sources.Unsubscribe();
    }

    // What an event's source binding holds as it is, the script's own slot when it has none.
    private object? BoundSource(ScriptEventEntry entry)
    {
        if (entry.SourceBinding < 0)
            return Host.Slot;
        var bound = entry.SourceBinding < Bindings.Count ? Bindings[entry.SourceBinding] : null;
        return bound is Component component && entry.PayloadType != typeof(FieldChanged) ? component.Slot : bound;
    }

    private Slot? SourceOf(ScriptEventEntry entry)
    {
        if (entry.SourceBinding < 0)
            return Host.Slot;
        var bound = entry.SourceBinding < Bindings.Count ? Bindings[entry.SourceBinding] : null;
        return bound switch
        {
            Slot slot => slot,
            Component component => component.Slot,
            _ => null,
        };
    }

    // What the Displays and Pulses show, onto their nodes in the open graph, with how often flow left each
    // node by each wire and the node the script lately faulted in. A Display that fails to work out says
    // so on its own card; it is not held against the script, which it only watches.
    public void PublishDisplays(double now)
    {
        _nextDisplays = now + DisplaySeconds;
        if (ScriptGraphEditing.FindSpace(Host) is not { } space)
            return;
        _shownNodes.Clear();
        foreach (var node in space.Nodes)
            _shownNodes[node.NodeId.Value ?? string.Empty] = node;

        var instance = Runner.Instance;
        foreach (var entry in Runner.Compiled.Displays)
        {
            if (!_shownNodes.TryGetValue(entry.NodeId, out var node))
                continue;
            string text;
            if (entry.CountsRuns)
                text = instance.ReadVariable(entry.NodeId) is int runs ? (runs == 1 ? "ran once" : $"ran {runs} times") : "ran 0 times";
            else if (!entry.Refreshed && !entry.Captured)
                text = "not shown";
            else if (entry.Refreshed && Runner.RefreshDisplay(entry.NodeId) is { Ok: false } failed)
                text = "error: " + (failed.Message ?? failed.Status.ToString());
            else
                text = ShowText(instance.ReadVariable(entry.NodeId));
            node.Show(text);
        }

        PublishTraces();
        foreach (var (id, node) in _shownNodes)
            node.ShowFault(TryGetFaultMark(id, now, out var fault) ? fault : string.Empty);
    }

    // Each node's exit counts, for the nodes whose counts moved since the last look.
    private void PublishTraces()
    {
        var traces = Runner.Compiled.Traces;
        if (_publishedTraces.Length != traces.Count)
        {
            _publishedTraces = new int[traces.Count];
            Array.Fill(_publishedTraces, -1);
        }
        foreach (var (id, owned) in _tracesByNode)
        {
            bool moved = false;
            foreach (int index in owned)
                moved |= Runner.Instance.ReadTrace(index) != _publishedTraces[index];
            if (!moved || !_shownNodes.TryGetValue(id, out var node))
                continue;
            _traceText.Clear();
            foreach (int index in owned)
            {
                int count = Runner.Instance.ReadTrace(index);
                _publishedTraces[index] = count;
                if (_traceText.Length > 0)
                    _traceText.Append(',');
                _traceText.Append(traces[index].Exit).Append(':').Append(count.ToString(CultureInfo.InvariantCulture));
            }
            node.ShowTrace(_traceText.ToString());
        }
    }

    private static string ShowText(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        float single => single.ToString("0.###", CultureInfo.InvariantCulture),
        double precise => precise.ToString("0.#####", CultureInfo.InvariantCulture),
        string text => text.Length > 60 ? text[..57] + "..." : text,
        Lumora.Script.ScriptJson json => Lumora.Script.ScriptJsonWriter.Preview(json, 60),
        Slot slot => slot.IsDestroyed ? "(gone)" : string.IsNullOrEmpty(slot.Name.Value) ? "(slot)" : slot.Name.Value,
        User user => user.IsDestroyed ? "(gone)" : user.UserName.Value ?? "(user)",
        Component component => component.IsDestroyed ? "(gone)" : component.GetType().Name,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // A message from another script of the same author: to this Shared script on the host, or to every
    // machine's copy of this Local one.
    public void EnqueueMessage(string message, float value, User? from)
    {
        foreach (var entry in Runner.Instance.Events)
        {
            if (entry.PayloadType != typeof(MessageReceived))
                continue;
            var node = entry.NodeId;
            Enqueue(runner => runner.Raise(node, new MessageReceived(message, value, from)));
        }
    }

    private void WatchUsers()
    {
        var world = Host.World;
        if (world == null)
            return;
        world.GetAllUsers(_userScratch);
        foreach (var user in _userScratch)
        {
            // In once their body and name are: a user this machine has only just heard of may have neither.
            if (!user.IsDestroyed && ScriptReads.RootOf(user)?.Slot != null && !string.IsNullOrEmpty(user.UserName.Value) && _knownUsers.Add(user))
            {
                var joined = user;
                Enqueue(runner => runner.Raise(new UserJoined(joined)));
            }
        }
        if (_knownUsers.Count == _userScratch.Count)
            return;
        foreach (var user in new List<User>(_knownUsers))
        {
            if (user.IsDestroyed || !_userScratch.Contains(user))
            {
                _knownUsers.Remove(user);
                var left = user;
                Enqueue(runner => runner.Raise(new UserLeft(left)));
            }
        }
    }

    // A call from another script of the same author to one of this script's Functions, run in this script's
    // turn, as this script, and answered on the caller's ticket. One whose caller stopped or was deleted since
    // runs nothing; one still waiting when this script stops is answered failed.
    private readonly List<(CallTicket Ticket, string Function, string Where)> _inbound = new();

    public void EnqueueFunctionCall(ScriptContext caller, CallTicket ticket, string function, object?[] arguments, string where)
    {
        if (!CanRun || DrivesOnly)
        {
            ticket.Fail($"the script on {where} is not running");
            return;
        }
        var waiting = (ticket, function, where);
        _inbound.Add(waiting);
        Enqueue(runner =>
        {
            _inbound.Remove(waiting);
            if (ticket.IsAnswered || ticket.Cancelled.IsCancellationRequested || caller.Host is { IsDestroyed: true })
            {
                ticket.Abandon();
                return new ScriptCallResult(ScriptCallStatus.NoSuchEvent, null, "its caller is gone", 0);
            }
            // Answered at once, or by a Function that waits once its call returns.
            return runner.CallFunction(function, arguments, ticket, $"{function} on {where}");
        });
    }

    // A press on a Call node's card, when the running graph has that Call node.
    public void EnqueueCall(User? caller, string nodeId)
    {
        foreach (var entry in Runner.Instance.Events)
        {
            if (entry.NodeId != nodeId || entry.PayloadType != typeof(CallPressed))
                continue;
            Enqueue(runner => runner.Raise(nodeId, new CallPressed(caller)));
            return;
        }
    }

    // A press from a UI button on the script's own slot, which the host hears through its own receiver.
    public void EnqueuePress(User? presser, float3 point)
    {
        foreach (var (slot, entry) in _listeners)
        {
            if (entry.PayloadType != typeof(ButtonPressed) || !ReferenceEquals(slot, Host.Slot))
                continue;
            var node = entry.NodeId;
            Enqueue(runner => runner.Raise(node, new ButtonPressed(presser, point)));
        }
    }

    private void OnResponded(TouchControl control, TouchResponse response, TouchContact contact)
    {
        var presser = contact.User;
        var point = contact.Point;
        foreach (var (slot, entry) in _listeners)
        {
            if (!ReferenceEquals(slot, control.Slot))
                continue;
            var node = entry.NodeId;
            if (response == TouchResponse.Pressed && entry.PayloadType == typeof(ButtonPressed))
                Enqueue(runner => runner.Raise(node, new ButtonPressed(presser, point)));
            else if (response == TouchResponse.Released && entry.PayloadType == typeof(ButtonReleased))
                Enqueue(runner => runner.Raise(node, new ButtonReleased(presser, point)));
            else if (response is TouchResponse.Pressed or TouchResponse.Pressing or TouchResponse.Released
                && entry.PayloadType == typeof(ControlTouched))
            {
                var touched = new ControlTouched(presser, response == TouchResponse.Pressed, response == TouchResponse.Pressing,
                    response == TouchResponse.Released, point, contact.Tip, contact.Hand, contact.Kind == TouchProbeKind.Fingertip);
                Enqueue(runner => runner.Raise(node, touched));
            }
            else if (ScriptEventSources.IsPressStep(entry))
            {
                var (local, normalized) = PointsOn(control, point);
                if (response == TouchResponse.Pressed && entry.PayloadType == typeof(PressBegan))
                    Enqueue(runner => runner.Raise(node, new PressBegan(presser, point, local, normalized)));
                else if (response == TouchResponse.Pressing && entry.PayloadType == typeof(PressHeld))
                    Enqueue(runner => runner.Raise(node, new PressHeld(presser, point, local, normalized)));
                else if (response == TouchResponse.Released && entry.PayloadType == typeof(PressEnded))
                    Enqueue(runner => runner.Raise(node, new PressEnded(presser, point, local, normalized)));
            }
        }
    }

    // Where a touch is on a control: in the control's own space, and across the box collider it is touched
    // through when it has one, from its lower left corner to its upper right as on a panel button.
    private static (float2 Local, float2 Normalized) PointsOn(TouchControl control, float3 point)
    {
        var slot = control.Slot;
        if (slot == null || slot.IsDestroyed)
            return (float2.Zero, float2.Zero);
        var local = slot.GlobalPointToLocal(point);
        var box = slot.GetComponent<BoxCollider>();
        var size = box?.Size.Value ?? float3.Zero;
        var from = local - (box?.Offset.Value ?? float3.Zero) + size * 0.5f;
        return (new float2(local.x, local.y), new float2(size.x != 0f ? from.x / size.x : 0f, size.y != 0f ? from.y / size.y : 0f));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.Clear();
        foreach (var (ticket, function, where) in _inbound)
            ticket.Fail($"the script on {where} stopped before {function} ran");
        _inbound.Clear();
        Unsubscribe();
        _sources.Release();
        if (Runner.Context.IsLocal)
            Runner.Context.DropLocalMade();
        Runner.Instance.StopAwaits();
        var scripts = Host.World?.ScriptsIfAny;
        scripts?.Remove(this);
        scripts?.Ledger.Release(Principal, _memory);
        Runner.Stop();
    }
}

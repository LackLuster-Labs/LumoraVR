// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Scripting;
using Lumora.Script;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Scripting;

// Runs a script on the object it sits on.
//
// Graph points at the packed graph. Bindings are the world objects the graph reaches, by position: the
// graph names binding n, never a RefID, so duplicating, saving and loading the object remaps them like
// any other reference. Only the graph travels; the authority compiles its own module from it.
//
// A Shared script runs on the authority AS ITS AUTHOR: the world itself for a world-built object, else
// the user whose byte minted this component, pinned by identity when the component wakes. It never
// runs as anyone else. If that author leaves, the script stops for good, even if the byte later
// belongs to someone new. A user's script also needs the Script domain on their role.
//
// What decides what a user's script does has to be the author's own doing: the graph reference, the
// provider behind it and its URL, and every binding. Someone else allowed to edit this component could
// otherwise hand the author's rights a graph or a target of their own, so an edit by anyone else stops
// the script until the author writes it again. Packing someone else's script makes a new host that
// runs as whoever packed it.
//
// Presses reach On Pressed from a touch control on the same slot, which already relays to the
// authority, and from a UI button on the same slot, which fires on the presser's peer and is carried
// over by the presser's ScriptPressRelay.
//
// Status is written by the authority for everyone's inspector and is host-only on the wire.
//
// A Local script runs on every machine, the host's included, as its author, for whoever is at each. The
// host still decides whether it may run at all, with the same checks a Shared script gets, and says so
// in LocalApproval: the hash of the graph every machine may run, or nothing. Each machine then runs that
// graph once it has it, and keeps how it is going in LocalStatus, for its own inspector only. A machine
// that cannot compile the graph says it needs a newer build. What a Local script may change is in
// ScriptWrites; a Shared script of the same author reaches it through the inbox.
//
// Graphs compile on a worker. Until the compile lands the host keeps whatever it was running, and the
// scheduler brings it back here in the script phase once it has.
//
// Switching off the object a script sits on, or its open graph's slot, stops the script, the same as
// disabling the host; switching it back on starts it fresh.
//
// An Author script runs on its author's own machine, approved by the host in LocalApproval like a Local one,
// with the host's checks; it writes as its author and reads their input. A world script's author is the
// world, so one runs on the host as a Shared one would.
//
// Drives run on every machine. The runtime that owns the script on the host keeps one drive link per drive
// node (ScriptDriveLinks), and the host approves the graph in LocalApproval for every machine not running
// the whole script to run a drives-only copy of. -xlinka
[ComponentCategory("Utility/Scripting")]
public sealed class ScriptHost : Component, IButtonPressReceiver, ICustomInspectorUI, IInspectorActionHandler
{
    public readonly AssetRef<ScriptGraphAsset> Graph = new();
    public readonly SyncRefList<IWorldElement> Bindings = new();
    public readonly Sync<ScriptMode> Mode = new();
    public readonly Sync<string> Status = new();

    // Where the open graph stood when it was last packed, in this object's own space; a zero turn, not the
    // identity a rotation field starts at, is a graph never opened. See ScriptGraphPlacement.
    [HideInInspector]
    public readonly Sync<float3> GraphAt = new();
    [HideInInspector]
    public readonly Sync<floatQ> GraphTurn = new(default(floatQ));

    // Host-only and never saved: the host's say on a Local script, and what a Shared script of the same
    // author last sent it. The count comes last, so its change arrives with the text and number.
    [NonPersistent]
    public readonly Sync<string> LocalApproval = new();
    [NonPersistent]
    public readonly Sync<string> InboxMessage = new();
    [NonPersistent]
    public readonly Sync<float> InboxValue = new();
    [NonPersistent]
    public readonly Sync<int> Inbox = new();

    // On a copy as it is made, by the machine that made it, until the script running it has heard so. Only
    // the copy carries it; the machine that made the copy also keeps its own word for a script it runs itself.
    [NonPersistent]
    [DontCopy]
    [HideInInspector]
    public readonly Sync<bool> Duplicated = new();
    private bool _duplicatedHere;

    // Host-only and never saved: what the Shared script costs on the host, milliseconds a frame over the
    // last second and its compile there. Written only while a panel on another machine asks for them.
    [NonPersistent]
    public readonly Sync<float> HostMs = new();
    [NonPersistent]
    public readonly Sync<float> HostCompileMs = new();

    // Host-only and never saved: what the Shared script keeps here for every machine to read, by name.
    // A fresh start clears it; a live graph swap keeps it.
    [NonPersistent]
    public readonly SyncList<ScriptSyncedValue> Synced = new();

    private ScriptHostRuntime? _runtime;
    private ScriptGraph? _runningGraph;
    private ScriptPrincipal? _pinned;

    // Set once, for good: why this host has no author it may run as.
    private string? _orphaned;

    // What the running script is doing, and its last fault since it started, which stays on the status
    // however often the host decides again that it should keep running.
    private string _running = string.Empty;
    private string? _lastFault;

    public bool IsRunning => _runtime is { CanRun: true };

    private ScriptHostRuntime? _localRuntime;
    private int _seenInbox;
    private Slot? _watched;

    // Why this machine's last Edit graph or Pack press from the inspector did nothing, for its inspector.
    private string? _lastAction;

    // The compile this host is waiting on, Shared or Local, by graph hash.
    private System.Threading.Tasks.Task<CompileResult>? _compiling;
    private string? _compilingHash;

    // How the Local script is doing on this machine, for this machine's inspector.
    public string LocalStatus { get; private set; } = string.Empty;

    public bool IsRunningHere => _localRuntime is { CanRun: true };

    // Refusals the running script has met at the gate, and the last of them, here and for a Local script
    // on this machine.
    public int Refusals => _runtime?.Runner.Context.RefusalCount ?? 0;

    public string? LastRefusal => _runtime?.Runner.Context.LastRefusal;

    public string? LastRefusalHere => _localRuntime?.Runner.Context.LastRefusal;

    // The Shared runtime on the host, the Local one on this machine, for the diagnostics panel.
    internal ScriptHostRuntime? SharedRuntime => _runtime;

    internal ScriptHostRuntime? LocalRuntime => _localRuntime;

    public override void OnAwake()
    {
        base.OnAwake();
        Status.MarkHostOnly();
        LocalApproval.MarkHostOnly();
        InboxMessage.MarkHostOnly();
        InboxValue.MarkHostOnly();
        Inbox.MarkHostOnly();
        HostMs.MarkHostOnly();
        HostCompileMs.MarkHostOnly();
        Synced.MarkHostOnly();

        // Pinned here: on the authority a user's component wakes while its sender is still in the world.
        if (World?.IsAuthority == true)
            Pin();

        _watched = Slot;
        if (_watched != null)
            _watched.ActiveChanged += OnSlotActiveChanged;
    }

    private void OnSlotActiveChanged(Slot slot)
    {
        if (World is { } world && !IsDestroyed && (world.IsAuthority || Mode.Value == ScriptMode.Local || _localRuntime != null))
            MarkChangeDirty();
    }

    public override void OnStart()
    {
        base.OnStart();
        // Every machine keeps its hosts with the scheduler: a Local script runs everywhere.
        World?.Scripts.Register(this);
    }

    public override void OnChanges()
    {
        base.OnChanges();
        Reconcile();
        TakeInbox();
    }

    public override void OnDuplicate()
    {
        base.OnDuplicate();
        _duplicatedHere = true;
        using (World?.DataModelPermissions?.EnterSystemBypass())
            Duplicated.Value = true;
    }

    public override void OnDestroy()
    {
        if (_watched != null)
            _watched.ActiveChanged -= OnSlotActiveChanged;
        _watched = null;
        World?.ScriptsIfAny?.Unregister(this);
        StopRuntime();
        StopLocal(string.Empty);
        KeepClipsWarm(false);
        _linkedFor = null;
        ScriptDriveLinks.Release(this);
        base.OnDestroy();
    }

    // While the script may run, the clips it is bound to stay loaded on every machine. See ScriptClipWarmer.
    private void KeepClipsWarm(bool running)
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed || World is not { IsAuthority: true } world)
            return;
        ScriptClipWarmer? warmer = null;
        foreach (var existing in slot.GetComponents<ScriptClipWarmer>())
        {
            if (ReferenceEquals(existing.Host.Target, this))
                warmer = existing;
        }
        var clips = new List<IAssetProvider<AudioClip>>();
        for (int i = 0; running && i < Bindings.Count; i++)
        {
            if (Bindings[i] is IAssetProvider<AudioClip> clip and Component { IsDestroyed: false, IsLocalElement: false } && !clips.Contains(clip))
                clips.Add(clip);
        }
        if (clips.Count == 0 && warmer == null)
            return;

        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            if (clips.Count == 0)
            {
                warmer!.Destroy();
                return;
            }
            if (warmer == null)
            {
                warmer = slot.AttachComponent<ScriptClipWarmer>();
                warmer.Persistent = false;
                warmer.Host.Target = this;
            }
            bool same = warmer.Clips.Count == clips.Count;
            for (int i = 0; same && i < clips.Count; i++)
                same = ReferenceEquals(warmer.Clips[i], clips[i]);
            if (!same)
            {
                warmer.Clips.Clear();
                foreach (var clip in clips)
                    warmer.Clips.Add(clip);
            }
        }
    }

    // Raised on the authority only, which is the only place a Shared script runs.
    public override void OnUserJoined(User user)
    {
        base.OnUserJoined(user);
        if (_runtime is { CanRun: true } runtime && runtime.Runner.Instance.Handles(typeof(UserJoined)))
            runtime.Enqueue(runner => runner.Raise(new UserJoined(user)));
    }

    public override void OnUserLeft(User user)
    {
        base.OnUserLeft(user);
        if (_runtime is { CanRun: true } runtime && runtime.Runner.Instance.Handles(typeof(UserLeft)))
            runtime.Enqueue(runner => runner.Raise(new UserLeft(user)));
    }

    public void OnButtonPressed(Button button, in UIInteractionContext context)
    {
        var world = World;
        if (world == null || IsDestroyed)
            return;
        // A Local script hears the press on the machine it was made on, at once; an Author script on its author's.
        if (Mode.Value == ScriptMode.Local || _localRuntime is { DrivesOnly: false, Runner.Context.OnAuthorMachine: true })
        {
            if (_localRuntime is { CanRun: true } local && button.CanInteract
                && world.DataModelPermissions?.AllowsDomain(context.Actor ?? world.LocalUser, DataModelPermissionDomain.Touch) != false)
                local.EnqueuePress(context.Actor ?? world.LocalUser, context.WorldPoint);
            return;
        }
        if (world.IsAuthority)
            PressFrom(context.Actor, context.WorldPoint);
        else
            ScriptPressRelay.For(world)?.Publish(this, context.WorldPoint);
    }

    // Built with the inspector section, and built again by ScriptGraphReadout whenever what it shows changes.
    public void BuildInspectorBody(UIBuilder ui)
    {
        InspectorStats.AddRow(ui, "Status", string.IsNullOrEmpty(Status.Value) ? "not started" : Status.Value);
        if (Mode.Value != ScriptMode.Shared)
            InspectorStats.AddRow(ui, "Here", string.IsNullOrEmpty(LocalStatus) ? "not started" : LocalStatus);
        if ((Mode.Value != ScriptMode.Shared ? LastRefusalHere : LastRefusal) is { } refused)
            InspectorStats.AddRow(ui, "Last refused", refused);
        if (Synced.Count > 0)
        {
            InspectorStats.AddRow(ui, "Synced", Synced.Count == 1 ? "1 value" : $"{Synced.Count} values");
            int rows = 0;
            foreach (var entry in Synced)
            {
                if (++rows > MaxSyncedRows)
                    break;
                string text = entry.Value.Value ?? string.Empty;
                InspectorStats.AddRow(ui, entry.Key.Value ?? string.Empty, text.Length > 40 ? text[..37] + "..." : text);
            }
        }
        AddGraphRows(ui);
        // Only the host lets a script check joins, and only for the graph it was shown.
        if (World is { IsAuthority: true } && _runtime is { CanRun: true } checker && !checker.IsLocal
            && checker.Runner.Instance.Handles(typeof(JoinRequested)))
        {
            bool on = _checksJoinsFor == checker.GraphHash;
            InspectorStats.AddRow(ui, "Join checks", on
                ? "on: it hears everyone who asks to join this session"
                : "off: on, it can turn people away and pick their role until the session ends");
            InspectorUI.RelayButton(ui, this, "joins", on ? "Stop it checking joins" : "Let it check joins", 0f);
        }
        // A group never packed has no graph or bindings to take on; packing it is what makes it run.
        if (Status.Value?.Contains("someone else") == true && Graph.Target != null)
            InspectorUI.RelayButton(ui, this, "adopt", "Run these changes as mine", 0f);
        ScriptGraphReadout.Watch(ui, this);
    }

    // The graph as one chip, tinted by how it stands, over the one thing to do with it next.
    private void AddGraphRows(UIBuilder ui)
    {
        var (state, tint, action, label) = GraphState();
        InspectorUI.FixedRow(ui.Root, "Graph", 30f, out var row, ui.Root);
        row.PushStyle();
        row.MinWidth(150f);
        row.PreferredWidth(190f);
        row.FlexibleWidth(0f);
        var name = row.Text("Graph", InspectorUI.FontSize - 1f, InspectorUI.MutedColor);
        InspectorUI.FillParent(name.RectTransform!);
        name.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        name.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        row.PopStyle();

        row.PushStyle();
        row.FlexibleWidth(1f);
        var chip = row.Next("State");
        var plate = chip.AttachComponent<Image>();
        plate.Tint.Value = new color(tint.r, tint.g, tint.b, 0.16f);
        plate.CornerRadius.Value = 15f;
        row.NestInto(chip);
        var text = row.Text(state, InspectorUI.FontSize - 1f, tint);
        InspectorUI.FillParent(text.RectTransform!);
        text.RectTransform!.OffsetMin.Value = new float2(14f, 0f);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        row.NestOut();
        row.PopStyle();

        InspectorUI.FixedRow(ui.Root, "Graph Action", 36f, out var act, ui.Root);
        act.PushStyle();
        act.BackgroundColor(action.Length > 0 ? DashTheme.Accent : DashTheme.Surface);
        act.TextColor(action.Length > 0 ? DashTheme.OnAccent : DashTheme.TextMuted);
        InspectorUI.RelayButton(act, this, action, label, 0f);
        act.PopStyle();

        if (!string.IsNullOrEmpty(_lastAction))
            InspectorStats.AddRow(ui, "Last press", _lastAction).Color.Value = DashTheme.Warning;
    }

    // What the graph is doing, the chip's tint, and the press it offers: unpack, pack, or nothing while a pack
    // is on its way.
    private (string State, color Tint, string Action, string Label) GraphState()
    {
        var space = ScriptGraphEditing.FindSpace(this);
        if (ScriptGraphEditing.IsPacking(space))
            return ($"Packing {space!.NodeList.Count} nodes", DashTheme.Accent, string.Empty, "Packing...");
        if (space is { Packed.Value: false })
        {
            int nodes = space.NodeList.Count;
            if (!space.Slot.IsActive)
                return ($"Open, {nodes} nodes, switched off", DashTheme.Warning, "pack", "Pack graph");
            if (space.IsChecked && space.HasProblems)
                return ($"Open, {nodes} nodes, has problems", DashTheme.Warning, "pack", "Pack graph");
            return ($"Open, {nodes} nodes", DashTheme.Positive, "pack", "Pack graph");
        }
        if (space != null)
        {
            int kept = space.NodeList.Count;
            return space.IsChecked && space.HasProblems
                ? ($"Packed with errors, {kept} nodes", DashTheme.Warning, "edit", "Unpack graph")
                : ($"Packed in place, {kept} nodes", DashTheme.Accent, "edit", "Unpack graph");
        }
        if (Graph.Target == null)
            return ("No graph yet", DashTheme.TextMuted, "edit", "New graph");
        return (Graph.IsAssetAvailable && Graph.Asset?.Graph is { } packed ? $"Packed, {packed.Nodes.Count} nodes" : "Packed",
            DashTheme.Accent, "edit", "Unpack graph");
    }

    // The graph the Shared script runs here now and the objects its bindings stand for, while it runs one: the
    // last good graph, which a graph with errors is packed as.
    internal (ScriptGraph Graph, IReadOnlyList<object?> Bindings)? Running
        => _runtime is { CanRun: true } runtime && _runningGraph != null ? (_runningGraph, runtime.Bindings) : null;

    // Everything the section shows, in one line: the readout builds the section again when this reads differently.
    internal string InspectorState()
    {
        var (state, _, action, _) = GraphState();
        string running = _runtime is { CanRun: true } runtime ? runtime.GraphHash : string.Empty;
        return $"{state}|{action}|{Status.Value}|{LocalStatus}|{_lastAction}|{Synced.Count}|{LastRefusal}|{LastRefusalHere}|{running}|{_checksJoinsFor}";
    }

    // On the machine of whoever pressed, so each of these acts as them.
    public void HandleInspectorAction(string argument)
    {
        switch (argument)
        {
            case "edit":
                _lastAction = ScriptGraphEditing.Unpack(this, out var error) == null ? error ?? "the graph did not open" : null;
                break;
            case "pack":
                _lastAction = null;
                if (ScriptGraphEditing.FindSpace(this) is { } space)
                    _ = TakePackAsync(ScriptGraphEditing.PackAsync(space));
                break;
            case "adopt":
                RunEditsAsAuthor();
                break;
            case "joins":
                ToggleJoinChecks();
                break;
        }
    }

    // The graph the host let check joins this session, by its hash: any edit takes it back. Kept on the host's
    // own copy only, never synced or saved, so every session starts with no checks on. -xlinka
    private string? _checksJoinsFor;

    private void ToggleJoinChecks()
    {
        if (World is not { IsAuthority: true } || _runtime is not { CanRun: true } runtime || runtime.IsLocal)
        {
            _lastAction = "only the host lets a running script check joins";
            return;
        }
        if (_checksJoinsFor == runtime.GraphHash)
        {
            _checksJoinsFor = null;
            _lastAction = null;
            return;
        }
        if (!runtime.Principal.IsWorldContent)
        {
            _lastAction = "only the world's own scripts check joins";
            return;
        }
        _checksJoinsFor = runtime.GraphHash;
        _lastAction = null;
        LumoraLogger.Log($"ScriptHost {ReferenceID}: checks joins this session");
    }

    // The running script the host let check joins, while it is still the graph that was let.
    internal ScriptHostRuntime? JoinCheck
        => _checksJoinsFor != null && _runtime is { CanRun: true, IsLocal: false } runtime && runtime.GraphHash == _checksJoinsFor
            && Mode.Value == ScriptMode.Shared && runtime.Principal.IsWorldContent && runtime.Runner.Instance.Handles(typeof(JoinRequested))
            ? runtime : null;

    private async System.Threading.Tasks.Task TakePackAsync(System.Threading.Tasks.Task<string?> pack)
    {
        string? refused;
        try
        {
            refused = await pack.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            refused = ex.Message;
        }
        _lastAction = refused;
        if (refused != null)
            LumoraLogger.Warn($"ScriptHost {ReferenceID}: pack: {refused}");
    }

    // A press the authority takes: the slot still has a live button, and the presser may interact here.
    // A press on one of this script's Call nodes, by the one who pressed it. The button only exists on
    // the open graph's card, so a press is only taken while the graph is open here.
    internal void CallFrom(User? caller, string nodeId)
    {
        if (_runtime is not { CanRun: true } runtime || string.IsNullOrEmpty(nodeId))
            return;
        if (ScriptGraphEditing.FindSpace(this) is not { IsShown: true })
            return;
        if (World?.DataModelPermissions?.AllowsDomain(caller, DataModelPermissionDomain.Touch) == false)
            return;
        runtime.EnqueueCall(caller, nodeId);
    }

    // Something a relay on a watched object passed on: a panel button press, a beam coming or going, a
    // beam click. The runtime checks the object is one this script listens to.
    internal void SourceEventFrom(User? user, Slot source, ScriptSourceKind kind, in float3 point)
    {
        if (_runtime is { CanRun: true } runtime && source != null && !source.IsDestroyed)
            runtime.Sources.Relay(user, source, kind, point);
    }

    // The same, with the text typed or the thing dropped that some kinds carry.
    internal void SourceEventFrom(User? user, Slot source, ScriptSourceKind kind, in float3 point, string? text, Slot? other)
    {
        if (_runtime is { CanRun: true } runtime && source != null && !source.IsDestroyed)
            runtime.Sources.Relay(user, source, kind, point, text, other);
    }

    // A press on a panel button on a watched object landing, held or ending, with where it is on the button.
    internal void PressStepFrom(User? user, Slot source, ScriptSourceKind kind, in float3 point, in float2 local, in float2 normalized)
    {
        if (_runtime is { CanRun: true } runtime && source != null && !source.IsDestroyed)
            runtime.Sources.RelayPressStep(user, source, kind, point, local, normalized);
    }

    // On the machine where a Call node's button was pressed: the authority runs it, anyone else carries
    // the press there.
    public static void Call(ScriptGraphNode node)
    {
        var world = node?.World;
        if (node == null || world == null || node.IsDestroyed || node.Space?.Host.Target is not { IsDestroyed: false } host)
            return;
        string id = node.NodeId.Value ?? string.Empty;
        if (world.IsAuthority)
            host.CallFrom(world.LocalUser, id);
        else
            ScriptPressRelay.For(world)?.PublishCall(host, id);
    }

    internal void PressFrom(User? presser, in float3 point)
    {
        if (_runtime is not { CanRun: true } runtime)
            return;
        if (!ScriptEventSources.Pressable(Slot))
            return;
        if (World?.DataModelPermissions?.AllowsDomain(presser, DataModelPermissionDomain.Touch) == false)
            return;
        runtime.EnqueuePress(presser, point);
    }

    // Decides again whether the script should run, and with what. Cheap when nothing changed.
    internal void Reconcile()
    {
        var world = World;
        if (world == null || IsDestroyed)
            return;
        if (world.IsAuthority)
        {
            ReconcileShared(world);
            KeepClipsWarm(IsRunning || !string.IsNullOrEmpty(LocalApproval.Value));
        }
        ReconcileLocal();
        KeepDriveLinks();
    }

    // On the host: the drive links follow whichever runtime owns the script here, the Shared one, or the
    // host's own copy of a Local one. Remade when that runtime changes, taken away when there is none.
    private ScriptHostRuntime? _linkedFor;

    private void KeepDriveLinks()
    {
        var world = World;
        if (world == null || !world.IsAuthority || IsDestroyed)
            return;
        var owner = _runtime is { CanRun: true } shared ? shared
            : Mode.Value != ScriptMode.Shared && _localRuntime is { CanRun: true } local ? local
            : null;
        if (ReferenceEquals(owner, _linkedFor))
            return;
        _linkedFor = owner;
        if (owner == null)
        {
            ScriptDriveLinks.Release(this);
            return;
        }
        if (ScriptDriveLinks.Reconcile(this, owner.Runner.Compiled, owner.Bindings, owner.Runner.Context) is { } refused)
            owner.Runner.Context.Warn(ScriptWarning.Fault, refused);
    }

    private void ReconcileShared(World world)
    {
        if (!Enabled.Value)
        {
            Halt("disabled");
            return;
        }
        var space = ScriptGraphEditing.FindSpace(this);
        if (SwitchedOff(space) is { } off)
        {
            Halt(off);
            return;
        }
        if (!MayRun(world, out var why))
        {
            Halt(why);
            return;
        }

        // An open graph runs as it stands once it checks clean, when every edit in it is the author's own.
        // While it is being checked the script keeps what it has, and with problems it keeps the last good
        // graph. Someone else's edits wait for a pack: a script runs as one person. One with nothing in it
        // that could ever run gets no runtime at all, so it holds no memory either.
        string? packedNote = null;
        if (space != null && !space.Packed.Value)
        {
            if (!space.IsChecked)
                return;
            if (space.HasProblems || space.LastBuild == null)
            {
                SetStatus(_runtime != null ? "running the last good graph; the open one has problems" : "the open graph has problems");
                return;
            }
            if (_pinned!.IsWorldContent || !HasForeignWork(space))
            {
                if (!HasWork(space.LastCompile))
                {
                    Halt(NothingToRun);
                    return;
                }
                var live = space.LastBuild;
                var liveBindings = new object?[live.Bindings.Count];
                for (int i = 0; i < liveBindings.Length; i++)
                    liveBindings[i] = live.Bindings[i];
                RunOrApprove(live.Graph, liveBindings, "running the open graph");
                return;
            }
            packedNote = Graph.Target == null
                ? "not running until packed: it has someone else's work in it, and a script runs as one person"
                : "the open graph has someone else's edits, which run once its author packs it";
        }

        if (!_pinned!.IsWorldContent && ForeignEdit(_pinned.Author) is string edited)
        {
            Halt(edited);
            return;
        }

        // No graph at all stops the script. A graph on its way keeps the last one running until it lands.
        if (Graph.Target == null || Graph.Target is IUrlAssetProvider { SourceUrl: null })
        {
            Halt(packedNote ?? "no graph");
            return;
        }
        var asset = Graph.Asset;
        var graph = Graph.IsAssetAvailable ? asset?.Graph : null;
        if (graph == null)
        {
            if (((Graph.Target as IUrlAssetProvider)?.LoadFailure ?? asset?.LoadError) is string error)
                Halt("graph does not load: " + error);
            else if (_runtime == null)
                SetStatus("waiting for its graph");
            return;
        }

        var bindings = new object?[Bindings.Count];
        for (int i = 0; i < bindings.Length; i++)
            bindings[i] = Bindings[i];
        RunOrApprove(graph, bindings, packedNote == null ? "running" : "running the packed graph; " + packedNote);
    }

    private void Run(ScriptGraph graph, object?[] bindings, string status)
    {
        if (_runtime != null && SameGraph(graph) && SameBindings(_runtime.Bindings, bindings))
        {
            _running = status;
            SetStatus(RunningStatus());
            return;
        }
        Start(graph, bindings, _pinned!, status);
    }

    // Anything in the open graph that someone other than the author made or last changed: a node, a value,
    // a wire. Moving a node is not a change to what it does.
    private bool HasForeignWork(ScriptGraphSpace space)
    {
        byte owner = ReferenceID.GetUserByte();
        var author = _pinned?.Author;
        foreach (var node in space.Nodes)
        {
            if (node.ReferenceID.GetUserByte() != owner || WrittenByOther(node.NodeType, author) || WrittenByOther(node.Signature, author)
                || WrittenByOther(node.Version, author) || WrittenByOther(node.NodeId, author))
                return true;
            foreach (var value in node.Values)
            {
                if (value.ReferenceID.GetUserByte() != owner || WrittenByOther(value.Port, author) || WrittenByOther(value.Json, author)
                    || WrittenByOther(value.Reference, author))
                    return true;
            }
            foreach (var wire in node.Inputs)
            {
                if (ForeignWire(wire, owner, author))
                    return true;
            }
            foreach (var wire in node.Exits)
            {
                if (ForeignWire(wire, owner, author))
                    return true;
            }
        }
        return false;
    }

    private static bool ForeignWire(ScriptGraphWire wire, byte owner, User? author)
        => wire.ReferenceID.GetUserByte() != owner || WrittenByOther(wire.Port, author) || WrittenByOther(wire.Node, author)
            || WrittenByOther(wire.NodePort, author);

    private const string NothingToRun = "nothing to run yet: the open graph has no event";

    // Anything a graph would ever do: an event that starts it, a drive, a Display that works its value out
    // alone, or a Function another script may call. A Pulse, and a Display fed by an event, only ever show what
    // an event did.
    private static bool HasWork(CompileResult? compiled)
    {
        if (compiled == null || compiled.Events.Count > 0 || compiled.Drives.Count > 0 || compiled.Functions.Count > 0)
            return true;
        foreach (var display in compiled.Displays)
        {
            if (display.Refreshed)
                return true;
        }
        return false;
    }

    private bool MayRun(World world, out string why)
    {
        why = string.Empty;
        // Only a host that woke before its world was the authority gets here unpinned.
        if (_pinned == null && _orphaned == null)
            Pin();
        if (_orphaned == null && !_pinned!.IsStillAuthorIn(world))
            _orphaned = AuthorLeft;
        if (_orphaned != null)
        {
            why = _orphaned;
            return false;
        }
        if (!_pinned!.IsWorldContent
            && world.DataModelPermissions?.AllowsDomain(_pinned.Author, DataModelPermissionDomain.Script) != true)
        {
            why = "its author's role may not run scripts here";
            return false;
        }
        return true;
    }

    // A null writer is a value that came with the element itself, from its creator or a load.
    private string? ForeignEdit(User? author)
    {
        byte owner = ReferenceID.GetUserByte();
        if (Foreign(Graph, author))
            return "its graph was changed by someone else";
        if (Graph.Target is Component provider)
        {
            if (provider.ReferenceID.GetUserByte() != owner)
                return "its graph belongs to someone else";
            if (provider is ScriptGraphProvider graphProvider && Foreign(graphProvider.URL, author))
                return "its graph was changed by someone else";
        }
        for (int i = 0; i < Bindings.Count; i++)
        {
            var binding = Bindings.GetElement(i);
            if (binding.ReferenceID.GetUserByte() != owner || Foreign(binding, author))
                return "its bindings were changed by someone else";
        }
        return null;
    }

    // Someone else changed this script; its author, on their own machine, takes the change as theirs. A
    // value written again unchanged never leaves the machine, so the graph moves to a fresh provider of the
    // author's own with the same URL, and the bindings are minted again by the author.
    public bool RunEditsAsAuthor()
    {
        var world = World;
        var local = world?.LocalUser;
        if (world == null || local == null || IsDestroyed || !local.HoldsByte(ReferenceID.GetUserByte()))
            return false;

        if (Graph.Target is ScriptGraphProvider old)
        {
            var fresh = Slot.AttachComponent<ScriptGraphProvider>();
            fresh.URL.Value = old.URL.Value;
            Graph.Target = fresh;
            if (ReferenceEquals(old.Slot, Slot) && old.ReferenceID.GetUserByte() == ReferenceID.GetUserByte())
                old.Destroy();
        }
        ScriptBindingsUndo.Assign(Bindings, ScriptBindingsUndo.Snapshot(Bindings), rebuild: true);
        return true;
    }

    private static bool WrittenByOther(ConflictingSyncElement member, User? author)
        => member.LastModifyingUser is { } writer && !ReferenceEquals(writer, author);

    // Written by someone else. What the host set up here for the script of the author that packed this one is
    // the author's doing, for as long as it holds what the host wrote.
    private bool Foreign(ConflictingSyncElement member, User? author)
        => WrittenByOther(member, author) && World?.ScriptsIfAny?.SetUpForAuthor(this, member) != true;

    private const string AuthorLeft = "its author left";

    private const int MaxSyncedRows = 8;

    private void Pin()
    {
        _pinned = ScriptPrincipal.ForElement(this);
        if (_pinned == null)
            _orphaned = "its author is not in the world";
    }

    private bool SameGraph(ScriptGraph graph)
        => ReferenceEquals(graph, _runningGraph) || _runtime!.GraphHash == graph.ComputeHash();

    private void Start(ScriptGraph graph, object?[] bindings, ScriptPrincipal principal, string status)
    {
        if (CompiledNow(graph, out bool cached) is not { } compiled)
        {
            if (_runtime == null)
                SetStatus("compiling");
            return;
        }
        var previous = _runtime;
        ScriptHostRuntime next;
        try
        {
            next = ScriptHostRuntime.Start(this, compiled, cached, bindings, principal, previous,
                authorMachine: Mode.Value == ScriptMode.Author);
        }
        catch (Exception ex)
        {
            if (ex is not (ScriptLinkException or ScriptLimitException or Lumora.Wasm.WasmException))
                LumoraLogger.Warn($"ScriptHost {ReferenceID}: script failed to start: {ex}");

            // A new graph that fails leaves the last good one running.
            if (previous is { CanRun: true })
            {
                SetStatus("new graph refused, still running the last one: " + ex.Message);
            }
            else
            {
                StopRuntime();
                SetStatus(ex.Message);
            }
            return;
        }

        _runtime = next;
        _runningGraph = graph;
        // Asked before the old one goes: a disposed runtime never reads as running, and a live swap would
        // then look like a fresh start.
        bool swapped = previous is { CanRun: true };
        if (swapped)
        {
            next.Runner.Instance.AdoptStoredValues(previous!.Runner.Instance);
            next.Runner.Instance.AdoptFlowState(previous.Runner.Instance);
        }
        previous?.Dispose();
        if (!swapped)
        {
            ClearSynced();
            if (next.Runner.Instance.Handles(typeof(ScriptStarted)))
                next.Enqueue(runner => runner.Raise(new ScriptStarted()));
            if (Duplicated.Value)
            {
                using (World?.DataModelPermissions?.EnterSystemBypass())
                    Duplicated.Value = false;
                if (next.Runner.Instance.Handles(typeof(ObjectDuplicated)))
                    next.Enqueue(runner => runner.Raise(new ObjectDuplicated()));
            }
        }
        _running = status;
        _lastFault = null;
        SetStatus(RunningStatus());
    }

    private string RunningStatus() => _lastFault == null ? _running : $"{_running}, last fault: {_lastFault}";

    // The graph's compile when it can be had now: from the cache, or from the worker this host waited on.
    // Null while it is on a worker.
    private CompileResult? CompiledNow(ScriptGraph graph, out bool cached)
    {
        cached = false;
        string hash = graph.ComputeHash();
        if (_compiling != null && _compilingHash == hash)
        {
            if (!_compiling.IsCompleted)
                return null;
            var landed = _compiling.Result;
            _compiling = null;
            _compilingHash = null;
            return landed;
        }
        if (ScriptEnvironment.TryGetCached(hash, out var hit))
        {
            cached = true;
            return hit;
        }
        _compilingHash = hash;
        _compiling = ScriptEnvironment.CompileAsync(graph, hash);
        World.Scripts.AwaitCompile(this, _compiling);
        return null;
    }

    private void Halt(string status)
    {
        StopRuntime();
        SetApproval(string.Empty);
        SetStatus(status);
    }

    // Why the script is stopped by a switch: its object off, or its graph's slot. Null while both are on.
    private string? SwitchedOff(ScriptGraphSpace? space)
    {
        if (Slot is not { IsActive: true })
            return "its object is switched off";
        if (space is { Slot.IsActive: false })
            return "its graph is switched off";
        return null;
    }

    // What a Shared script would run, a Local one is approved to run on every machine instead, and an Author
    // one on its author's.
    private void RunOrApprove(ScriptGraph graph, object?[] bindings, string status)
    {
        if (Mode.Value == ScriptMode.Local || (Mode.Value == ScriptMode.Author && !_pinned!.IsWorldContent))
        {
            StopRuntime();
            SetApproval(graph.ComputeHash());
            SetStatus((Mode.Value == ScriptMode.Local ? "on every machine: " : "on its author's machine: ") + status);
            return;
        }
        Run(graph, bindings, status);
        // Every other machine runs this graph's drives, so they need it approved as a Local one's would be.
        SetApproval(_runtime is { CanRun: true } running && running.Runner.Instance.HasDrives ? running.GraphHash : string.Empty);
    }

    private void SetApproval(string hash)
    {
        var world = World;
        if (world == null || !world.IsAuthority || LocalApproval.Value == hash)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
            LocalApproval.Value = hash;
    }

    // On every machine: runs the graph the host approved once this machine has it, as the author. A Local
    // script runs whole everywhere, an Author one on its author's machine; everywhere else a drives-only copy
    // runs, except where the host's own Shared runtime drives.
    private void ReconcileLocal()
    {
        string approved = LocalApproval.Value ?? string.Empty;
        var here = ScriptPrincipal.ForElement(this);
        bool authorHere = Mode.Value == ScriptMode.Author && here is { IsWorldContent: false }
            && World?.LocalUser is { } me && me.HoldsByte(here.AllocationByte);
        bool drivesOnly = Mode.Value != ScriptMode.Local && !authorHere;
        if ((drivesOnly && (approved.Length == 0 || _runtime != null || (World?.IsAuthority != false && Mode.Value == ScriptMode.Shared)))
            || !Enabled.Value)
        {
            StopLocal(string.Empty);
            return;
        }
        var space = ScriptGraphEditing.FindSpace(this);
        if (SwitchedOff(space) is { } off)
        {
            StopLocal(off);
            return;
        }
        if (approved.Length == 0)
        {
            StopLocal(string.IsNullOrEmpty(Status.Value) ? "waiting for the host" : "not running: " + Status.Value);
            return;
        }
        var principal = ScriptPrincipal.ForElement(this);
        if (principal == null)
        {
            StopLocal("its author is not here");
            return;
        }

        ScriptGraph? graph = null;
        object?[]? bindings = null;
        if (space is { Packed.Value: false, IsChecked: true, HasProblems: false, LastBuild: { } build } && build.Graph.ComputeHash() == approved)
        {
            graph = build.Graph;
            bindings = new object?[build.Bindings.Count];
            for (int i = 0; i < bindings.Length; i++)
                bindings[i] = build.Bindings[i];
        }
        else if (Graph.IsAssetAvailable && Graph.Asset?.Graph is { } packed && packed.ComputeHash() == approved)
        {
            graph = packed;
            bindings = new object?[Bindings.Count];
            for (int i = 0; i < bindings.Length; i++)
                bindings[i] = Bindings[i];
        }
        if (graph == null || bindings == null)
        {
            if (_localRuntime == null)
                LocalStatus = "waiting for the graph";
            return;
        }
        if (_localRuntime is { } running && running.GraphHash == approved && SameBindings(running.Bindings, bindings)
            && running.Principal.AuthorKey == principal.AuthorKey && running.DrivesOnly == drivesOnly
            && running.Runner.Context.OnAuthorMachine == authorHere)
            return;
        StartLocal(graph, bindings, principal, drivesOnly, authorHere);
    }

    private void StartLocal(ScriptGraph graph, object?[] bindings, ScriptPrincipal principal, bool drivesOnly, bool authorMachine = false)
    {
        if (CompiledNow(graph, out bool cached) is not { } compiled)
        {
            if (_localRuntime == null)
                LocalStatus = "compiling here";
            return;
        }
        if (!compiled.Success)
        {
            foreach (var error in compiled.Errors)
            {
                if (error.NeedsNewerBuild)
                {
                    StopLocal("this machine needs a newer build to run it: " + error.Message);
                    return;
                }
            }
            StopLocal("cannot run here: " + string.Join("; ", compiled.Errors));
            return;
        }

        var previous = _localRuntime;
        ScriptHostRuntime next;
        try
        {
            next = ScriptHostRuntime.Start(this, compiled, cached, bindings, principal, previous, local: true, drivesOnly: drivesOnly,
                authorMachine: authorMachine);
        }
        catch (Exception ex)
        {
            if (ex is not (ScriptLinkException or ScriptLimitException or Lumora.Wasm.WasmException))
                LumoraLogger.Warn($"ScriptHost {ReferenceID}: Local script failed to start: {ex}");
            if (previous is { CanRun: true })
                LocalStatus = "new graph refused here, still running the last one: " + ex.Message;
            else
                StopLocal(ex.Message);
            return;
        }

        _localRuntime = next;
        _seenInbox = Inbox.Value;
        bool swapped = previous is { CanRun: true };
        if (swapped)
        {
            next.Runner.Instance.AdoptStoredValues(previous!.Runner.Instance);
            next.Runner.Instance.AdoptFlowState(previous.Runner.Instance);
        }
        previous?.Dispose();
        if (!swapped && next.Runner.Instance.Handles(typeof(ScriptStarted)))
            next.Enqueue(runner => runner.Raise(new ScriptStarted()));
        if (!swapped && !drivesOnly && _duplicatedHere)
        {
            _duplicatedHere = false;
            if (next.Runner.Instance.Handles(typeof(ObjectDuplicated)))
                next.Enqueue(runner => runner.Raise(new ObjectDuplicated()));
        }
        LocalStatus = drivesOnly ? "driving here" : "running here";
    }

    private void StopLocal(string status)
    {
        var runtime = _localRuntime;
        _localRuntime = null;
        runtime?.Dispose();
        LocalStatus = status;
    }

    // A message from a Shared script of the same author, on the authority, for every machine's copy of
    // this Local script. Messages sent in the same frame arrive as the last of them.
    internal void Post(string message, float value)
    {
        var world = World;
        if (world == null || !world.IsAuthority || IsDestroyed)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            InboxMessage.Value = message;
            InboxValue.Value = value;
            Inbox.Value = unchecked(Inbox.Value + 1);
        }
    }

    // On the host, from this host's Shared script: one synced value set, added when the name is new. True
    // when it was added.
    internal bool WriteSynced(string name, string type, string text)
    {
        var world = World;
        if (world == null || !world.IsAuthority || IsDestroyed)
            return false;
        var entry = FindSynced(name);
        bool added = entry == null;
        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            if (entry == null)
            {
                entry = Synced.Add();
                entry.Key.Value = name;
            }
            if (entry.Type.Value != type)
                entry.Type.Value = type;
            if (entry.Value.Value != text)
                entry.Value.Value = text;
        }
        return added;
    }

    internal void RemoveSynced(string name)
    {
        var world = World;
        if (world == null || !world.IsAuthority || IsDestroyed || FindSynced(name) is not { } entry)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
            Synced.Remove(entry);
    }

    private void ClearSynced()
    {
        var world = World;
        if (world == null || !world.IsAuthority || Synced.Count == 0)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
            Synced.Clear();
    }

    public bool TryReadSynced<T>(string name, out T value)
    {
        if (FindSynced(name) is { } entry)
            return entry.TryRead(out value);
        value = default!;
        return false;
    }

    public ScriptSyncedValue? FindSynced(string name)
    {
        foreach (var entry in Synced)
        {
            if (string.Equals(entry.Key.Value, name, StringComparison.Ordinal))
                return entry;
        }
        return null;
    }

    // A message from a script of the same author to this Shared script, on the authority.
    internal void Receive(string message, float value, User? from)
    {
        if (_runtime is { CanRun: true } runtime)
            runtime.EnqueueMessage(message, value, from);
    }

    private void TakeInbox()
    {
        int inbox = Inbox.Value;
        if (inbox == _seenInbox)
            return;
        _seenInbox = inbox;
        if (_localRuntime is { CanRun: true } local)
            local.EnqueueMessage(InboxMessage.Value ?? string.Empty, InboxValue.Value, null);
    }

    private void StopRuntime()
    {
        var runtime = _runtime;
        _runtime = null;
        _runningGraph = null;
        runtime?.Dispose();
    }

    internal void RuntimeStopped(ScriptHostRuntime runtime, string reason)
    {
        if (ReferenceEquals(runtime, _localRuntime))
        {
            StopLocal(reason);
            return;
        }
        if (!ReferenceEquals(runtime, _runtime))
            return;
        if (World == null || !runtime.Principal.IsStillAuthorIn(World))
            _orphaned ??= AuthorLeft;
        Halt(_orphaned ?? reason);
    }

    internal void RuntimeFaulted(ScriptHostRuntime runtime, string reason)
    {
        if (ReferenceEquals(runtime, _localRuntime))
            LocalStatus = "running here, last fault: " + reason;
        else if (ReferenceEquals(runtime, _runtime))
        {
            _lastFault = reason;
            SetStatus(RunningStatus());
        }
    }

    // On the host, while a panel elsewhere is watching: the Shared script's cost here, only when it moved.
    internal void PublishCost()
    {
        var world = World;
        if (world == null || !world.IsAuthority || IsDestroyed)
            return;
        var stats = _runtime?.Stats;
        float ms = stats == null ? 0f : MathF.Round((float)stats.MsPerFrame, 3);
        float compile = stats == null ? 0f : MathF.Round((float)stats.CompileMs, 1);
        if (HostMs.Value == ms && HostCompileMs.Value == compile)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            HostMs.Value = ms;
            HostCompileMs.Value = compile;
        }
    }

    // Engine bookkeeping on what may be a user's component, so it takes the system scope a frozen world
    // would otherwise refuse the authority.
    private void SetStatus(string text)
    {
        var world = World;
        if (world == null || Status.Value == text)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
            Status.Value = text;
    }

    private static bool SameBindings(IReadOnlyList<object?> running, object?[] current)
    {
        if (running.Count != current.Length)
            return false;
        for (int i = 0; i < current.Length; i++)
        {
            if (!ReferenceEquals(ShaderPropertyFields.Unwrap(running[i]), current[i]))
                return false;
        }
        return true;
    }
}

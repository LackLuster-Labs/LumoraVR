// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// Loom on this machine, opened from the Editor wizards: the engine and its limits, how long the script
// phase takes, what every script in the world costs and what it cost to compile, the memory scripts and
// the process hold, and a self-test that times the engine here.
//
// The shell is shared and anyone can use it. Each peer builds the diagnostic contents locally: process
// metrics, clipboard actions and benchmarks belong to the person using it. A Shared script's costs
// still come from the host. Local view slots are reconstructed by every peer, not sent per frame.
[ComponentCategory("Utility/Inspectors")]
public sealed class LoomDiagnostic : Component, IInspectorActionHandler
{
    public const float PanelWidth = 800f;
    public const float PanelHeight = 1080f;

    // Canvas units are slot units, so a panel is scaled down to half a millimetre a unit, like the
    // inspector's: this one is 0.4 by 0.54 m.
    public const float PanelScale = InspectorUI.PanelScale;
    private const float RowHeight = 30f;
    private const float ScriptRowHeight = 60f;
    private const float LabelWidth = 240f;
    private const float PollSeconds = 0.5f;
    private const double ProcessSeconds = 1.0;
    private const double WatchSeconds = 4.0;
    private const int MaxScriptRows = 80;

    // A script that spends half a millisecond every frame is heavy, and so is one that took HeavyPeakMs of a
    // single frame; a tenth of a millisecond a frame is busy.
    public const double HeavyMs = 0.5;
    public const double HeavyPeakMs = 4.0;
    public const double BusyMs = 0.1;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private enum Tab { Overview, Scripts, Memory, Benchmark, SelfTest }

    public readonly Sync<int> SelectedPage = new();
    public readonly SyncRef<ScriptHost> SelectedScript = new();

    private enum CostBand { Stopped, Idle, Light, Busy, Heavy }

    private readonly record struct Live(Text Text, Func<string> Value, Func<color>? Tint);

    // One script as this machine sees it: measured here, or published by the host for a Shared script.
    private readonly record struct ScriptView(ScriptHost Host, ScriptHostRuntime? Runtime, bool Local, bool Running,
        bool Published, double Ms, double CompileMs, string Status, CostBand Band);

    private readonly List<Live> _live = new();
    private readonly Dictionary<Tab, Text> _tabLabels = new();
    private readonly List<ScriptHost> _hosts = new();
    private readonly List<ScriptHost> _listed = new();
    private readonly List<ScriptAuthorAccount> _accounts = new();
    private readonly List<string> _listedAccounts = new();
    private readonly Dictionary<string, string> _authorNames = new(StringComparer.Ordinal);
    private readonly StringBuilder _text = new(512);
    private Tab _tab;
    private Slot? _view;
    private Slot? _body;
    private ScriptHost? _selected;
    private float _poll;
    private double _nextWatch;
    private string _notice = string.Empty;

    // The self-test runs one step a frame, then compiles every script it can find here, one a frame.
    private readonly List<ScriptSelfTestResult> _results = new();
    private int _step = -1;
    private readonly Queue<ScriptHost> _compileQueue = new();
    private readonly List<(string Name, bool Ok, double Ms, string Detail)> _compiled = new();
    private int _compileTotal;
    private string _lastRun = string.Empty;

    // The benchmark runs one case a frame on its own local stage, removed when the last case is done.
    private ScriptBenchmark? _bench;
    private int _benchStep = -1;
    private readonly List<ScriptBenchmarkResult> _benchResults = new();
    private string _benchRun = string.Empty;

    // This machine: the process sampled once a second, the world's update averaged between polls.
    private System.Diagnostics.Process? _process;
    private bool _processUnavailable;
    private long _sampleTicks;
    private long _nextDetailTicks;
    private TimeSpan _lastCpu;
    private long _lastAllocated;
    private double _cpuPercent;
    private double _allocatedPerSecond;
    private long _workingSet;
    private long _privateBytes;
    private int _threads;
    private DateTime _processStart;
    private readonly int[] _gcAtOpen = new int[3];
    private ulong _profileIndex = ulong.MaxValue;
    private double _worldMsSum;
    private double _scriptMsSum;
    private int _profileFrames;
    private double _worldMs;
    private double _scriptMs;

    // Opens the panel on this machine, or brings the one already open in front of whoever is looking.
    public static LoomDiagnostic? Spawn(World world)
    {
        if (world?.RootSlot == null)
            return null;
        var existing = Find(world);
        if (existing != null)
        {
            PlaceInFront(existing.Slot, world);
            return existing;
        }

        var panelSlot = world.AddSpawnSlot(world.RootSlot, "Loom Diagnostic");
        panelSlot.Persistent.Value = false;
        panelSlot.Tag.Value = "Developer";
        panelSlot.LocalScale.Value = float3.One * PanelScale;
        PlaceInFront(panelSlot, world);
        return panelSlot.AttachComponent<LoomDiagnostic>();
    }

    public static LoomDiagnostic? Find(World world)
    {
        var panel = world?.RootSlot?.GetComponentInChildren<LoomDiagnostic>();
        return panel is { IsDestroyed: false } ? panel : null;
    }

    public static void Toggle(World world)
    {
        if (Find(world) is { } open)
            open.Slot.Destroy();
        else
            Spawn(world);
    }

    private static void PlaceInFront(Slot panelSlot, World world)
    {
        var head = world.LocalUser?.Root?.HeadSlot;
        if (head == null || head.IsDestroyed)
            return;
        float3 forward = head.GlobalRotation * float3.Backward;
        forward.y = 0f;
        forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
        panelSlot.GlobalPosition = head.GlobalPosition + forward * 1.0f + new float3(0f, -0.1f, 0f);
        panelSlot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(-forward.x, -forward.z));
    }

    public override void OnAttach()
    {
        base.OnAttach();
        var theme = Slot.GetOrAttachComponent<UITheme>();
        var shell = Slot.GetOrAttachComponent<PanelShell>();
        shell.Title.Value = "Loom Diagnostic · This client";
        shell.Size.Value = new float2(PanelWidth, PanelHeight);
        theme.ApplyTo(shell);
        // Build the shared shell before peers reconstruct their own diagnostic controls.
        _ = shell.ContentSlot;
    }

    public override void OnStart()
    {
        base.OnStart();
        EnsureView();
    }

    private void EnsureView()
    {
        if (_view is { IsDestroyed: false }) return;
        if (Slot.GetComponent<PanelShell>()?.ContentSlot is not { } content) return;
        for (int generation = 0; generation < _gcAtOpen.Length; generation++)
            _gcAtOpen[generation] = GC.CollectionCount(generation);
        SampleProcess();
        _view = content.AddLocalSlot("Loom View");
        InspectorUI.FillParent(_view.AttachComponent<RectTransform>());
        BuildLayout(new UIBuilder(_view));
    }

    public override void OnDestroy()
    {
        _bench?.Dispose();
        _bench = null;
        _process?.Dispose();
        _process = null;
        base.OnDestroy();
    }

    private void BuildLayout(UIBuilder ui)
    {
        var page = ui.Current;
        InspectorUI.ApplyTheme(ui, Slot);

        var column = page.AttachComponent<VerticalLayout>();
        column.Spacing.Value = 6f;
        column.PaddingLeft.Value = 6f;
        column.PaddingRight.Value = 6f;
        column.PaddingTop.Value = 6f;
        column.PaddingBottom.Value = 6f;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        InspectorUI.FixedRow(page, "Tabs", 36f, out var tabs, Slot);
        _tabLabels.Clear();
        AddTab(tabs, Tab.Overview, "Overview");
        AddTab(tabs, Tab.Scripts, "Scripts");
        AddTab(tabs, Tab.Memory, "Memory");
        AddTab(tabs, Tab.Benchmark, "Benchmark");
        AddTab(tabs, Tab.SelfTest, "Self-test");

        var host = page.AddSlot("Body");
        host.AttachComponent<RectTransform>();
        var hostElement = host.AttachComponent<LayoutElement>();
        hostElement.FlexibleHeight.Value = 1f;
        hostElement.MinHeight.Value = 300f;

        var scrollUi = new UIBuilder(host);
        InspectorUI.ApplyTheme(scrollUi, Slot);
        var scroll = scrollUi.ScrollRect(out var content, null, InspectorUI.PaneColor);
        scroll.RowsActOnRelease.Value = true;
        InspectorUI.FillParent(scroll.Slot.GetComponent<RectTransform>()!);

        var contentLayout = content.Slot.AttachComponent<VerticalLayout>();
        contentLayout.Spacing.Value = 2f;
        contentLayout.ForceExpandWidth.Value = true;
        contentLayout.ForceExpandHeight.Value = false;
        _body = content.Slot;

        _selected = SelectedScript.Target;
        ShowTab(Enum.IsDefined(typeof(Tab), SelectedPage.Value) ? (Tab)SelectedPage.Value : Tab.Overview);
    }

    private void AddTab(UIBuilder ui, Tab tab, string label)
    {
        var button = InspectorUI.RelayButton(ui, this, "tab:" + tab, label, 0f);
        if (button.Slot.GetComponentInChildren<Text>() is { } text)
            _tabLabels[tab] = text;
    }

    private void ShowTab(Tab tab)
    {
        SelectedPage.Value = (int)tab;
        _tab = tab;
        _notice = string.Empty;
        if (tab == Tab.Memory)
            ReadProcessDetail();
        foreach (var (key, label) in _tabLabels)
        {
            if (!label.IsDestroyed)
                label.Color.Value = key == tab ? InspectorUI.AccentColor : InspectorUI.MutedColor;
        }
        Rebuild();
    }

    private void Rebuild()
    {
        var body = _body;
        if (body == null || body.IsDestroyed)
            return;
        body.DestroyChildren();
        _live.Clear();
        Scan();
        switch (_tab)
        {
            case Tab.Overview:
                BuildOverview();
                break;
            case Tab.Scripts:
                BuildScripts();
                break;
            case Tab.Memory:
                BuildMemory();
                break;
            case Tab.Benchmark:
                BuildBenchmark();
                break;
            case Tab.SelfTest:
                BuildSelfTest();
                break;
        }
        RefreshLive();
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        EnsureView();
        var world = World;
        if (world == null || Slot == null || !Slot.IsActive)
            return;

        var page = Enum.IsDefined(typeof(Tab), SelectedPage.Value) ? (Tab)SelectedPage.Value : Tab.Overview;
        if (_tab != page || _selected != SelectedScript.Target)
        {
            _selected = SelectedScript.Target;
            ShowTab(page);
        }

        var profile = world.LastFrameProfile;
        if (profile.UpdateIndex != _profileIndex)
        {
            _profileIndex = profile.UpdateIndex;
            _worldMsSum += profile.TotalMs;
            _scriptMsSum += profile.ScriptsMs;
            _profileFrames++;
        }

        StepSelfTest();
        StepBenchmark();

        _poll -= world.Time.Delta;
        if (_poll > 0f)
            return;
        _poll = PollSeconds;

        if (_profileFrames > 0)
        {
            _worldMs = _worldMsSum / _profileFrames;
            _scriptMs = _scriptMsSum / _profileFrames;
            _worldMsSum = 0;
            _scriptMsSum = 0;
            _profileFrames = 0;
        }
        SampleProcess();
        Scan();

        // A Shared script's cost is only known on the host; ask it to keep publishing while this is open.
        double now = world.Time.TotalTime;
        if (!world.IsAuthority && now >= _nextWatch && HasShared())
        {
            _nextWatch = now + WatchSeconds;
            ScriptPressRelay.For(world)?.WatchCosts();
        }

        if (NeedsRebuild())
        {
            Rebuild();
            return;
        }
        RefreshLive();
    }

    private void RefreshLive()
    {
        foreach (var live in _live)
        {
            if (live.Text.IsDestroyed)
                continue;
            string value;
            color? tint = null;
            try
            {
                value = live.Value();
                tint = live.Tint?.Invoke();
            }
            catch (Exception ex)
            {
                value = "(" + ex.GetType().Name + ")";
            }
            if (live.Text.Content.Value != value)
                live.Text.Content.Value = value;
            if (tint is { } changed && live.Text.Color.Value != changed)
                live.Text.Color.Value = changed;
        }
    }

    // The hosts this machine knows, and the author accounts its ledger holds.
    private void Scan()
    {
        var world = World;
        _hosts.Clear();
        _accounts.Clear();
        _authorNames.Clear();
        if (world?.ScriptsIfAny is not { } scripts)
            return;
        scripts.GetHosts(_hosts);
        scripts.Ledger.GetAccounts(_accounts);
        foreach (var host in _hosts)
        {
            NameAuthor(host.SharedRuntime);
            NameAuthor(host.LocalRuntime);
        }
    }

    private void NameAuthor(ScriptHostRuntime? runtime)
    {
        if (runtime != null && !string.IsNullOrEmpty(runtime.Principal.DisplayName))
            _authorNames[runtime.Principal.AuthorKey] = runtime.Principal.DisplayName!;
    }

    private bool HasShared()
    {
        foreach (var host in _hosts)
        {
            if (host.Mode.Value == ScriptMode.Shared)
                return true;
        }
        return false;
    }

    // The list tabs are built for a set of scripts or authors; a new one, or one gone, builds them again.
    private bool NeedsRebuild()
    {
        if (_tab == Tab.Scripts)
        {
            if (_selected != null && _selected.IsDestroyed)
            {
                _selected = null;
                return true;
            }
            int shown = System.Math.Min(_hosts.Count, MaxScriptRows);
            if (_listed.Count != shown)
                return true;
            foreach (var host in _listed)
            {
                if (host.IsDestroyed || !_hosts.Contains(host))
                    return true;
            }
        }
        if (_tab == Tab.Memory)
        {
            if (_listedAccounts.Count != _accounts.Count)
                return true;
            for (int i = 0; i < _accounts.Count; i++)
            {
                if (_listedAccounts[i] != _accounts[i].AuthorKey)
                    return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ rows

    private void Section(string title) => InspectorUI.SectionHeader(_body!, title, Slot);

    private void Info(string label, Func<string> value, Func<color>? tint = null)
    {
        InspectorUI.FixedRow(_body!, label, RowHeight, out var ui, Slot);
        ui.PushStyle().MinWidth(LabelWidth).PreferredWidth(LabelWidth).FlexibleWidth(0f);
        Align(ui.Text(label, InspectorUI.FontSize - 1f, InspectorUI.MutedColor));
        ui.PopStyle();
        ui.PushStyle().MinWidth(0f).PreferredWidth(0f).FlexibleWidth(1f);
        var text = ui.Text(string.Empty, InspectorUI.FontSize - 1f, InspectorUI.TextColor);
        Fit(text);
        ui.PopStyle();
        _live.Add(new Live(text, value, tint));
    }

    private void Line(Func<string> value, Func<color>? tint = null)
    {
        InspectorUI.FixedRow(_body!, "Line", RowHeight, out var ui, Slot);
        ui.PushStyle().FlexibleWidth(1f);
        var text = ui.Text(string.Empty, InspectorUI.FontSize - 1f, InspectorUI.TextColor);
        Fit(text);
        ui.PopStyle();
        _live.Add(new Live(text, value, tint));
    }

    private void Note(string value, color tint)
    {
        InspectorUI.FixedRow(_body!, "Note", RowHeight, out var ui, Slot);
        ui.PushStyle().FlexibleWidth(1f);
        Fit(ui.Text(value, InspectorUI.FontSize - 2f, tint));
        ui.PopStyle();
    }

    private void Buttons(params (string Label, string Action)[] buttons)
    {
        InspectorUI.FixedRow(_body!, "Actions", RowHeight + 2f, out var ui, Slot);
        foreach (var (label, action) in buttons)
            InspectorUI.RelayButton(ui, this, action, label, 0f);
    }

    private static void Align(Text text)
    {
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        text.WordWrap.Value = false;
    }

    // A value longer than its row shrinks to fit rather than running past the panel's edge.
    private static void Fit(Text text)
    {
        Align(text);
        text.AutoSizeHorizontal.Value = true;
        text.AutoSizeMin.Value = 11f;
    }

    // ------------------------------------------------------------------ overview

    private void BuildOverview()
    {
        Section("At a glance");
        Info("Script phase", () =>
        {
            var phase = Phase;
            if (phase == null || !phase.HasWindow)
                return "no scripts have run here yet";
            return $"{Ms(phase.AverageMs)} ms a frame, peak {Ms(phase.PeakMs)} ms (Shared {Ms(phase.AverageSharedMs)}, Local {Ms(phase.AverageLocalMs)})";
        }, () => Phase is { HasWindow: true } phase
            ? phase.PeakMs >= ScriptScheduler.WarnFrameMs ? DashTheme.Negative : phase.AverageMs >= 1.0 ? DashTheme.Warning : InspectorUI.TextColor
            : InspectorUI.MutedColor);
        Info("Share of the update", () => _worldMs <= 0
            ? "measuring"
            : $"{_scriptMs / _worldMs * 100.0:0.#}% of this world's {Ms(_worldMs)} ms update");
        Info("Frame rate", () =>
        {
            float fps = World?.Time.FramesPerSecond ?? 0f;
            return fps > 0f ? $"{fps:0} fps, {1000f / fps:0.0} ms a frame" : "-";
        });
        Info("Scripts", ScriptCounts);
        Info("Heaviest", () =>
        {
            ScriptView? heaviest = null;
            foreach (var host in _hosts)
            {
                var view = View(host);
                if (view.Running && (heaviest == null || view.Ms > heaviest.Value.Ms))
                    heaviest = view;
            }
            if (heaviest is not { } top || top.Ms <= 0)
                return "nothing is busy";
            return $"{NameOf(top.Host)}, {Ms(top.Ms)} ms a frame{(top.Published ? " on the host" : "")} ({BandName(top.Band).ToLowerInvariant()})";
        }, () =>
        {
            double most = 0;
            foreach (var host in _hosts)
                most = System.Math.Max(most, View(host).Ms);
            return most >= HeavyMs ? DashTheme.Negative : most >= BusyMs ? DashTheme.Warning : InspectorUI.TextColor;
        });
        Info("Calls", () => Phase is { HasWindow: true } phase
            ? $"{Rate(phase.CallsPerSecond)} a second, {Rate(phase.FuelPerSecond)} fuel a second"
            : "-");
        Info("Warnings", () => WarningTotal() is var total && total == 0
            ? "none from the scripts running here"
            : $"{total:N0} from the scripts running here; each script's own are under it",
            () => WarningTotal() > 0 ? DashTheme.Warning : InspectorUI.TextColor);
        Info("Script memory", () => $"{Bytes(ScriptMemory(out int count, out _))} in {count} running here");
        Info("Process memory", () => _workingSet <= 0
            ? $"managed heap {Bytes(GC.GetTotalMemory(false))}"
            : $"{Bytes(_workingSet)} working set, managed heap {Bytes(GC.GetTotalMemory(false))}");

        Section("Engine");
        var options = ScriptInstanceOptions.Default;
        Info("Runs on", () => World?.IsAuthority == true
            ? "this machine is the host: Shared scripts run here, Local ones everywhere"
            : "this machine is a guest: Shared scripts run on the host, Local ones here");
        Info("Engine", () => "WebAssembly interpreter over register code, fuel metered");
        Info("Fuel", () => "one per instruction and 10 an engine call, counted for every call; it never runs out");
        Info("Memory", () => "what a script's module asks for, fixed when it starts");
        Info("Stops", () => $"a call still running after {options.MaxCallMilliseconds / 1000.0:0.#} s, calls {options.MaxCallDepth} deep, JSON {ScriptJson.MaxDepth} deep");
        Info("Nodes", () =>
        {
            var registry = ScriptEnvironment.Registry;
            return registry.Problems.Count == 0
                ? $"{registry.Definitions.Count} available"
                : $"{registry.Definitions.Count} available, {registry.Problems.Count} left out";
        }, () => ScriptEnvironment.Registry.Problems.Count == 0 ? InspectorUI.TextColor : DashTheme.Negative);
        Info("Warns a script at", () => $"a call over {ScriptScheduler.WarnCallMs:0} ms, {ScriptScheduler.WarnFrameMs:0} ms of a frame, "
            + $"{ScriptContextBase.WarnHandles:N0} handles, {ScriptContextBase.WarnTextBytes >> 20} MiB of text, {ScriptCollectionBudget.WarnBytes >> 20} MiB of lists, "
            + $"{options.WarnDelays:N0} delays, {options.WarnAwaits} awaits, {ScriptHostRuntime.WarnQueue} events waiting");
        Info("And in the world at", () => $"{ScriptContext.WarnRaysPerFrame} rays a frame, {ScriptContext.WarnMadePerSecond} slots a second, "
            + $"{ScriptContext.WarnMessagesPerSecond} messages a second; a person's scripts {ScriptAuthorLedger.WarnRaysPerFrame:N0} rays a frame, "
            + $"{ScriptAuthorLedger.WarnMadePerMinute} slots a minute, {ScriptAuthorLedger.WarnMemoryBytes >> 20} MiB");

        Section("Compiling");
        Info("Cache", () =>
        {
            var stats = ScriptEnvironment.CompileStats;
            return $"{stats.Cached} graphs held, {stats.CacheHits} {(stats.CacheHits == 1 ? "start" : "starts")} reused one";
        });
        Info("Compiles here", () =>
        {
            var stats = ScriptEnvironment.CompileStats;
            return stats.Compiles == 0
                ? "none yet"
                : $"{stats.Compiles}, {Ms(stats.TotalMs)} ms in all, {Ms(stats.TotalMs / stats.Compiles)} ms on average";
        });
        Info("Slowest", () => $"{Ms(ScriptEnvironment.CompileStats.SlowestMs)} ms");
        Info("On workers", () =>
        {
            int running = ScriptEnvironment.Compiling;
            int waiting = World?.ScriptsIfAny?.CompilesWaiting ?? 0;
            return running == 0 && waiting == 0
                ? "none; compiles run off the world thread"
                : $"{running} compiling, {waiting} {(waiting == 1 ? "script" : "scripts")} waiting to start";
        });
        Info("Last", () =>
        {
            var last = ScriptEnvironment.CompileStats.Last;
            return last.Hash == null
                ? "-"
                : $"{Ms(last.Milliseconds)} ms for {last.Nodes} nodes, {Bytes(last.ModuleBytes)} module{(last.Success ? "" : ", failed")}";
        });
        Info("Node registry", () => $"scanned once, in {Ms(ScriptEnvironment.CompileStats.RegistryScanMs)} ms");
    }

    private ScriptPhaseStats? Phase => World?.ScriptsIfAny?.Phase;

    private string ScriptCounts()
    {
        int shared = 0, local = 0, stopped = 0, onHost = 0;
        bool authority = World?.IsAuthority == true;
        foreach (var host in _hosts)
        {
            var view = View(host);
            if (!view.Running)
                stopped++;
            else if (view.Published)
                onHost++;
            else if (view.Local)
                local++;
            else
                shared++;
        }
        var text = $"{_hosts.Count} in this world: {shared + local} running here";
        if (shared + local > 0)
            text += $" ({shared} Shared, {local} Local)";
        if (!authority)
            text += $", {onHost} on the host";
        return stopped > 0 ? text + $", {stopped} not running" : text;
    }

    // ------------------------------------------------------------------ scripts

    private void BuildScripts()
    {
        _listed.Clear();
        var views = new List<ScriptView>(_hosts.Count);
        foreach (var host in _hosts)
            views.Add(View(host));
        // Running first, heaviest first; then the rest by name.
        static int Rank(in ScriptView view) => view.Running ? 0 : 1;
        views.Sort((a, b) =>
        {
            int rank = Rank(a).CompareTo(Rank(b));
            if (rank != 0)
                return rank;
            int cost = b.Ms.CompareTo(a.Ms);
            return cost != 0 ? cost : string.Compare(NameOf(a.Host), NameOf(b.Host), StringComparison.OrdinalIgnoreCase);
        });

        if (_selected is { IsDestroyed: false } selected)
            BuildSelected(selected);

        Section("Scripts in this world");
        Line(ScriptCounts);
        Note($"Heavy: over {Ms(HeavyMs)} ms a frame, or a frame past {Ms(HeavyPeakMs)} ms.", InspectorUI.MutedColor);
        if (!(World?.IsAuthority ?? false) && HasShared())
            Note("Shared scripts run on the host; their cost here is what the host reports while this panel is open.", InspectorUI.MutedColor);
        Buttons(("Sort by cost", "sort"));
        if (views.Count == 0)
        {
            Note("No scripts in this world.", InspectorUI.MutedColor);
            return;
        }

        for (int i = 0; i < views.Count && i < MaxScriptRows; i++)
        {
            var host = views[i].Host;
            _listed.Add(host);
            InspectorUI.FixedRow(_body!, "Script", ScriptRowHeight, out var ui, Slot);
            var button = InspectorUI.RelayButton(ui, this, "sel:" + i.ToString(Inv), string.Empty, 0f);
            if (button.Slot.GetComponentInChildren<Text>() is not { } text)
                continue;
            text.RichText.Value = true;
            text.Size.Value = InspectorUI.FontSize - 2f;
            Fit(text);
            if (text.RectTransform is { } rect)
            {
                rect.OffsetMin.Value = new float2(10f, 3f);
                rect.OffsetMax.Value = new float2(-10f, -3f);
            }
            _live.Add(new Live(text, () => ScriptRowText(host), null));
        }
        if (views.Count > MaxScriptRows)
            Note($"and {views.Count - MaxScriptRows} more, the lightest", InspectorUI.MutedColor);
    }

    private string ScriptRowText(ScriptHost host)
    {
        if (host.IsDestroyed)
            return "(gone)";
        var view = View(host);
        var sb = _text.Clear();
        sb.Append("<b>").Append(Plain(Clip(NameOf(host), 36))).Append("</b>    ");
        sb.Append("<color=").Append(Hex(BandColor(view.Band))).Append('>').Append(BandName(view.Band)).Append("</color>");
        if (view.Running)
        {
            sb.Append("    ").Append(Ms(view.Ms)).Append(" ms a frame");
            if (view.Published)
                sb.Append(" on the host");
        }
        sb.Append('\n').Append("<color=").Append(Hex(InspectorUI.MutedColor)).Append('>');
        sb.Append(view.Local ? "Local, " : "Shared, ").Append(Plain(Clip(view.Status, 48)));
        if (view.Runtime is { } runtime)
        {
            var stats = runtime.Stats;
            sb.Append("  ·  peak ").Append(Ms(stats.PeakFrameMs)).Append(" ms");
            sb.Append("  ·  ").Append(Rate(stats.CallsPerSecond)).Append(" calls/s");
            sb.Append("  ·  ").Append(Bytes(runtime.MemoryHeld));
            sb.Append("  ·  compile ").Append(Ms(stats.CompileMs)).Append(" ms");
        }
        else if (view.Published && view.CompileMs > 0)
        {
            sb.Append("  ·  compile ").Append(Ms(view.CompileMs)).Append(" ms on the host");
        }
        sb.Append("</color>");
        return sb.ToString();
    }

    private void BuildSelected(ScriptHost host)
    {
        Section("Selected: " + Clip(NameOf(host), 48));
        Info("Object", () => host.IsDestroyed ? "(gone)" : Clip(host.Slot.ParentHierarchyToString(), 60));
        Info("Author", () => AuthorOf(host));
        Info("Runs", () =>
        {
            var view = View(host);
            return view.Local ? "Local: on every machine, for whoever is there" : "Shared: on the host, as its author";
        });
        Info("Status", () => Clip(View(host).Status, 64), () => View(host).Running ? InspectorUI.TextColor : DashTheme.Warning);
        Info("Cost", () =>
        {
            var view = View(host);
            if (view.Runtime is { Stats: var stats })
                return stats.HasWindow
                    ? $"{Ms(stats.MsPerFrame)} ms a frame, peak frame {Ms(stats.PeakFrameMs)} ms, slowest call {Ms(stats.PeakCallMs)} ms"
                    : "measuring";
            return view.Published ? $"{Ms(view.Ms)} ms a frame on the host" : "not measured on this machine";
        }, () => BandColor(View(host).Band));
        Info("Calls", () => Stats(host) is { } stats
            ? $"{Rate(stats.CallsPerSecond)} a second, {stats.Calls:N0} since it started"
            : "-");
        Info("Fuel", () => Stats(host) is { } stats
            ? $"{Rate(stats.FuelPerSecond)} a second, most in one call {stats.PeakCallFuel:N0}"
            : "-");
        Info("Faults", () => Runtime(host) is { } runtime
            ? $"{runtime.Stats.Traps} traps"
                + (runtime.Stats.OutOfTime > 0 ? $", {runtime.Stats.OutOfTime} stopped after {ScriptInstanceOptions.Default.MaxCallMilliseconds / 1000.0:0.#} s" : "")
                + (runtime.Stats.OutOfFuel > 0 ? $", {runtime.Stats.OutOfFuel} out of fuel" : "")
            : "-", () => Runtime(host) is { } runtime && (runtime.Stats.Traps + runtime.Stats.OutOfFuel + runtime.Stats.OutOfTime) > 0 ? DashTheme.Warning : InspectorUI.TextColor);
        Info("Warnings", () => Runtime(host) is { Runner.Context: var context }
            ? context.Warnings == 0 ? "none" : $"{context.Warnings:N0}, last: {Clip(context.LastWarning ?? "-", 56)}"
            : "-", () => Runtime(host) is { Runner.Context.Warnings: > 0 } ? DashTheme.Warning : InspectorUI.TextColor);
        Info("Events", () => Runtime(host) is { } runtime ? $"{runtime.QueuedEvents} waiting" : "-");
        Info("Memory", () => Runtime(host) is { } runtime
            ? $"{Bytes(runtime.MemoryHeld)} working memory, {runtime.Runner.Context.HandleCount} handles"
            : "-");
        Info("Compile", () =>
        {
            var view = View(host);
            if (view.Runtime is { Stats: var stats })
                return $"{Ms(stats.CompileMs)} ms{(stats.CompileCached ? ", reused from the cache" : "")}; loaded in {Ms(stats.LoadMs)} ms";
            return view.Published && view.CompileMs > 0 ? $"{Ms(view.CompileMs)} ms on the host" : "-";
        });
        Info("Runs as", () => Runtime(host)?.Runner.Compiled is not { } compiled ? "-"
            : compiled.IsNative ? "native code"
            : compiled.NativeRefusal is { } why ? "the interpreter: " + why
            : Lumora.Wasm.WasmStore.NativeCode ? "the interpreter, native code on its way" : "the interpreter");
        Info("Graph", () => Runtime(host) is { } runtime
            ? $"{runtime.Stats.Nodes} nodes, {Bytes(runtime.Stats.ModuleBytes)} module, {runtime.Runner.Compiled.Events.Count} events, {runtime.Runner.Compiled.Timers.Count} delays, {runtime.Runner.Compiled.Variables.Count} stored values"
            : "-");
        Info("Refused writes", () =>
        {
            var runtime = Runtime(host);
            if (runtime == null)
                return "-";
            var context = runtime.Runner.Context;
            return context.RefusalCount == 0 ? "none" : $"{context.RefusalCount}, last: {Clip(context.LastRefusal ?? "", 48)}";
        }, () => Runtime(host)?.Runner.Context.RefusalCount > 0 ? DashTheme.Warning : InspectorUI.TextColor);
        Info("Running for", () => Runtime(host) is { } runtime && World is { } world
            ? Duration(TimeSpan.FromSeconds(System.Math.Max(0, world.Time.TotalTime - runtime.Stats.StartedAt)))
            : "-");
        Buttons(("Jump to it", "jump"), ("Edit graph", "edit"), ("Recompile here", "recompile-one"), ("Close", "back"));
        Line(() => _notice, () => InspectorUI.CyanColor);
    }

    // ------------------------------------------------------------------ memory

    private void BuildMemory()
    {
        Section("This machine");
        Info("Working set", () => _workingSet <= 0 ? "not available here" : Bytes(_workingSet));
        Info("Private memory", () => _processUnavailable || _privateBytes <= 0 ? "not available here" : Bytes(_privateBytes));
        Info("Managed heap", () =>
        {
            var info = GC.GetGCMemoryInfo();
            return $"{Bytes(GC.GetTotalMemory(false))} in use, {Bytes(info.HeapSizeBytes)} after the last collection, {Bytes(info.FragmentedBytes)} fragmented";
        });
        Info("Allocating", () => $"{Bytes((long)_allocatedPerSecond)} a second", () => _allocatedPerSecond >= 64 << 20 ? DashTheme.Warning : InspectorUI.TextColor);
        Info("Collections", () =>
        {
            int g0 = GC.CollectionCount(0) - _gcAtOpen[0];
            int g1 = GC.CollectionCount(1) - _gcAtOpen[1];
            int g2 = GC.CollectionCount(2) - _gcAtOpen[2];
            return $"{g0} young, {g1} middle, {g2} full since this panel opened";
        });
        Info("GC pauses", () => $"{GC.GetGCMemoryInfo().PauseTimePercentage:0.##}% of the time");
        Info("CPU", () => _processUnavailable ? "not available here" : $"{_cpuPercent:0.#}% of {Environment.ProcessorCount} cores");
        Info("Threads", () => _processUnavailable ? "-" : _threads.ToString(Inv));
        Info("Up for", () => _processUnavailable ? "-" : Duration(DateTime.Now - _processStart));
        Info("Runtime", () => $"{RuntimeInformation.FrameworkDescription}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC, {GCSettings.LatencyMode}");
        Info("System", () => $"{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");

        Section("Scripts on this machine");
        Info("Working memory", () =>
        {
            long total = ScriptMemory(out int count, out var largest);
            return count == 0
                ? "no scripts running here"
                : $"{Bytes(total)} in {count} scripts, largest {NameOf(largest!.Host)} at {Bytes(largest.MemoryHeld)}";
        });
        Info("Handles", () =>
        {
            int total = 0, most = 0;
            foreach (var runtime in Runtimes())
            {
                int held = runtime.Runner.Context.HandleCount;
                total += held;
                most = System.Math.Max(most, held);
            }
            return $"{total:N0} held, most in one script {most:N0}";
        });
        Info("Modules", () =>
        {
            long bytes = 0;
            foreach (var runtime in Runtimes())
                bytes += runtime.Stats.ModuleBytes;
            return $"{Bytes(bytes)} of code across running scripts, {ScriptEnvironment.CompileStats.Cached} compiled graphs cached";
        });

        Section("What each person's scripts hold");
        _listedAccounts.Clear();
        if (_accounts.Count == 0)
        {
            Note("No user's scripts are counted here. The world's own scripts count against nobody.", InspectorUI.MutedColor);
            return;
        }
        foreach (var account in _accounts)
        {
            var key = account.AuthorKey;
            _listedAccounts.Add(key);
            Info(Clip(_authorNames.TryGetValue(key, out var name) ? name : key, 26), () => AccountText(key),
                () => AccountTint(key));
        }
    }

    private string AccountText(string key)
    {
        foreach (var account in _accounts)
        {
            if (account.AuthorKey != key)
                continue;
            return $"{Bytes(account.Memory)} working memory, {account.Standing:N0} slots standing, "
                + $"{account.MadeLastMinute:N0} made this minute, {account.CastsThisFrame:N0} rays this frame";
        }
        return "gone";
    }

    private color AccountTint(string key)
    {
        foreach (var account in _accounts)
        {
            if (account.AuthorKey == key)
                return account.Memory > ScriptAuthorLedger.WarnMemoryBytes || account.MadeLastMinute > ScriptAuthorLedger.WarnMadePerMinute
                    ? DashTheme.Warning : InspectorUI.TextColor;
        }
        return InspectorUI.MutedColor;
    }

    // ------------------------------------------------------------------ benchmark

    private void BuildBenchmark()
    {
        Buttons(("Run benchmark", "bench"));
        Line(BenchStatus, () => _benchResults.Exists(r => !r.Ok) ? DashTheme.Negative : InspectorUI.TextColor);
        Note("Each case is a small Local script on slots only this machine has. Nothing it does leaves this machine.", InspectorUI.MutedColor);
        string? group = null;
        var cases = ScriptBenchmark.Cases;
        for (int i = 0; i < cases.Count; i++)
        {
            if (cases[i].Group != group)
            {
                group = cases[i].Group;
                Section(group);
            }
            int index = i;
            string unit = cases[i].Per switch
            {
                ScriptBenchmark.Per.Child => ", a child",
                ScriptBenchmark.Per.Person => ", a person",
                _ => string.Empty,
            };
            Info(cases[i].Name + unit, () => BenchText(index), () => BenchTint(index));
        }
    }

    private string BenchText(int index)
    {
        if (index < _benchResults.Count)
        {
            var result = _benchResults[index];
            return result.Ok
                ? $"{Us(result.MicrosecondsPerOp)} \u00b5s each, {Rate(result.OpsPerSecond)} a second, {result.FuelPerOp.ToString("0", Inv)} fuel ({result.Detail})"
                : "FAILS: " + result.Detail;
        }
        return _benchStep == index ? "running" : _benchStep >= 0 ? "waiting" : "not run";
    }

    private color BenchTint(int index)
    {
        if (index >= _benchResults.Count)
            return InspectorUI.MutedColor;
        return _benchResults[index].Ok ? InspectorUI.TextColor : DashTheme.Negative;
    }

    private string BenchStatus()
    {
        var cases = ScriptBenchmark.Cases;
        if (_benchStep >= 0)
            return $"Running case {_benchStep + 1} of {cases.Count}...";
        if (_benchResults.Count == 0)
            return "Not run yet. Takes a frame a case, and the frame rate dips while it runs.";
        int ok = _benchResults.FindAll(r => r.Ok).Count;
        double ms = 0;
        foreach (var result in _benchResults)
            ms += result.Milliseconds;
        int people = PeopleHere();
        return $"Last run {_benchRun}: {ok} of {_benchResults.Count} ran, {Ms(ms)} ms timed, {people} {(people == 1 ? "person" : "people")} here";
    }

    private int PeopleHere()
    {
        int count = 0;
        var world = World;
        if (world == null)
            return 0;
        foreach (var user in world.GetAllUsers())
        {
            if (!user.IsDestroyed)
                count++;
        }
        return count;
    }

    private void StartBenchmark()
    {
        var world = World;
        if (_benchStep >= 0 || world == null)
            return;
        _benchResults.Clear();
        _bench?.Dispose();
        _bench = new ScriptBenchmark(world);
        _benchStep = 0;
    }

    private void StepBenchmark()
    {
        if (_benchStep < 0 || _bench == null)
            return;
        var cases = ScriptBenchmark.Cases;
        if (_benchStep < cases.Count)
        {
            var bench = cases[_benchStep];
            ScriptBenchmarkResult result;
            try
            {
                result = _bench.Run(bench);
            }
            catch (Exception ex)
            {
                result = new ScriptBenchmarkResult(bench.Group, bench.Name, false, 0, 0, 0, ex.GetType().Name + ": " + ex.Message);
            }
            _benchResults.Add(result);
            _benchStep++;
        }
        if (_benchStep >= cases.Count)
        {
            _benchStep = -1;
            _bench.Dispose();
            _bench = null;
            _benchRun = DateTime.Now.ToString("HH:mm:ss", Inv);
        }
    }

    private static string Us(double microseconds) => microseconds switch
    {
        < 1 => microseconds.ToString("0.000", Inv),
        < 10 => microseconds.ToString("0.00", Inv),
        _ => microseconds.ToString("0.0", Inv),
    };

    // ------------------------------------------------------------------ self-test

    private void BuildSelfTest()
    {
        Buttons(("Run self-test", "test"), ("Recompile every script", "recompile"), ("Copy report", "copy"));
        Line(TestStatus, () => _results.Exists(r => !r.Passed) || _compiled.Exists(c => !c.Ok) ? DashTheme.Negative : InspectorUI.TextColor);
        Line(() => _notice, () => InspectorUI.CyanColor);

        Section("Engine and compiler on this machine");
        var steps = ScriptSelfTest.Steps;
        for (int i = 0; i < steps.Count; i++)
        {
            int index = i;
            Info(steps[i].Name, () => index < _results.Count
                    ? (_results[index].Passed ? "PASS  " : "FAIL  ") + _results[index].Detail
                    : _step == index ? "running" : _step >= 0 ? "waiting" : "not run",
                () => index < _results.Count ? (_results[index].Passed ? DashTheme.Positive : DashTheme.Negative) : InspectorUI.MutedColor);
        }

        Section("Every script in this world, compiled here");
        Line(CompileSummary);
        for (int i = 0; i < _compileTotal; i++)
        {
            int index = i;
            Line(() => index < _compiled.Count
                    ? $"{(_compiled[index].Ok ? "ok" : "FAILS")}  {_compiled[index].Name}: {_compiled[index].Detail}"
                    : "waiting",
                () => index < _compiled.Count ? (_compiled[index].Ok ? InspectorUI.TextColor : DashTheme.Negative) : InspectorUI.MutedColor);
        }
    }

    private string TestStatus()
    {
        var steps = ScriptSelfTest.Steps;
        if (_step >= 0)
            return $"Running step {_step + 1} of {steps.Count}...";
        if (_compileQueue.Count > 0)
            return $"Compiling scripts, {_compiled.Count} of {_compileTotal} done...";
        if (_results.Count == 0 && _compiled.Count == 0)
            return "Not run yet. Runs here, on this machine, in a few frames.";
        int passed = _results.FindAll(r => r.Passed).Count;
        int failed = _results.Count - passed;
        double ms = 0;
        foreach (var result in _results)
            ms += result.Milliseconds;
        return _results.Count == 0
            ? $"Scripts compiled {_lastRun}."
            : $"Last run {_lastRun}: {passed} passed, {failed} failed, {Ms(ms)} ms of tests";
    }

    private string CompileSummary()
    {
        if (_compileTotal == 0)
            return "Not run yet.";
        int ok = 0;
        double total = 0, slowest = 0;
        string slowestName = "";
        foreach (var (name, success, ms, _) in _compiled)
        {
            if (success)
                ok++;
            total += ms;
            if (ms > slowest)
            {
                slowest = ms;
                slowestName = name;
            }
        }
        return _compiled.Count == 0
            ? $"{_compileTotal} to compile"
            : $"{ok} of {_compiled.Count} compile, {Ms(total)} ms in all, slowest {slowestName} at {Ms(slowest)} ms";
    }

    private void StartSelfTest()
    {
        _results.Clear();
        _compiled.Clear();
        _compileQueue.Clear();
        _compileTotal = 0;
        _step = 0;
        _notice = string.Empty;
        if (_tab == Tab.SelfTest)
            Rebuild();
    }

    private void StartRecompile()
    {
        _compiled.Clear();
        _compileQueue.Clear();
        foreach (var host in _hosts)
        {
            if (!host.IsDestroyed)
                _compileQueue.Enqueue(host);
        }
        _compileTotal = _compileQueue.Count;
        if (_compileTotal == 0)
            _lastRun = DateTime.Now.ToString("HH:mm:ss", Inv);
        if (_tab == Tab.SelfTest)
            Rebuild();
    }

    private void StepSelfTest()
    {
        var steps = ScriptSelfTest.Steps;
        if (_step >= 0)
        {
            if (_step < steps.Count)
            {
                var (name, run) = steps[_step];
                ScriptSelfTestResult result;
                try
                {
                    result = run();
                }
                catch (Exception ex)
                {
                    result = new ScriptSelfTestResult(name, false, ex.GetType().Name + ": " + ex.Message, 0);
                }
                _results.Add(result);
                _step++;
            }
            if (_step >= steps.Count)
            {
                _step = -1;
                _lastRun = DateTime.Now.ToString("HH:mm:ss", Inv);
                StartRecompile();
            }
            return;
        }

        if (_compileQueue.Count == 0)
            return;
        var host = _compileQueue.Dequeue();
        _compiled.Add(Recompile(host));
        if (_compileQueue.Count == 0)
            _lastRun = DateTime.Now.ToString("HH:mm:ss", Inv);
    }

    private (string Name, bool Ok, double Ms, string Detail) Recompile(ScriptHost host)
    {
        string name = Clip(NameOf(host), 32);
        if (host.IsDestroyed)
            return (name, true, 0, "gone");
        var graph = GraphOf(host);
        if (graph == null)
            return (name, true, 0, "its graph is not here yet");
        try
        {
            var result = ScriptSelfTest.CompileFresh(graph, out double ms);
            if (result.Success)
                return (name, true, ms, $"{Ms(ms)} ms, {graph.Nodes.Count} nodes, {Bytes(result.Module?.Length ?? 0)}");
            foreach (var error in result.Errors)
            {
                if (error.NeedsNewerBuild)
                    return (name, false, ms, "needs a newer build: " + error.Message);
            }
            return (name, false, ms, string.Join("; ", result.Errors));
        }
        catch (Exception ex)
        {
            return (name, false, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // The graph this machine would run for a host: the open one when it checks clean, else the packed one.
    private static ScriptGraph? GraphOf(ScriptHost host)
    {
        if (ScriptGraphEditing.FindSpace(host) is { IsChecked: true, HasProblems: false, LastBuild: { } build })
            return build.Graph;
        return host.Graph.IsAssetAvailable ? host.Graph.Asset?.Graph : null;
    }

    // ------------------------------------------------------------------ actions

    public void HandleInspectorAction(string action)
    {
        EnsureView();
        if (action.StartsWith("tab:", StringComparison.Ordinal))
        {
            if (Enum.TryParse<Tab>(action[4..], out var tab))
                ShowTab(tab);
            return;
        }
        if (action.StartsWith("sel:", StringComparison.Ordinal))
        {
            if (int.TryParse(action[4..], NumberStyles.Integer, Inv, out int index) && index >= 0 && index < _listed.Count)
                _selected = ReferenceEquals(_selected, _listed[index]) ? null : _listed[index];
            SelectedScript.Value = _selected?.ReferenceID ?? RefID.Null;
            _notice = string.Empty;
            Rebuild();
            return;
        }

        switch (action)
        {
            case "back":
                _selected = null;
                SelectedScript.Value = RefID.Null;
                Rebuild();
                return;
            case "sort":
                Rebuild();
                return;
            case "test":
                StartSelfTest();
                return;
            case "bench":
                StartBenchmark();
                return;
            case "recompile":
                _results.Clear();
                StartRecompile();
                return;
            case "copy":
                CopyReport();
                return;
            case "jump":
                JumpTo(_selected);
                return;
            case "edit":
                if (_selected is { IsDestroyed: false } editing && ScriptGraphEditing.Unpack(editing, out var error) == null)
                    _notice = error ?? "the graph did not open";
                return;
            case "recompile-one":
                if (_selected is { IsDestroyed: false } compiling)
                {
                    var (_, ok, _, detail) = Recompile(compiling);
                    _notice = (ok ? "Compiled here: " : "Does not compile here: ") + detail;
                }
                return;
        }
    }

    private void JumpTo(ScriptHost? host)
    {
        var userRoot = World?.LocalUser?.Root?.Slot;
        if (host is not { IsDestroyed: false } || host.Slot == null || userRoot == null || userRoot.IsDestroyed)
            return;
        userRoot.GlobalPosition = host.Slot.GlobalPosition + new float3(0f, 0f, 1.2f);
    }

    private void CopyReport()
    {
        var report = BuildReport();
        var clipboard = Engine.Current?.InputInterface?.ClipboardText;
        if (clipboard != null)
        {
            clipboard.SetText(report);
            _notice = "Report copied to the clipboard.";
        }
        else
        {
            LumoraLogger.Log(report);
            _notice = "No clipboard here; the report is in the log.";
        }
    }

    // Everything the panel shows, as plain text, for pasting into a bug report.
    private string BuildReport()
    {
        var world = World;
        var sb = new StringBuilder(4096);
        sb.Append("Loom diagnostic, ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append('\n');
        sb.Append("World: ").Append(world?.WorldName.Value ?? "-").Append(world?.IsAuthority == true ? " (host)" : " (guest)").Append('\n');
        sb.Append("Runtime: ").Append(RuntimeInformation.FrameworkDescription).Append(", ").Append(RuntimeInformation.OSDescription)
            .Append(", ").Append(RuntimeInformation.ProcessArchitecture).Append(", ").Append(Environment.ProcessorCount).Append(" cores\n");
        sb.Append("Memory: working set ").Append(Bytes(_workingSet)).Append(", managed heap ").Append(Bytes(GC.GetTotalMemory(false)))
            .Append(", allocating ").Append(Bytes((long)_allocatedPerSecond)).Append("/s, CPU ").Append(_cpuPercent.ToString("0.#", Inv)).Append("%\n");
        if (Phase is { HasWindow: true } phase)
            sb.Append("Script phase: ").Append(Ms(phase.AverageMs)).Append(" ms avg, ").Append(Ms(phase.PeakMs)).Append(" ms peak, ")
                .Append(Rate(phase.CallsPerSecond)).Append(" calls/s, ").Append(Rate(phase.FuelPerSecond)).Append(" fuel/s\n");
        var compile = ScriptEnvironment.CompileStats;
        sb.Append("Compiles: ").Append(compile.Compiles).Append(", ").Append(Ms(compile.TotalMs)).Append(" ms in all, slowest ")
            .Append(Ms(compile.SlowestMs)).Append(" ms, cache ").Append(compile.Cached).Append(", reused ").Append(compile.CacheHits)
            .Append(", registry scan ").Append(Ms(compile.RegistryScanMs)).Append(" ms\n");

        sb.Append("\nScripts (").Append(_hosts.Count).Append("):\n");
        foreach (var host in _hosts)
        {
            var view = View(host);
            sb.Append("  ").Append(NameOf(host)).Append(" | ").Append(view.Local ? "Local" : "Shared").Append(" | ").Append(BandName(view.Band))
                .Append(" | ").Append(Ms(view.Ms)).Append(" ms/frame").Append(view.Published ? " on host" : "");
            if (view.Runtime is { } runtime)
            {
                var stats = runtime.Stats;
                sb.Append(" | peak ").Append(Ms(stats.PeakFrameMs)).Append(" ms | ").Append(Rate(stats.CallsPerSecond)).Append(" calls/s | ")
                    .Append(Bytes(runtime.MemoryHeld)).Append(" | compile ").Append(Ms(stats.CompileMs)).Append(" ms | load ")
                    .Append(Ms(stats.LoadMs)).Append(" ms | ").Append(stats.Nodes).Append(" nodes | faults ")
                    .Append(stats.Traps + stats.OutOfFuel + stats.OutOfTime).Append(" | warnings ").Append(runtime.Runner.Context.Warnings);
            }
            sb.Append(" | ").Append(view.Status).Append('\n');
        }

        if (_accounts.Count > 0)
        {
            sb.Append("\nAuthors:\n");
            foreach (var account in _accounts)
                sb.Append("  ").Append(_authorNames.TryGetValue(account.AuthorKey, out var name) ? name : account.AuthorKey).Append(": ")
                    .Append(AccountText(account.AuthorKey)).Append('\n');
        }

        if (_benchResults.Count > 0)
        {
            sb.Append("\nBenchmark ").Append(_benchRun).Append(":\n");
            foreach (var result in _benchResults)
            {
                sb.Append("  ").Append(result.Group).Append(" / ").Append(result.Name).Append(": ");
                if (result.Ok)
                    sb.Append(Us(result.MicrosecondsPerOp)).Append(" us each, ").Append(Rate(result.OpsPerSecond)).Append("/s, ")
                        .Append(result.FuelPerOp.ToString("0", Inv)).Append(" fuel (").Append(result.Detail).Append(")\n");
                else
                    sb.Append("FAILS ").Append(result.Detail).Append('\n');
            }
        }
        if (_results.Count > 0)
        {
            sb.Append("\nSelf-test ").Append(_lastRun).Append(":\n");
            foreach (var result in _results)
                sb.Append("  ").Append(result.Passed ? "PASS " : "FAIL ").Append(result.Name).Append(": ").Append(result.Detail).Append('\n');
        }
        if (_compiled.Count > 0)
        {
            sb.Append("\nCompiled here:\n");
            foreach (var (name, ok, _, detail) in _compiled)
                sb.Append("  ").Append(ok ? "ok    " : "FAILS ").Append(name).Append(": ").Append(detail).Append('\n');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ measuring

    private ScriptView View(ScriptHost host)
    {
        bool local = host.Mode.Value == ScriptMode.Local;
        if (local)
        {
            var runtime = host.LocalRuntime;
            string status = string.IsNullOrEmpty(host.LocalStatus) ? "not started" : host.LocalStatus;
            bool running = runtime is { CanRun: true };
            return new ScriptView(host, runtime, true, running, false, running ? runtime!.Stats.MsPerFrame : 0,
                runtime?.Stats.CompileMs ?? 0, status, BandOf(runtime, running));
        }

        string shared = string.IsNullOrEmpty(host.Status.Value) ? "not started" : host.Status.Value!;
        if (World?.IsAuthority == true)
        {
            var runtime = host.SharedRuntime;
            bool running = runtime is { CanRun: true };
            return new ScriptView(host, runtime, false, running, false, running ? runtime!.Stats.MsPerFrame : 0,
                runtime?.Stats.CompileMs ?? 0, shared, BandOf(runtime, running));
        }

        bool hostRunning = shared.StartsWith("running", StringComparison.Ordinal);
        double ms = hostRunning ? host.HostMs.Value : 0;
        var band = !hostRunning ? CostBand.Stopped
            : ms >= HeavyMs ? CostBand.Heavy : ms >= BusyMs ? CostBand.Busy : ms > 0 ? CostBand.Light : CostBand.Idle;
        return new ScriptView(host, null, false, hostRunning, true, ms, host.HostCompileMs.Value, shared, band);
    }

    private static CostBand BandOf(ScriptHostRuntime? runtime, bool running)
        => running ? Band(runtime!.Stats) : CostBand.Stopped;

    private static CostBand Band(ScriptRuntimeStats stats)
    {
        if (!stats.HasWindow)
            return stats.Calls == 0 ? CostBand.Idle : CostBand.Light;
        if (stats.MsPerFrame >= HeavyMs || stats.PeakFrameMs >= HeavyPeakMs)
            return CostBand.Heavy;
        if (stats.MsPerFrame >= BusyMs)
            return CostBand.Busy;
        return stats.CallsPerSecond > 0 ? CostBand.Light : CostBand.Idle;
    }

    private static string BandName(CostBand band) => band switch
    {
        CostBand.Stopped => "STOPPED",
        CostBand.Idle => "IDLE",
        CostBand.Light => "LIGHT",
        CostBand.Busy => "BUSY",
        _ => "HEAVY",
    };

    private static color BandColor(CostBand band) => band switch
    {
        CostBand.Stopped => DashTheme.TextMuted,
        CostBand.Idle => DashTheme.TextDim,
        CostBand.Light => DashTheme.Positive,
        CostBand.Busy => DashTheme.Warning,
        _ => DashTheme.Negative,
    };

    private ScriptHostRuntime? Runtime(ScriptHost host)
        => host.IsDestroyed ? null : host.Mode.Value == ScriptMode.Local ? host.LocalRuntime : host.SharedRuntime;

    private ScriptRuntimeStats? Stats(ScriptHost host) => Runtime(host)?.Stats;

    private int WarningTotal()
    {
        int total = 0;
        foreach (var runtime in Runtimes())
            total += runtime.Runner.Context.Warnings;
        return total;
    }

    private IEnumerable<ScriptHostRuntime> Runtimes()
    {
        foreach (var host in _hosts)
        {
            if (host.SharedRuntime is { CanRun: true } shared)
                yield return shared;
            if (host.LocalRuntime is { CanRun: true } local)
                yield return local;
        }
    }

    private long ScriptMemory(out int count, out ScriptHostRuntime? largest)
    {
        long total = 0;
        count = 0;
        largest = null;
        foreach (var runtime in Runtimes())
        {
            total += runtime.MemoryHeld;
            count++;
            if (largest == null || runtime.MemoryHeld > largest.MemoryHeld)
                largest = runtime;
        }
        return total;
    }

    private string AuthorOf(ScriptHost host)
    {
        if (Runtime(host) is { } runtime)
            return runtime.Principal.IsWorldContent ? "the world" : runtime.Principal.DisplayName ?? runtime.Principal.AuthorKey;
        return ScriptPrincipal.ForElement(host) is { } principal
            ? principal.IsWorldContent ? "the world" : principal.DisplayName ?? principal.AuthorKey
            : "not in the world";
    }

    private void SampleProcess()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = _sampleTicks == 0 ? 0 : (now - _sampleTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_sampleTicks != 0 && seconds < ProcessSeconds)
            return;

        long allocated = GC.GetTotalAllocatedBytes(false);
        if (seconds > 0)
            _allocatedPerSecond = (allocated - _lastAllocated) / seconds;
        _lastAllocated = allocated;

        _workingSet = Environment.WorkingSet;
        if (!_processUnavailable)
        {
            try
            {
                _process ??= System.Diagnostics.Process.GetCurrentProcess();
                if (_processStart == default)
                    _processStart = _process.StartTime;
                var cpu = _process.TotalProcessorTime;
                if (seconds > 0)
                    _cpuPercent = (cpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100.0;
                _lastCpu = cpu;

                if (_tab == Tab.Memory && now >= _nextDetailTicks)
                    ReadProcessDetail();
            }
            catch (Exception)
            {
                // Some platforms keep these to themselves; the panel says so instead of guessing.
                _processUnavailable = true;
            }
        }
        _sampleTicks = now;
    }

    // Private memory and threads walk every process on the machine on some platforms, a few milliseconds
    // on the world thread, so they are read when the Memory tab opens and every five seconds while it shows.
    private void ReadProcessDetail()
    {
        if (_processUnavailable)
            return;
        try
        {
            _process ??= System.Diagnostics.Process.GetCurrentProcess();
            _nextDetailTicks = System.Diagnostics.Stopwatch.GetTimestamp() + 5 * System.Diagnostics.Stopwatch.Frequency;
            _process.Refresh();
            _privateBytes = _process.PrivateMemorySize64;
            _threads = _process.Threads.Count;
        }
        catch (Exception)
        {
            _processUnavailable = true;
        }
    }

    // ------------------------------------------------------------------ text

    private static string NameOf(ScriptHost host)
    {
        var name = host.Slot?.Name.Value;
        return string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    // Rich text tags come from the panel alone; a name that looks like one shows as written.
    private static string Plain(string text) => text.Replace('<', '(').Replace('>', ')');

    private static string Hex(in color value)
    {
        static int Byte(float channel) => (int)MathF.Round(System.Math.Clamp(channel, 0f, 1f) * 255f);
        return $"#{Byte(value.r):X2}{Byte(value.g):X2}{Byte(value.b):X2}";
    }

    private static string Ms(double ms) => ms switch
    {
        < 0.1 => ms.ToString("0.000", Inv),
        < 10 => ms.ToString("0.00", Inv),
        _ => ms.ToString("0.0", Inv),
    };

    private static string Rate(double perSecond) => perSecond switch
    {
        >= 1e6 => (perSecond / 1e6).ToString("0.0", Inv) + "M",
        >= 1e3 => (perSecond / 1e3).ToString("0.0", Inv) + "k",
        _ => perSecond.ToString("0.#", Inv),
    };

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.00", Inv) + " GiB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", Inv) + " MiB",
        >= 1L << 10 => (bytes / 1024.0).ToString("0.#", Inv) + " KiB",
        _ => bytes.ToString(Inv) + " B",
    };

    private static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{span.Minutes} min {span.Seconds} s" : $"{System.Math.Max(0, span.Seconds)} s";
}

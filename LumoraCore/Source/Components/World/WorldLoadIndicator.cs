// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Helio.UI;
using LumoraMeshes = Lumora.Core.Components.Meshes;

namespace Lumora.Core.Components;

// One local card in userspace, shared by joins and saved-world loads. It survives the focus switch,
// then retires after this arrival; later imports and texture upgrades cannot reopen it.
//
// It stands 1.1 m out, which is usually inside the world being left, so every part of it draws in its
// own overlay band with no depth test: walls and floors never cut through it and the panel never covers
// its own text. The percentage is the stage's share of the bar plus how much of that stage's count is
// done; a stage with no count holds at its start rather than guess. -xlinka
[ComponentCategory("Hidden")]
public sealed class WorldLoadIndicator : Component
{
    private const float Width = 0.64f;
    private const float Height = 0.25f;
    private const float Inset = 0.032f;
    private const float BarWidth = Width - 2f * Inset;
    private const float BarHeight = 0.008f;
    private const float ShowAfterSeconds = 0.35f;
    private const float AfterEntrySeconds = 8f;
    private const float LingerSeconds = 1.0f;

    private enum Stage { Connect, Load, Build, Finish }

    // Where each stage starts on the bar. Finish is the long tail on a fresh machine: shader compiles,
    // uploads and textures.
    private static readonly float[] StageStart = { 0f, 0.12f, 0.32f, 0.62f, 1f };

    private readonly HashSet<World> _seen = new();
    private readonly List<(TextRenderer Label, int Order)> _unlifted = new();
    private readonly TextRenderer?[] _stages = new TextRenderer?[4];
    private World? _target;
    private Slot? _plate;
    private Slot? _fill;
    private TextRenderer? _percent;
    private TextRenderer? _title;
    private TextRenderer? _detail;
    private Stage _stage;
    private int _peak;
    private float _progress;
    private float _bar;
    private float _age;
    private float _enteredFor;
    private float _idleFor;
    private float _refreshIn;
    private bool _visible;
    private bool _placed;

    private static WorldLoadIndicator? _shown;

    // The card stands in front of the view, so pictures of the world wait until it has gone. -xlinka
    public static bool Showing => _shown != null;

    public static void Attach(World world)
    {
        if (world == null || world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (world.IsDestroyed || world.RootSlot == null)
                return;
            foreach (var child in world.RootSlot.Children)
                if (child.GetComponent<WorldLoadIndicator>() != null)
                    return;
            var slot = world.RootSlot.AddLocalSlot("World Load Indicator");
            slot.Persistent.Value = false;
            slot.AttachComponent<WorldLoadIndicator>();
        });
    }

    public override void OnInit()
    {
        base.OnInit();
        Compose();
        SetVisible(false);
    }

    private void Compose()
    {
        var theme = NameplateTheme.For(World);
        _plate = Slot.AddLocalSlot("Loading card");
        int band = UserspaceRenderBands.LoadingCard;

        AddQuad(_plate, "Panel", 0f, new float2(Width, Height), 0.022f, DashTheme.Panel, theme, band);

        var edge = _plate.AddSlot("Edge");
        var ring = edge.AttachComponent<LumoraMeshes.RoundedQuadRingMesh>();
        ring.Size.Value = new float2(Width, Height);
        ring.CornerRadius.Value = 0.022f;
        ring.RimWidth.Value = 0.0016f;
        ring.Color = new color(DashTheme.Accent.r, DashTheme.Accent.g, DashTheme.Accent.b, 0.5f);
        AttachRenderer(edge, ring, theme, band + 1);

        var track = AddQuad(_plate, "Progress track", -0.094f, new float2(BarWidth, BarHeight), BarHeight * 0.5f,
            DashTheme.SurfaceHover, theme, band + 2);
        _fill = AddQuad(track, "Progress", 0f, new float2(BarWidth, BarHeight), BarHeight * 0.5f,
            DashTheme.Accent, theme, band + 3);

        float left = -BarWidth * 0.5f;
        int ink = band + 4;
        AddText("Heading", left, 0.094f, 0.019f, DashTheme.AccentHover, TextHorizontalAlignment.Left,
            "WorldLoad.Title".AsLocale("Loading world").Resolve(), theme, ink);
        _percent = AddText("Percent", -left, 0.080f, 0.044f, DashTheme.Text, TextHorizontalAlignment.Right,
            "0%", theme, ink);
        _title = AddText("World name", left, 0.056f, 0.034f, DashTheme.Text, TextHorizontalAlignment.Left,
            "", theme, ink);

        string[] names =
        {
            "WorldLoad.StageConnect".AsLocale("Connect").Resolve(),
            "WorldLoad.StageLoad".AsLocale("Load").Resolve(),
            "WorldLoad.StageBuild".AsLocale("Build").Resolve(),
            "WorldLoad.StageFinish".AsLocale("Finish").Resolve(),
        };
        for (int i = 0; i < _stages.Length; i++)
            _stages[i] = AddText("Stage " + names[i], left + i * (BarWidth / _stages.Length), 0.008f, 0.018f,
                DashTheme.TextMuted, TextHorizontalAlignment.Left, names[i], theme, ink);

        _detail = AddText("Detail", left, -0.040f, 0.020f, DashTheme.TextDim, TextHorizontalAlignment.Left,
            "", theme, ink);
    }

    private static Slot AddQuad(Slot parent, string name, float y, float2 size, float radius, color fill,
        NameplateTheme? theme, int order)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = new float3(0f, y, 0f);
        var mesh = slot.AttachComponent<LumoraMeshes.RoundedQuadMesh>();
        mesh.Size.Value = size;
        mesh.CornerRadius.Value = radius;
        mesh.Color = fill;
        AttachRenderer(slot, mesh, theme, order);
        return slot;
    }

    private static void AttachRenderer(Slot slot, LumoraMeshes.ProceduralMesh mesh, NameplateTheme? theme, int order)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        if (theme?.PlateMaterial != null)
            renderer.Material.Target = theme.PlateMaterial;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        renderer.SetOverlayOrder(order, depthTest: false);
    }

    private TextRenderer AddText(string name, float x, float y, float size, color ink, TextHorizontalAlignment align,
        string text, NameplateTheme? theme, int order)
    {
        var slot = _plate!.AddSlot(name);
        slot.LocalPosition.Value = new float3(x, y, 0f);
        var label = slot.AttachComponent<TextRenderer>();
        if (theme?.Font != null)
            label.Font.Target = theme.Font;
        label.Size.Value = size;
        label.Color.Value = ink;
        label.HorizontalAlign.Value = align;
        label.VerticalAlign.Value = TextVerticalAlignment.Middle;
        label.OutlineThickness.Value = 0f;
        label.Text.Value = text;
        _unlifted.Add((label, order));
        return label;
    }

    // A text renderer builds its mesh slot when it starts, so each label is lifted once that is there.
    private void LiftLabels()
    {
        for (int i = _unlifted.Count - 1; i >= 0; i--)
        {
            var (label, order) = _unlifted[i];
            var renderer = label.IsDestroyed ? null : label.Slot.FindChild("TextRenderer")?.GetComponent<MeshRenderer>();
            if (renderer == null && !label.IsDestroyed)
                continue;
            renderer?.SetOverlayOrder(order, depthTest: false);
            _unlifted.RemoveAt(i);
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var manager = Engine.Current?.WorldManager;
        if (manager == null)
            return;
        var operation = Engine.Current?.WorldLoadingService?.CurrentOperation;
        // A join waiting on a verified email: the card makes way for its pressable panel, in the same spot.
        if (operation is { IsHeld: true, IsCancelled: false })
        {
            JoinHoldPanel.Show(operation);
            if (_target != null || _visible)
                Dismiss();
            return;
        }
        bool joining = operation != null && !operation.IsCancelled && !operation.IsFailed && !operation.IsComplete;
        var candidate = (joining ? operation!.World : null) ?? manager.LoadingWorld ?? manager.FocusedWorld;
        if (candidate != null && candidate != World && !candidate.IsDestroyed && _seen.Add(candidate))
        {
            if (joining || candidate.IsLoading || candidate.IsSettling)
            {
                _target = candidate;
                _age = _enteredFor = _idleFor = _refreshIn = 0f;
                _progress = _bar = 0f;
                _stage = Stage.Connect;
                _peak = 0;
                _placed = false;
            }
        }

        var target = _target;
        if (target == null)
            return;
        bool failed = target.IsDestroyed || target.State == World.WorldState.Failed
            || (operation?.World == target && (operation.IsCancelled || operation.IsFailed));
        bool wanted = manager.FocusedWorld == target || manager.PendingFocusWorld == target
            || (joining && operation!.World == target);
        if (failed || !wanted)
        {
            Dismiss();
            return;
        }

        _age += delta;
        bool entered = manager.FocusedWorld == target && target.State == World.WorldState.Running;
        if (entered)
            _enteredFor += delta;
        // Global asset work is useful only as a brief arrival hint. It must never extend this
        // deadline: other people can keep adding content indefinitely.
        bool busy = target.IsLoading || target.IsSettling || Asset.LoadingCount > 0;
        _idleFor = busy ? 0f : _idleFor + delta;
        if (_enteredFor >= AfterEntrySeconds || _idleFor >= LingerSeconds || _age >= 75f)
        {
            Dismiss();
            return;
        }
        SetVisible(_age >= ShowAfterSeconds);
        if (!_visible)
            return;

        LiftLabels();
        UpdatePosition(delta);

        _refreshIn -= delta;
        if (_refreshIn <= 0f)
        {
            _refreshIn = 0.1f;
            Refresh(target, operation?.World == target ? operation : null, busy);
        }

        _bar += (_progress - _bar) * (1f - MathF.Exp(-10f * delta));
        if (_fill != null)
        {
            float width = System.Math.Clamp(_bar, 0.002f, 1f);
            _fill.LocalScale.Value = new float3(width, 1f, 1f);
            _fill.LocalPosition.Value = new float3(-BarWidth * 0.5f * (1f - width), 0f, 0f);
        }
    }

    private void Refresh(World target, Management.WorldLoadingOperation? operation, bool busy)
    {
        var state = target.State;
        Stage live;
        int left = -1;
        float known = -1f;
        string detail;

        if (!busy)
        {
            live = Stage.Finish;
            left = 0;
            detail = "WorldLoad.Done".AsLocale("Ready").Resolve();
        }
        else if (target.IsStagedLoading)
        {
            live = Stage.Load;
            int done = target.StagedLoadCount;
            int queued = target.StagedLoadLeft;
            if (queued >= 0 && done + queued > 0)
                known = (float)done / (done + queued);
            detail = "WorldLoad.LoadedObjects".AsLocale("{0} objects loaded", done.ToString("N0")).Resolve();
        }
        else if (state is World.WorldState.Created or World.WorldState.InitializingNetwork or World.WorldState.WaitingForJoinGrant)
        {
            live = Stage.Connect;
            // A join reports 0.45 once the world data starts to arrive, which is where Load takes over.
            if (operation != null)
                known = operation.Progress / 0.45f;
            detail = target.LoadStateDescription.Resolve();
        }
        else if (target.IsSessionStartPending || state != World.WorldState.Running)
        {
            live = Stage.Load;
            detail = target.LoadStateDescription.Resolve();
        }
        else if (target.PendingHookCreations > 0)
        {
            live = Stage.Build;
            left = target.PendingHookCreations;
            detail = "WorldLoad.BuildLeft".AsLocale("{0} objects left to build", left.ToString("N0")).Resolve();
        }
        else
        {
            live = Stage.Finish;
            int assets = Asset.LoadingCount;
            left = target.CountSettle().Total + assets;
            string settle = target.DescribeSettle();
            if (assets > 0)
            {
                string loading = "WorldLoad.AssetsLoading".AsLocale("{0} assets loading", assets.ToString("N0")).Resolve();
                settle = settle.Length > 0 ? settle + " · " + loading : loading;
            }
            detail = _enteredFor >= AfterEntrySeconds - 1f
                ? "WorldLoad.Background".AsLocale("Finishing in the background").Resolve()
                : settle.Length > 0 ? settle
                : "WorldLoad.Textures".AsLocale("Bringing your world into view").Resolve();
        }

        // Never a step back: builds queued by a late spawn once finishing began are still finishing, as far
        // as the person waiting can tell.
        if (live > _stage)
        {
            _stage = live;
            _peak = 0;
        }
        float within = 0f;
        if (left >= 0)
        {
            _peak = System.Math.Max(_peak, left);
            within = _peak > 0 ? 1f - (float)left / _peak : 1f;
        }
        else if (known >= 0f)
        {
            within = System.Math.Clamp(known, 0f, 1f);
        }
        int at = (int)_stage;
        float overall = busy ? MathF.Min(StageStart[at] + (StageStart[at + 1] - StageStart[at]) * within, 0.99f) : 1f;
        _progress = MathF.Max(_progress, overall);

        _title!.Text.Value = FitName(Management.WorldManager.DisplayName(target));
        _percent!.Text.Value = $"{(int)MathF.Floor(_progress * 100f)}%";
        _detail!.Text.Value = detail;
        for (int i = 0; i < _stages.Length; i++)
        {
            if (_stages[i] is { } label)
                label.Color.Value = !busy || i < at ? DashTheme.AccentHover : i == at ? DashTheme.Text : DashTheme.TextMuted;
        }

        // Unusually wide glyphs and translated lines still fit the fixed card. Measure is supplied by text
        // meshing; scale the label, not its font, so fitting cannot oscillate.
        FitLabel(_title, BarWidth - 0.15f);
        FitLabel(_detail, BarWidth);
    }

    private static void FitLabel(TextRenderer label, float room)
    {
        float width = label.RenderedSize.x;
        float scale = width > room ? room / width : 1f;
        label.Slot.LocalScale.Value = new float3(scale, scale, 1f);
    }

    private static string FitName(string? value)
    {
        string name = (value ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        // TextRenderer has no wrapping. Keep even wide glyphs beside the percentage.
        return name.Length <= 26 ? name : name[..25] + "…";
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(_shown, this))
            _shown = null;
        base.OnDestroy();
    }

    private void Dismiss()
    {
        _target = null;
        SetVisible(false);
        _seen.RemoveWhere(world => world.IsDestroyed);
    }

    private void SetVisible(bool visible)
    {
        if (_visible == visible && _plate?.ActiveSelf.Value == visible)
            return;
        _visible = visible;
        if (visible)
            _shown = this;
        else if (ReferenceEquals(_shown, this))
            _shown = null;
        if (_plate != null && !_plate.IsDestroyed)
            _plate.ActiveSelf.Value = visible;
        if (!visible)
            _placed = false;
    }

    private void UpdatePosition(float delta)
    {
        var head = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root?.HeadSlot
            ?? World?.LocalUser?.Root?.HeadSlot;
        if (head == null || head.IsDestroyed)
            return;
        var target = head.GlobalPosition + (head.GlobalRotation * float3.Backward) * 1.1f - float3.Up * 0.14f;
        Slot.GlobalPosition = _placed
            ? float3.Lerp(Slot.GlobalPosition, target, 1f - MathF.Exp(-5f * delta)) : target;
        if (_plate != null && Utility.UserFacing.TryLookRotation(Slot.GlobalPosition, head.GlobalPosition,
                float3.Up, yawOnly: false, out var facing))
            _plate.GlobalRotation = facing;
        _placed = true;
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// Turns a mesh collider, or what a renderer draws, into several convex hull colliders.
//
// Everyone sees the panel and the coloured shells of a preview; only the person who opened it can press it,
// and the split runs on their machine, off the world thread. Each setting is a row of chips rather than a
// slider: a hand at arm's length hits a chip first time, and the few values worth trying are all there is.
// Apply puts the hulls on the object's own slot with the old collider's settings, removes the mesh collider,
// and is one undo step. -xlinka
[ComponentCategory("Utility/Inspectors")]
public sealed class ConvexSplitDialog : Component
{
    private static readonly float2 CanvasSize = new(460f, 575f);
    private const float PhysicalHeight = 0.55f;

    // the cutter takes at most 100000 voxels, 32 hulls and a concavity of 0.001; past that only a finer plane
    // search is left to give
    private static readonly (int Resolution, float Concavity, int PlaneStep)[] DetailLevels =
    {
        (20000, 0.01f, 4), (50000, 0.0025f, 4), (100000, 0.001f, 4), (100000, 0.001f, 1),
    };
    private static readonly int[] HullCounts = { 4, 8, 16, 24, 32 };
    private static readonly int[] PointCounts = { 16, 24, 32, 48, 64 };
    private static readonly int[] PartSizes = { 0, 1, 2, 5 };

    public readonly SyncRef<Component> Target = new();
    public readonly SyncRef<User> Owner = new();
    public readonly Sync<int> Detail = new(1);
    public readonly Sync<int> MaxHulls = new(16);
    public readonly Sync<int> MaxPoints = new(32);
    public readonly Sync<int> SmallestPartPercent = new(2);
    private readonly SyncRef<Slot> _preview = new();

    private sealed class Chip
    {
        public required Sync<int> Field;
        public required int Value;
        public required ColorDriver Driver;
        public required Text Label;
    }

    private bool _attachedHere;
    private bool _built;
    private FontProvider? _font;
    private readonly List<Chip> _chips = new();
    private Text? _status;
    private Text? _stopLabel;
    private RectTransform? _barFill;
    private RoundedPanel? _barPanel;

    private CancellationTokenSource? _cancel;
    private Task<ConvexSplitResult>? _running;
    private ConvexSplitResult? _result;
    private bool _applyWhenDone;
    private double _startedAt;
    private double _lastTick;

    private static readonly LocaleText TitleText = LocaleText.Keyed("ConvexSplit.Title", "Split Into Convex Hulls");
    private static readonly LocaleText DetailText = LocaleText.Keyed("ConvexSplit.Detail", "Detail");
    private static readonly LocaleText HullsText = LocaleText.Keyed("ConvexSplit.MaxHulls", "Most Hulls");
    private static readonly LocaleText PointsText = LocaleText.Keyed("ConvexSplit.MaxPoints", "Points Per Hull");
    private static readonly LocaleText PartText = LocaleText.Keyed("ConvexSplit.SmallestPart", "Smallest Part");
    private static readonly LocaleText OffText = LocaleText.Keyed("ConvexSplit.Off", "Off");
    private static readonly LocaleText[] DetailNames =
    {
        LocaleText.Keyed("ConvexSplit.Detail.Coarse", "Coarse"),
        LocaleText.Keyed("ConvexSplit.Detail.Medium", "Medium"),
        LocaleText.Keyed("ConvexSplit.Detail.Fine", "Fine"),
        LocaleText.Keyed("ConvexSplit.Detail.Finest", "Finest"),
    };
    private static readonly LocaleText PreviewText = LocaleText.Keyed("ConvexSplit.Preview", "Preview");
    private static readonly LocaleText ApplyText = LocaleText.Keyed("ConvexSplit.Apply", "Apply");
    private static readonly LocaleText CancelText = LocaleText.Keyed("ConvexSplit.Cancel", "Cancel");
    private static readonly LocaleText StopText = LocaleText.Keyed("ConvexSplit.Stop", "Stop");
    private static readonly LocaleText ReadyText = LocaleText.Keyed("ConvexSplit.Ready", "Preview to see the parts, or Apply straight away.");
    private static readonly LocaleText StoppedText = LocaleText.Keyed("ConvexSplit.Stopped", "Stopped.");
    private static readonly LocaleText NoMeshText = LocaleText.Keyed("ConvexSplit.NoMesh", "The mesh has not loaded yet.");
    private static readonly LocaleText UnavailableText = LocaleText.Keyed("ConvexSplit.Unavailable", "Splitting is not available on this build.");
    private static readonly LocaleText NothingText = LocaleText.Keyed("ConvexSplit.Nothing", "No parts came out. Try more detail.");
    private static readonly LocaleText RefusedText = LocaleText.Keyed("ConvexSplit.Refused", "You cannot change this object here.");
    private static readonly LocaleText FailedText = LocaleText.Keyed("ConvexSplit.Failed", "The split failed: {0}");
    private static readonly LocaleText ColliderKind = LocaleText.Keyed("ConvexSplit.Source.Collider", "mesh collider");
    private static readonly LocaleText RendererKind = LocaleText.Keyed("ConvexSplit.Source.Renderer", "rendered mesh");
    private static LocaleText Working(string seconds) => LocaleText.Keyed("ConvexSplit.Working", "Splitting... {0} s", seconds);
    private static LocaleText Done(int hulls, int points, string seconds)
        => LocaleText.Keyed("ConvexSplit.Done", "{0} hulls, {1} points, {2} s", hulls, points, seconds);
    private static LocaleText SourceInfo(string triangles, string kind)
        => LocaleText.Keyed("ConvexSplit.Source", "{0} triangles, {1}", triangles, kind);

    // The dialog for target, in front of the local user. Target is a mesh collider or a mesh renderer.
    public static ConvexSplitDialog? Open(Component target)
    {
        var world = target?.World;
        var user = world?.LocalUser;
        if (world?.RootSlot == null || user == null || target!.Slot == null || target.IsDestroyed)
            return null;

        foreach (var open in world.RootSlot.GetComponentsInChildren<ConvexSplitDialog>())
        {
            if (!open.IsDestroyed && ReferenceEquals(open.Target.Target, target) && open.Owner.Target == user)
                return open;
        }

        var slot = world.AddLocalUserSlot(world.RootSlot, TitleText.Resolve());
        slot.Persistent.Value = false;
        var head = world.LocalUser?.Root?.HeadSlot;
        if (head != null && !head.IsDestroyed)
        {
            float3 forward = head.GlobalRotation * float3.Backward;
            forward.y = 0f;
            forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
            slot.GlobalPosition = head.GlobalPosition + forward * 0.75f + new float3(0f, -0.12f, 0f);
            slot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(-forward.x, -forward.z));
        }

        var dialog = slot.AttachComponent<ConvexSplitDialog>();
        dialog.Target.Target = target;
        dialog.Owner.Target = user;
        return dialog;
    }

    public override void OnAttach()
    {
        base.OnAttach();
        _attachedHere = true;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (_attachedHere)
        {
            using (World.EnterLocalUserAllocation())
                Build();
        }
    }

    public override void OnUserLeft(User user) => ImportDialog.TakeDownForLeaver(this, Owner, user);

    public override void OnDestroy()
    {
        _cancel?.Cancel();
        _cancel = null;
        _running = null;
        ClearPreview();
        base.OnDestroy();
    }

    private bool MayUse => IsExecutionAllowed && Owner.Target is { } user && ReferenceEquals(user, World?.LocalUser);

    public bool IsRunning => _running != null;

    public ConvexSplitResult? Result => _result;

    public ConvexSplitSettings Settings => new()
    {
        Resolution = DetailLevels[System.Math.Clamp(Detail.Value, 0, DetailLevels.Length - 1)].Resolution,
        MaxConcavity = DetailLevels[System.Math.Clamp(Detail.Value, 0, DetailLevels.Length - 1)].Concavity,
        PlaneStep = DetailLevels[System.Math.Clamp(Detail.Value, 0, DetailLevels.Length - 1)].PlaneStep,
        MaxHulls = System.Math.Clamp(MaxHulls.Value, 1, 32),
        MaxPointsPerHull = System.Math.Clamp(MaxPoints.Value, 8, 255),
        SmallestPart = System.Math.Clamp(SmallestPartPercent.Value, 0, 50) / 100f,
    };

    // LAYOUT

    private void Build()
    {
        if (_built)
            return;
        _built = true;

        Slot.LocalScale.Value = float3.One * (PhysicalHeight / CanvasSize.y);
        var panel = Slot.GetOrAttachComponent<PanelShell>();
        panel.Title.Value = TitleText.Resolve();
        panel.Size.Value = CanvasSize;
        var fontUrl = ImportDialog.ResolveFontUrl(World);
        if (fontUrl != null)
        {
            var fontSlot = Slot.AddSlot("DialogFont");
            _font = fontSlot.AttachComponent<FontProvider>();
            _font.URL.Value = fontUrl;
            _font.FallbackURLs.Add(DashTheme.FontRegular);
            panel.Font.Target = _font;
        }
        panel.RebuildContent(BuildContent);
        ImportDialog.ClaimCanvas(panel.ContentSlot, Owner);
    }

    private UIBuilder Builder(Slot root)
    {
        var ui = new UIBuilder(root);
        if (_font != null)
            ui.Font(_font);
        return ui.FontSize(DashTheme.FontBody).TextColor(DashTheme.Text);
    }

    private void BuildContent(UIBuilder ui)
    {
        var page = ui.Current;
        var layout = page.AttachComponent<VerticalLayout>();
        layout.Spacing.Value = 12f;
        layout.PaddingLeft.Value = 16f;
        layout.PaddingRight.Value = 16f;
        layout.PaddingTop.Value = 14f;
        layout.PaddingBottom.Value = 16f;
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = false;

        BuildSourceCard(page);
        BuildChips(page, DetailText, Detail, new[] { 0, 1, 2, 3 }, v => DetailNames[v].Resolve());
        BuildChips(page, HullsText, MaxHulls, HullCounts, v => v.ToString());
        BuildChips(page, PointsText, MaxPoints, PointCounts, v => v.ToString());
        BuildChips(page, PartText, SmallestPartPercent, PartSizes, v => v == 0 ? OffText.Resolve() : $"{v}%");
        BuildStatus(page);
        BuildActions(page);
        RefreshChips();
    }

    private static Slot Block(Slot parent, string name, float height)
    {
        var slot = parent.AddSlot(name);
        slot.AttachComponent<RectTransform>();
        var element = slot.AttachComponent<LayoutElement>();
        element.MinHeight.Value = height;
        element.PreferredHeight.Value = height;
        element.FlexibleHeight.Value = 0f;
        return slot;
    }

    private static void Fill(RectTransform rect, float inset = 0f)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = new float2(inset, 0f);
        rect.OffsetMax.Value = new float2(-inset, 0f);
    }

    private Text Line(Slot parent, string content, float size, color tint, float height)
    {
        var ui = Builder(parent);
        ui.PushStyle().MinHeight(height).PreferredHeight(height).FlexibleWidth(1f);
        var text = ui.Text(content, size, tint);
        ui.PopStyle();
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        return text;
    }

    // What is being split: the object's name over its triangle count and which part of it is the source.
    private void BuildSourceCard(Slot page)
    {
        var card = Block(page, "Source", 66f);
        var back = card.AttachComponent<RoundedPanel>();
        back.Color.Value = DashTheme.Surface;
        back.OutlineColor.Value = DashTheme.OutlineStrong;
        back.OutlineThickness.Value = DashTheme.OutlineWidth;
        back.CornerRadius.Value = DashTheme.RadiusCard;
        var stack = card.AttachComponent<VerticalLayout>();
        stack.PaddingLeft.Value = 14f;
        stack.PaddingRight.Value = 14f;
        stack.PaddingTop.Value = 9f;
        stack.PaddingBottom.Value = 9f;
        stack.Spacing.Value = 2f;
        stack.ForceExpandWidth.Value = true;
        stack.ForceExpandHeight.Value = false;

        var target = Target.Target;
        string name = target?.Slot?.SlotName.Value ?? "";
        int triangles = 0;
        var mesh = target is MeshCollider collider
            ? ConvexHullPointSource.GetMesh(collider.Mesh.Target)
            : ConvexHullPointSource.GetMesh(ConvexHullPointSource.Resolve(target));
        if (mesh != null)
        {
            foreach (var submesh in mesh.Submeshes)
            {
                if (submesh.Topology == PhosTopology.Triangles)
                    triangles += submesh.Count;
            }
        }
        string kind = (target is MeshCollider ? ColliderKind : RendererKind).Resolve();
        Line(card, name, DashTheme.FontHeading, DashTheme.Text, 26f);
        Line(card, SourceInfo(triangles.ToString("N0"), kind).Resolve(), DashTheme.FontSmall, DashTheme.TextDim, 18f);
    }

    private void BuildChips(Slot page, LocaleText title, Sync<int> field, int[] values, Func<int, string> label)
    {
        var row = Block(page, title.Resolve(), 62f);
        var stack = row.AttachComponent<VerticalLayout>();
        stack.Spacing.Value = 6f;
        stack.ForceExpandWidth.Value = true;
        stack.ForceExpandHeight.Value = false;
        Line(row, title.Resolve().ToUpperInvariant(), DashTheme.FontLabel, DashTheme.TextDim, 18f);

        var strip = Block(row, "Chips", 38f);
        var across = strip.AttachComponent<HorizontalLayout>();
        across.Spacing.Value = 6f;
        across.ForceExpandWidth.Value = true;
        across.ForceExpandHeight.Value = true;

        foreach (int value in values)
        {
            int pick = value;
            var slot = strip.AddSlot(label(value));
            slot.AttachComponent<RectTransform>();
            var element = slot.AttachComponent<LayoutElement>();
            element.FlexibleWidth.Value = 1f;
            element.MinHeight.Value = 38f;
            element.PreferredHeight.Value = 38f;
            var back = slot.AttachComponent<RoundedPanel>();
            back.Color.Value = DashTheme.Field;
            back.OutlineColor.Value = DashTheme.OutlineStrong;
            back.OutlineThickness.Value = DashTheme.OutlineWidth;
            back.CornerRadius.Value = DashTheme.RadiusChip;

            var text = Builder(slot).Text(label(value), DashTheme.FontSmall, DashTheme.TextDim);
            text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            Fill(text.RectTransform!);

            var button = slot.AttachComponent<Button>();
            var driver = button.AddColorDriver(back.Color, DashTheme.Field, InteractionColorMode.Direct);
            driver.PressedColor.Value = DashTheme.SurfacePressed;
            driver.DisabledColor.Value = DashTheme.Field;
            button.Clicked += (_, _) => Pick(field, pick);
            _chips.Add(new Chip { Field = field, Value = value, Driver = driver, Label = text });
        }
    }

    private void Pick(Sync<int> field, int value)
    {
        if (!MayUse || _running != null || field.Value == value)
            return;
        field.Value = value;
        _result = null;
        ClearPreview();
        SetBar(0f, 0f, DashTheme.Accent);
        SetStatus(ReadyText);
        RefreshChips();
    }

    private void RefreshChips()
    {
        foreach (var chip in _chips)
        {
            bool on = chip.Field.Value == chip.Value;
            chip.Driver.NormalColor.Value = on ? DashTheme.Accent : DashTheme.Field;
            chip.Driver.HighlightColor.Value = on ? DashTheme.AccentHover : DashTheme.SurfaceHover;
            chip.Label.Color.Value = on ? DashTheme.OnAccent : DashTheme.TextDim;
        }
    }

    // A thin track that sweeps while the split runs and fills when it lands, over one line of what happened.
    private void BuildStatus(Slot page)
    {
        var block = Block(page, "Status", 46f);
        var stack = block.AttachComponent<VerticalLayout>();
        stack.Spacing.Value = 8f;
        stack.ForceExpandWidth.Value = true;
        stack.ForceExpandHeight.Value = false;

        var track = Block(block, "Track", 6f);
        var trackPanel = track.AttachComponent<RoundedPanel>();
        trackPanel.Color.Value = DashTheme.Field;
        trackPanel.CornerRadius.Value = 3f;
        var fill = track.AddSlot("Fill");
        _barFill = fill.AttachComponent<RectTransform>();
        _barFill.AnchorMin.Value = float2.Zero;
        _barFill.AnchorMax.Value = new float2(0f, 1f);
        _barFill.OffsetMin.Value = float2.Zero;
        _barFill.OffsetMax.Value = float2.Zero;
        _barPanel = fill.AttachComponent<RoundedPanel>();
        _barPanel.Color.Value = DashTheme.Accent;
        _barPanel.CornerRadius.Value = 3f;

        _status = Line(block, ConvexSplit.Available ? ReadyText.Resolve() : UnavailableText.Resolve(),
            DashTheme.FontSmall, DashTheme.TextDim, 32f);
        _status.WordWrap.Value = true;
    }

    private void BuildActions(Slot page)
    {
        var row = Block(page, "Actions", 50f);
        var across = row.AttachComponent<HorizontalLayout>();
        across.Spacing.Value = 10f;
        across.ForceExpandWidth.Value = true;
        across.ForceExpandHeight.Value = true;

        ActionButton(row, PreviewText, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Text, () => Run(thenApply: false));
        ActionButton(row, ApplyText, DashTheme.Accent, DashTheme.AccentHover, DashTheme.OnAccent, ApplyPressed);
        _stopLabel = ActionButton(row, CancelText, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Text, CancelPressed);
    }

    private Text ActionButton(Slot row, LocaleText label, color fill, color hover, color ink, Action pressed)
    {
        var slot = row.AddSlot(label.Resolve());
        slot.AttachComponent<RectTransform>();
        var element = slot.AttachComponent<LayoutElement>();
        element.FlexibleWidth.Value = 1f;
        element.MinHeight.Value = 50f;
        element.PreferredHeight.Value = 50f;
        var back = slot.AttachComponent<RoundedPanel>();
        back.Color.Value = fill;
        back.OutlineColor.Value = DashTheme.OutlineStrong;
        back.OutlineThickness.Value = DashTheme.OutlineWidth;
        back.CornerRadius.Value = DashTheme.RadiusControl;
        var text = Builder(slot).Text(label.Resolve(), DashTheme.FontBody, ink);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        Fill(text.RectTransform!);
        var button = slot.AttachComponent<Button>();
        var driver = button.AddColorDriver(back.Color, fill, InteractionColorMode.Direct);
        driver.HighlightColor.Value = hover;
        driver.PressedColor.Value = DashTheme.SurfacePressed;
        button.Clicked += (_, _) =>
        {
            if (MayUse)
                pressed();
        };
        return text;
    }

    private void SetStatus(in LocaleText text, color? tint = null)
    {
        if (_status == null || _status.IsDestroyed)
            return;
        _status.Content.Value = text.Resolve();
        _status.Color.Value = tint ?? DashTheme.TextDim;
    }

    private void SetBar(float from, float to, color tint)
    {
        if (_barFill == null || _barFill.IsDestroyed)
            return;
        _barFill.AnchorMin.Value = new float2(from, 0f);
        _barFill.AnchorMax.Value = new float2(to, 1f);
        if (_barPanel != null && !_barPanel.IsDestroyed)
            _barPanel.Color.Value = tint;
    }

    // RUNNING

    private void ApplyPressed()
    {
        if (_result != null && _running == null)
            Apply();
        else
            Run(thenApply: true);
    }

    private void CancelPressed()
    {
        if (_running != null)
        {
            _cancel?.Cancel();
            return;
        }
        Slot.Destroy();
    }

    // Starts a split with the current settings; thenApply applies it the moment it lands.
    public void Run(bool thenApply)
    {
        if (_running != null)
        {
            _applyWhenDone |= thenApply;
            return;
        }
        if (!ConvexSplit.Available)
        {
            SetStatus(UnavailableText, DashTheme.Warning);
            return;
        }
        if (!ConvexSplit.TryCapture(Target.Target, out var positions, out var indices))
        {
            SetStatus(NoMeshText, DashTheme.Warning);
            return;
        }

        ClearPreview();
        _result = null;
        _applyWhenDone = thenApply;
        _cancel = new CancellationTokenSource();
        _startedAt = _lastTick = World.TotalTime;
        _running = ConvexSplit.RunAsync(positions, indices, Settings, _cancel.Token);
        if (_stopLabel != null && !_stopLabel.IsDestroyed)
            _stopLabel.Content.Value = StopText.Resolve();
        SetStatus(Working("0.0"));
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        FollowTarget();

        var running = _running;
        if (running == null)
            return;

        double now = World.TotalTime;
        if (!running.IsCompleted)
        {
            if (now - _lastTick >= 1.0 / 15.0)
            {
                _lastTick = now;
                double elapsed = now - _startedAt;
                float head = (float)(elapsed * 0.7 % 1.3) - 0.3f;
                SetBar(System.Math.Clamp(head, 0f, 1f), System.Math.Clamp(head + 0.3f, 0f, 1f), DashTheme.Accent);
                SetStatus(Working(elapsed.ToString("0.0")));
            }
            return;
        }

        _running = null;
        bool stopped = _cancel?.IsCancellationRequested == true;
        _cancel?.Dispose();
        _cancel = null;
        if (_stopLabel != null && !_stopLabel.IsDestroyed)
            _stopLabel.Content.Value = CancelText.Resolve();

        if (stopped || running.IsCanceled)
        {
            SetBar(0f, 0f, DashTheme.Accent);
            SetStatus(StoppedText);
            return;
        }
        if (running.IsFaulted)
        {
            var error = running.Exception?.GetBaseException();
            LumoraLogger.Warn($"ConvexSplitDialog: split failed: {error}");
            SetBar(0f, 0f, DashTheme.Accent);
            SetStatus(FailedText.WithArgs(error?.Message ?? ""), DashTheme.Negative);
            return;
        }

        var result = running.Result;
        if (result.Hulls.Count == 0)
        {
            SetBar(0f, 0f, DashTheme.Accent);
            SetStatus(NothingText, DashTheme.Warning);
            return;
        }

        _result = result;
        SetBar(0f, 1f, DashTheme.Positive);
        SetStatus(Done(result.Hulls.Count, result.PointCount, result.Seconds.ToString("0.0")), DashTheme.Text);
        LumoraLogger.Log($"ConvexSplitDialog: {result.Hulls.Count} hulls ({result.Dropped} dropped), {result.PointCount} points, "
            + $"{result.Seconds:0.00} s, hull volume {result.HullVolume:0.####} vs mesh {result.MeshVolume:0.####}");
        if (_applyWhenDone)
            Apply();
        else
            ShowPreview(result);
    }

    // APPLY

    public void Apply()
    {
        var target = Target.Target;
        var result = _result;
        var slot = target?.Slot;
        if (!MayUse || target == null || target.IsDestroyed || result == null || slot == null || slot.IsDestroyed)
            return;

        var undo = new List<IUndoBatch>();
        try
        {
            Replace(target, result.Hulls, undo);
        }
        catch (UnauthorizedAccessException ex)
        {
            LumoraLogger.Warn($"ConvexSplitDialog: refused: {ex.Message}");
            SetStatus(RefusedText, DashTheme.Negative);
            return;
        }

        UndoManager.ForLocalUser(World)?.Record(CompositeUndoBatch.Combine(UndoLocale.SplitConvexHulls, undo));
        Slot.Destroy();
    }

    // One hull collider per part on the target's slot, carrying a mesh collider's settings; the mesh collider
    // itself goes. A renderer only gains the hulls. Every change lands in undo.
    public static List<ConvexHullCollider> Replace(Component target, IReadOnlyList<float3[]> hulls, List<IUndoBatch>? undo)
    {
        var made = new List<ConvexHullCollider>(hulls.Count);
        var slot = target.Slot;
        var source = target as MeshCollider;
        foreach (var hull in hulls)
        {
            var collider = slot.AttachComponent<ConvexHullCollider>();
            collider.Points.AddRange(hull);
            if (source != null)
            {
                collider.Offset.Value = source.Offset.Value;
                collider.Type.Value = source.Type.Value;
                collider.Mass.Value = source.Mass.Value;
                collider.CharacterCollider.Value = source.CharacterCollider.Value;
                collider.IgnoreRaycasts.Value = source.IgnoreRaycasts.Value;
                collider.Friction.Value = source.Friction.Value;
                collider.Bounce.Value = source.Bounce.Value;
                collider.Enabled.Value = source.Enabled.Value;
            }
            made.Add(collider);
            if (undo != null && ComponentExistenceUndoBatch.CreateAttach(collider) is { } attached)
                undo.Add(attached);
        }

        if (source != null)
        {
            var removed = undo != null ? ComponentExistenceUndoBatch.CreateDestroy(source) : null;
            if (removed != null)
                undo!.Add(removed);
            else if (!source.IsDestroyed)
                source.Destroy();
        }
        return made;
    }

    // PREVIEW

    // Translucent shells a touch larger than each part so they read over the surface they wrap, one hue per
    // part. They follow the object rather than sit inside it, so nothing is added to it before Apply.
    private void ShowPreview(ConvexSplitResult result)
    {
        ClearPreview();
        var target = Target.Target;
        if (target?.Slot == null || World?.RootSlot == null)
            return;

        float3 offset = target is MeshCollider collider ? collider.Offset.Value : float3.Zero;
        using (World.EnterLocalUserAllocation())
        {
            var root = World.AddLocalUserSlot(World.RootSlot, "Hull Preview");
            root.Persistent.Value = false;
            _preview.Target = root;
            FollowTarget();

            for (int i = 0; i < result.Hulls.Count; i++)
            {
                var hull = result.Hulls[i];
                float3 centre = float3.Zero;
                foreach (var p in hull)
                    centre += p;
                centre /= hull.Length;

                var part = root.AddSlot($"Hull {i + 1}");
                part.LocalPosition.Value = offset;
                var mesh = part.AttachComponent<ConvexHullMesh>();
                mesh.FlatShading.Value = true;
                foreach (var p in hull)
                    mesh.Points.Add(centre + (p - centre) * 1.015f);

                var tint = color.FromHSV(i * 0.618034f % 1f, 0.7f, 1f);
                var shell = part.AttachComponent<FresnelMaterial>();
                shell.NearColor.Value = new colorHDR(tint.r, tint.g, tint.b, 0.22f);
                shell.FarColor.Value = new colorHDR(tint.r, tint.g, tint.b, 0.85f);
                shell.BlendMode.Value = BlendMode.Alpha;
                var renderer = part.AttachComponent<MeshRenderer>();
                renderer.Mesh.Target = mesh;
                renderer.Material.Target = shell;
                renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            }
        }
    }

    public Slot? PreviewRoot => _preview.Target;

    private void ClearPreview()
    {
        var root = _preview.Target;
        if (root != null && !root.IsDestroyed)
            root.Destroy();
        if (!IsDestroyed)
            _preview.Target = null!;
    }

    private void FollowTarget()
    {
        var root = _preview.Target;
        var source = Target.Target?.Slot;
        if (root == null || root.IsDestroyed || source == null || source.IsDestroyed || !_attachedHere)
            return;
        var position = source.GlobalPosition;
        if ((position - root.GlobalPosition).LengthSquared > 1e-10f)
            root.GlobalPosition = position;
        var rotation = source.GlobalRotation;
        if (MathF.Abs(floatQ.Dot(rotation, root.GlobalRotation)) < 0.999999f)
            root.GlobalRotation = rotation;
        var scale = source.GlobalScale;
        if ((scale - root.GlobalScale).LengthSquared > 1e-10f)
            root.GlobalScale = scale;
    }
}

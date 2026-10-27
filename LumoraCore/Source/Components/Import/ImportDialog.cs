// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Import;

// An import dialog stands out in the world: everyone sees it open, turn its pages and show how the import
// went. The machine that made it builds it and runs it, with the files on that machine, and once an importer
// is named only that user's pointers reach it. -xlinka
public abstract class ImportDialog : Component
{
    public const int GridItemsPerRow = 2;
    public const float GridItemSpacing = 10f;
    public const float GridPadding = 12f;
    public const float ButtonHeight = 56f;
    public const float HeaderRowHeight = 44f;

    public static readonly color BackColor = DashTheme.Surface;
    public static readonly color SectionTitleColor = DashTheme.Text;
    public static readonly color BorderColor = DashTheme.Outline;
    public static readonly color ButtonFill = DashTheme.Surface;
    public static readonly color ButtonText = DashTheme.Text;

    // Semibold is the tool-panel default; callers can still override the URL. Each dialog owns
    // its provider in its own world so font references never cross world boundaries. -xlinka
    public static System.Uri? DefaultFontUrl { get; set; } = DashTheme.FontSemibold;

    // Prefer the configured tool font, then any provider in the dialog's world if explicitly unset.
    //
    // The startup URL is null in anything that comes up without the dashboard - a headless run, a world
    // template that spawns a dialog itself - and text with no font renders NOTHING at all, so the dialog
    // would be a blank frame with invisible buttons. Borrowing the URL rather than the provider keeps the
    // per-dialog provider rule intact: no ref points at somebody else's slot, so a dialog outliving the
    // slot it borrowed from is not a thing. -xlinka
    public static Uri? ResolveFontUrl(World? world)
    {
        if (DefaultFontUrl != null)
            return DefaultFontUrl;

        var provider = world?.RootSlot?.GetComponentInChildren<FontProvider>();
        var url = provider?.URL?.Value;
        return url;
    }

    // Files on the importer's machine. Never synced: nobody else could open them.
    public readonly List<string> Paths = new();

    // World where the result is spawned. The dialog is shared, with importer-only controls. Set by
    // UniversalImporter before OnStart fires, on the importer's machine only. - xlinka
    public World? TargetWorld { get; set; }

    // Who opened the dialog. Synced, so every machine knows whose it is; null on a dialog nobody claimed.
    public readonly SyncRef<User> ImportingUser = new();

    // Whether the dialog was attached on this machine. One that arrived from another machine only shows what
    // its maker built.
    private bool _attachedHere;
    private PanelShell? _panel;
    private WizardForm? _wizard;
    private FontProvider? _fontProvider;

    protected World ResolveTargetWorld() => TargetWorld ?? World;

    protected virtual float2 CanvasSize => new float2(460f, 520f);
    protected virtual string TitleText => "Import";

    // Target physical height of the panel in world meters. Canvas pixels are
    // multiplied by PhysicalHeight / CanvasSize.y so the panel sits at a comfortable
    // arm's-length size regardless of how many rows the wizard needs. - xlinka
    protected virtual float PhysicalHeight => 0.5f;

    protected bool CanInteract => IsExecutionAllowed && MayUse(ImportingUser, World);

    public PanelShell? Panel => _panel;
    public WizardForm? Wizard => _wizard;

    public void SetLocalUserAsImporting()
    {
        if (!ImportingUser.Value.IsNull)
            throw new InvalidOperationException("Importing user is already set!");
        ImportingUser.Target = World.LocalUser;
    }

    public override void OnAttach()
    {
        base.OnAttach();
        _attachedHere = true;
    }

    // Built and run by the machine that made it. One brought back from a save or duplicated here is this
    // machine's too; the buttons it came with lead nowhere, so it builds afresh over them.
    public override void OnStart()
    {
        base.OnStart();
        if (_attachedHere || IsMintedHere)
        {
            if (!_attachedHere || ImportingUser.Value.IsNull)
                ImportingUser.Target = World.LocalUser;
            EnsureBuilt(copied: !_attachedHere);
        }
    }

    public override void OnUserLeft(User user) => TakeDownForLeaver(this, ImportingUser, user);

    // Missing or not-yet-resolved ownership never grants access to local files.
    internal static bool MayUse(SyncRef<User> importer, World? world)
        => importer.Target is { } user && ReferenceEquals(user, world?.LocalUser);

    // The importer gone, its dialog goes too: the files it stood on left with them, and nobody else may press
    // it. The authority takes it down as engine work, the way a leaver's body goes. -xlinka
    internal static void TakeDownForLeaver(Component dialog, SyncRef<User> importer, User leaver)
    {
        if (dialog.World is not { IsAuthority: true } world || leaver == null || importer.Value.IsNull
            || importer.Value != leaver.ReferenceID || dialog.Slot is not { IsDestroyed: false } slot)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
            slot.Destroy();
    }

    // Why nothing may be brought into this world from here, or null when it may. Asked on opening and again
    // by every action at the moment it would bring something in: the right can be taken away while the
    // dialog is up, and an import the host refuses would only ever appear on the importer's machine. -xlinka
    public static string? SpawnRefusal(World? world)
    {
        if (world == null)
            return "There is no world to import into.";
        if (!world.AllowsItemSpawning
            || world.DataModelPermissions?.AllowsDomain(world.LocalUser, DataModelPermissionDomain.Spawn) == false)
            return "This world does not let you bring files in.";
        return null;
    }

    // Only the owner's pointers reach a claimed dialog.
    internal static void ClaimCanvas(Slot? inside, SyncRef<User> importer)
    {
        var canvas = inside?.GetComponentInParents<Helio.UI.Canvas>();
        if (canvas != null && importer.Target is { } user)
        {
            canvas.Exclusive.Target = user;
            if (inside?.GetComponentInParents<PanelShell>()?.Slot.GetComponent<Grabbable>() is { } grab)
            {
                grab.OnlyUsers.Clear();
                grab.OnlyUsers.Add(user);
            }
        }
    }

    private void EnsureBuilt(bool copied)
    {
        if (_panel != null) return;

        // Apply canvas->world scale so the panel renders at PhysicalHeight meters
        // tall regardless of canvas px height. Without this, 1 canvas px = 1 world
        // unit and the panel fills the view. - xlinka
        var canvasScale = CanvasSize.y > 0f ? PhysicalHeight / CanvasSize.y : 0.001f;
        Slot.LocalScale.Value = new float3(canvasScale, canvasScale, canvasScale);

        _panel = Slot.GetComponent<PanelShell>() ?? Slot.AttachComponent<PanelShell>();
        _panel.Title.Value = TitleText;
        _panel.Size.Value = CanvasSize;
        if (copied)
        {
            Slot.FindChild("DialogFont", recursive: false)?.Destroy();
            _panel.ClearContent();
        }

        // Create a per-dialog FontProvider in this dialog's own world (so the
        // PanelShell.Font.Target ref doesn't cross worlds, which SyncRef rejects
        // even when one of the worlds is the userspace overlay). - xlinka
        var fontUrl = ResolveFontUrl(World);
        if (fontUrl != null)
        {
            var fontSlot = Slot.AddSlot("DialogFont");
            _fontProvider = fontSlot.AttachComponent<FontProvider>();
            _fontProvider.URL.Value = fontUrl;
            _fontProvider.FallbackURLs.Add(DashTheme.FontRegular);
            _panel.Font.Target = _fontProvider;
        }

        var wizardHost = _panel.ContentSlot!.AddSlot("Wizard");
        var rect = wizardHost.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;

        _wizard = wizardHost.AttachComponent<WizardForm>();
        _wizard.CanvasSize.Value = CanvasSize;
        ClaimCanvas(wizardHost, ImportingUser);
        var refusal = SpawnRefusal(ResolveTargetWorld());
        _wizard.OpenRoot(WithFont(refusal != null ? ui => BuildRefusal(ui, refusal) : OpenRoot));
    }

    // Every action that brings something in asks this first. True when it may not, with the reason on the
    // dialog and in the log.
    protected bool RefuseSpawn()
    {
        var refusal = SpawnRefusal(ResolveTargetWorld());
        if (refusal == null)
            return false;
        Logger.Warn($"{GetType().Name}: {refusal} {Paths.Count} file(s) not imported.");
        OpenPage(ui => BuildRefusal(ui, refusal));
        return true;
    }

    private void BuildRefusal(UIBuilder ui, string refusal)
    {
        var body = SetupSection(ui, "Can't Import Here", backButton: false);
        body.PushStyle().FlexibleHeight(1f);
        var msg = body.Text(refusal, 14f, ButtonText);
        msg.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        msg.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        msg.WordWrap.Value = true;
        body.PopStyle();

        SetupGrid(body);
        GridButton(body, "Close", () => Slot.Destroy());
    }

    protected abstract void OpenRoot(UIBuilder ui);

    // Wrap a page-build delegate so the incoming UIBuilder has the dialog's own
    // FontProvider applied before subclass build code runs. Each dialog
    // instantiates its own provider in its own world (see EnsureBuilt) to avoid
    // cross-world AssetRef rejection. - xlinka
    protected Action<UIBuilder> WithFont(Action<UIBuilder> build)
    {
        return ui =>
        {
            if (_fontProvider != null) ui.Font(_fontProvider);
            ui.FontSize(DashTheme.FontBody).BackgroundColor(ButtonFill).TextColor(ButtonText);
            build(ui);
        };
    }

    // Open a wizard page with the default font/style applied. Subclasses should
    // call this instead of `Wizard?.Open(menu)` directly. - xlinka
    protected void OpenPage(Action<UIBuilder> build)
    {
        _wizard?.Open(WithFont(build));
    }

    // Page layout: attach VerticalLayout directly to the page slot (matches the
    // file-browser pattern) so its children inherit the page rect. If we used
    // ui.VerticalLayout() instead, it'd create a child slot with the default
    // 100x100 centered RectTransform and squash everything inside it. - xlinka
    protected UIBuilder SetupSection(UIBuilder ui, LocaleText title, bool backButton = true)
    {
        var page = ui.Root;
        var vLayout = page.AttachComponent<VerticalLayout>();
        vLayout.Spacing.Value = 8f;
        vLayout.PaddingLeft.Value = GridPadding;
        vLayout.PaddingRight.Value = GridPadding;
        vLayout.PaddingTop.Value = GridPadding;
        vLayout.PaddingBottom.Value = GridPadding;
        vLayout.ForceExpandWidth.Value = true;
        vLayout.ForceExpandHeight.Value = false;

        if (backButton)
        {
            var headerRow = page.AddSlot("BackRow");
            headerRow.AttachComponent<RectTransform>();
            var hLE = headerRow.AttachComponent<LayoutElement>();
            hLE.MinHeight.Value = HeaderRowHeight;
            hLE.PreferredHeight.Value = HeaderRowHeight;
            var hLayout = headerRow.AttachComponent<HorizontalLayout>();
            hLayout.Spacing.Value = 8f;
            hLayout.ForceExpandWidth.Value = false;
            hLayout.ForceExpandHeight.Value = true;

            AddBackButton(headerRow);

            var promptSlot = headerRow.AddSlot("Prompt");
            promptSlot.AttachComponent<RectTransform>();
            var pLE = promptSlot.AttachComponent<LayoutElement>();
            pLE.FlexibleWidth.Value = 1f;
            pLE.MinHeight.Value = HeaderRowHeight;
            pLE.PreferredHeight.Value = HeaderRowHeight;
            var pBuilder = new UIBuilder(promptSlot);
            if (_fontProvider != null) pBuilder.Font(_fontProvider);
            var pText = pBuilder.Text(title, 16f, SectionTitleColor);
            pText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            pText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(pText.RectTransform!);
        }
        else
        {
            var promptRow = page.AddSlot("Prompt");
            promptRow.AttachComponent<RectTransform>();
            var pLE = promptRow.AttachComponent<LayoutElement>();
            pLE.MinHeight.Value = HeaderRowHeight;
            pLE.PreferredHeight.Value = HeaderRowHeight;
            var pBuilder = new UIBuilder(promptRow);
            if (_fontProvider != null) pBuilder.Font(_fontProvider);
            var pText = pBuilder.Text(title, 16f, SectionTitleColor);
            pText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            pText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(pText.RectTransform!);
        }

        var body = page.AddSlot("Body");
        body.AttachComponent<RectTransform>();
        var bLE = body.AttachComponent<LayoutElement>();
        bLE.FlexibleHeight.Value = 1f;
        bLE.MinHeight.Value = 200f;

        // Body MUST carry its own layout controller. Without one, a fresh RectTransform is a 100x100
        // box centred in its parent, so every row a dialog added landed on top of every other row in
        // the middle of the panel - which is exactly how all of these looked. The page's own
        // VerticalLayout only arranges the header and this Body slot; it does not reach inside. -xlinka
        var bodyLayout = body.AttachComponent<VerticalLayout>();
        bodyLayout.Spacing.Value = GridItemSpacing;
        bodyLayout.PaddingTop.Value = 4f;
        bodyLayout.PaddingBottom.Value = 4f;
        bodyLayout.ForceExpandWidth.Value = true;
        bodyLayout.ForceExpandHeight.Value = false;

        var bodyBuilder = new UIBuilder(body);
        if (_fontProvider != null) bodyBuilder.Font(_fontProvider);
        bodyBuilder.FontSize(DashTheme.FontBody).BackgroundColor(ButtonFill).TextColor(ButtonText);
        return bodyBuilder;
    }

    // Current is the last label/spacer produced by the builder; Root is the body container. Using
    // Current here puts the entire grid INSIDE that label's default rect and collapses its buttons.
    protected void SetupGrid(UIBuilder ui)
    {
        var row = ui.Root.AddSlot("Buttons");
        row.AttachComponent<RectTransform>();

        _gridRowElement = row.AttachComponent<LayoutElement>();
        _gridRowElement.UseZeroMetrics.Value = true;
        _gridRowElement.FlexibleHeight.Value = 0f;
        _gridButtonCount = 0;
        _buttonGrid = row.AttachComponent<GridLayout>();
        _buttonGrid.Spacing.Value = GridItemSpacing;
        GrowGridRow();

        ui.NestInto(row);
    }

    private LayoutElement? _gridRowElement;
    private GridLayout? _buttonGrid;
    private int _gridButtonCount;

    // The row has to be tall enough for however many buttons actually get added, which is only known
    // as they arrive - a fixed height clips the second row the moment a dialog has four buttons.
    private void GrowGridRow()
    {
        if (_gridRowElement == null)
            return;

        int columns = System.Math.Clamp(_gridButtonCount, 1, GridItemsPerRow);
        if (_buttonGrid != null) _buttonGrid.Columns.Value = columns;
        int rows = System.Math.Max(1, (_gridButtonCount + columns - 1) / columns);
        float height = rows * ButtonHeight + (rows - 1) * GridItemSpacing;
        _gridRowElement.MinHeight.Value = height;
        _gridRowElement.PreferredHeight.Value = height;
    }

    protected void GridButton(UIBuilder ui, LocaleText label, Action onClick, color? tint = null, bool primary = false)
    {
        _gridButtonCount++;
        GrowGridRow();

        ActionButton(ui, label, () => { if (CanInteract) onClick(); }, tint, primary);
    }

    // Shared by wizard pages, advanced settings, and folder import actions.
    internal static Button ActionButton(UIBuilder ui, LocaleText label, Action onClick, color? tint = null, bool primary = false)
        => ActionButton(ui, label, onClick, out _, tint, primary);

    internal static Button ActionButton(UIBuilder ui, LocaleText label, Action onClick, out ColorDriver driver,
        color? tint = null, bool primary = false)
    {
        var slot = ui.Next(label.Fallback ?? string.Empty);
        var le = slot.GetComponent<LayoutElement>() ?? slot.AttachComponent<LayoutElement>();
        le.UseZeroMetrics.Value = true;
        le.MinHeight.Value = ButtonHeight;
        le.PreferredHeight.Value = ButtonHeight;
        le.FlexibleHeight.Value = 0f;

        var bg = slot.AttachComponent<RoundedPanel>();
        var fill = primary ? DashTheme.Accent : tint ?? ButtonFill;
        bg.Color.Value = fill;
        bg.OutlineColor.Value = DashTheme.OutlineStrong;
        bg.OutlineThickness.Value = DashTheme.OutlineWidth;
        bg.CornerRadius.Value = DashTheme.RadiusControl;

        var labelBuilder = new UIBuilder(slot).SetStyle(ui.Style);
        var text = labelBuilder.Text(label, DashTheme.FontBody, primary ? DashTheme.OnAccent : ButtonText);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        text.WordWrap.Value = true;
        FillRect(text.RectTransform!, 12f, 12f, 8f, 8f);

        var button = slot.AttachComponent<Button>();
        button.Clicked += (_, _) => onClick();
        driver = StyleButton(button, bg, fill, primary);
        return button;
    }

    private static ColorDriver StyleButton(Button button, RoundedPanel panel, color fill, bool primary = false)
    {
        var driver = button.AddColorDriver(panel.Color, fill, InteractionColorMode.Direct);
        driver.HighlightColor.Value = primary ? DashTheme.AccentHover : DashTheme.SurfaceHover;
        driver.PressedColor.Value = primary ? DashTheme.AccentPressed : DashTheme.SurfacePressed;
        driver.DisabledColor.Value = DashTheme.Field;
        return driver;
    }

    // The scrolling content owns its layout. The caller keeps its body builder for fixed actions.
    internal static UIBuilder ScrollContent(UIBuilder ui)
    {
        ui.PushStyle().MinHeight(0f).PreferredHeight(0f).FlexibleHeight(1f);
        var scroll = ui.ScrollRect(out var content, background: color.Transparent);
        scroll.RowsActOnRelease.Value = true;
        ui.PopStyle();
        var viewport = scroll.Slot.GetComponent<LayoutElement>();
        if (viewport != null) viewport.UseZeroMetrics.Value = true;
        var layout = content.Slot.AttachComponent<VerticalLayout>();
        layout.Spacing.Value = 6f;
        layout.PaddingTop.Value = 4f;
        layout.PaddingBottom.Value = 4f;
        layout.ForceExpandHeight.Value = false;
        return new UIBuilder(content.Slot).SetStyle(ui.Style);
    }

    // Honest "not supported yet" page: shows a message + a Close button that just
    // tears down the dialog. No content is spawned - we don't fake success for a
    // pipeline that doesn't exist. Call from a dialog whose format Lumora can't
    // import yet. - xlinka
    protected void ShowUnsupported(string message)
    {
        OpenPage(ui =>
        {
            var body = SetupSection(ui, "Not Supported Yet", backButton: false);
            body.PushStyle().FlexibleHeight(1f);
            var msg = body.Text(message, 14f, ButtonText);
            msg.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            msg.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            msg.WordWrap.Value = true;
            body.PopStyle();

            SetupGrid(body);
            GridButton(body, "Close", () => Slot.Destroy());
        });
    }

    private void AddBackButton(Slot parent)
    {
        var slot = parent.AddSlot("Back");
        slot.AttachComponent<RectTransform>();
        var le = slot.AttachComponent<LayoutElement>();
        le.MinWidth.Value = 80f;
        le.PreferredWidth.Value = 80f;
        le.FlexibleWidth.Value = 0f;

        var bg = slot.AttachComponent<RoundedPanel>();
        bg.Color.Value = BackColor;
        bg.OutlineColor.Value = BorderColor;
        bg.OutlineThickness.Value = DashTheme.OutlineWidth;
        bg.CornerRadius.Value = DashTheme.RadiusControl;

        var b = new UIBuilder(slot);
        if (_fontProvider != null) b.Font(_fontProvider);
        var t = b.Text("Back", DashTheme.FontBody, ButtonText);
        t.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        t.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(t.RectTransform!);

        var button = slot.AttachComponent<Button>();
        button.Clicked += (_, _) => { if (CanInteract) _wizard?.Return(); };
        StyleButton(button, bg, BackColor);
    }

    private static void FillRect(RectTransform rect, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = new float2(left, bottom);
        rect.OffsetMax.Value = new float2(-right, -top);
    }

    // raw-file fallback: route through registered handler if one is plugged in,
    // otherwise spawn a labeled grabbable as a visible placeholder. - xlinka
    public void AsRawFile()
    {
        if (!CanInteract || RefuseSpawn()) return;
        int count = Paths.Count;
        int rowSize = (int)MathF.Max(1f, MathF.Ceiling(MathF.Sqrt(count)));
        int index = 0;
        var basePos = Slot.GlobalPosition;
        var baseRot = Slot.GlobalRotation;
        float scale = 1f;

        var target = ResolveTargetWorld();
        foreach (var file in Paths)
        {
            var name = Path.GetFileName(file);
            var s = target.AddSpawnSlot(target.RootSlot, string.IsNullOrEmpty(name) ? file : name);
            var offset = UniversalImporter.GridOffset(ref index, rowSize) * scale;
            s.GlobalPosition = basePos + baseRot * offset;
            s.GlobalRotation = baseRot;
            s.GlobalScale = new float3(scale, scale, scale);

            var handler = ImportHandlers.Raw;
            if (handler != null)
            {
                var pathCaptured = file;
                _ = handler.ImportAsync(s, pathCaptured);
            }
            else
                UniversalImporter.SpawnFileLabel(s, string.IsNullOrEmpty(name) ? file : name);
        }
        Slot.Destroy();
    }
}

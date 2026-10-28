// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The dash is a workspace: every app is a window on it, the dock down the left opens and raises them,
// the bar along the top says who you are, where you are and what is going up and carries the tools, and the
// home widgets sit on the workspace itself, behind the windows. Three sheets back to front: the desk, the
// windows, the shell (dock, bar, tips). Each open window is lifted toward the reader by its place in the stack and lays
// down depth with its backing, so the one on top covers the rest whichever way their render bands land;
// the shell is lifted over all of them and anything hung on the canvas root at an overlay level (a modal)
// over that. -xlinka
[ComponentCategory("Hidden")]
[UserspaceOnly]
public class Dashboard : UIComponent, IUIGrabReceiver
{
    public const float Outer = 14f;
    public const float IslandTop = 14f;
    public const float IslandHeight = DashStatusIsland.Height;
    public const float DeskTopGap = 8f;
    public const float DockGap = 12f;
    // The column the desk widgets stand in on the right. Windows may go over it; a first run leaves it clear.
    public const float GadgetColumnWidth = 304f;
    public const float GadgetColumnGap = 12f;

    private const long DeskOrder = 10L;
    private const long WindowsOrder = 20L;
    private const long ShellOrder = 30L;
    // The snap guide's outline is a dashed line: a preview, not a window.
    private const float GuideDash = 14f;
    private const float GuideGap = 9f;
    private const float GuideStroke = 2f;


    public readonly Sync<float2> Size;
    public readonly Sync<string> Title;
    public readonly Sync<string> Version;
    public readonly AssetRef<FontSet> Font;
    public readonly AssetRef<FontSet> FontSemibold;
    public readonly AssetRef<FontSet> FontBold;
    // Fixed-width face for readouts that should not reflow as their digits change.
    public readonly AssetRef<FontSet> FontMono;

    private readonly List<DashboardScreen> _screens = new();
    private readonly Dictionary<int, RoundedRectTextureProvider> _roundedByRadius = new();
    private readonly Dictionary<string, ImageProvider> _icons = new();

    private bool _built;
    private RectTransform? _rootRect;
    private RoundedRectTextureProvider? _rounded;
    private Canvas? _canvas;
    private Slot? _deskSlot;
    private Slot? _windowsSlot;
    private Slot? _shellSlot;
    private Slot? _iconSlot;
    private StackLayer? _shellLayer;
    private DashWorkspace? _workspace;
    private DashDock? _dock;
    private DashStatusIsland? _island;
    private DashTopBar? _topBar;
    private DashSnapPicker? _snapPicker;
    private UIUnlitMaterial? _backing;
    private DashboardScreen? _desk;
    private DashboardScreen? _lastCurrent;
    private Slot? _guide;
    private readonly List<Slot> _guideDashes = new();
    private float2 _guideDashed;
    private Slot? _tip;
    private Text? _tipText;

    public IReadOnlyList<DashboardScreen> Screens => _screens;

    // The screen that takes the keyboard: the window in front, or the desk when no window is.
    public DashboardScreen? CurrentScreen => _workspace?.Focused?.Screen ?? _desk;

    public DashboardScreen? DeskScreen => _desk;
    public DashWorkspace? Workspace => _workspace;
    public DashDock? Dock => _dock;
    public DashStatusIsland? Island => _island;
    public DashTopBar? TopBar => _topBar;
    public DashSnapPicker? SnapPicker => _snapPicker;
    public Canvas? Canvas => _canvas;
    public Slot? ScreenHostSlot => _windowsSlot;
    public Slot? DeskSlot => _deskSlot;
    public Slot? GuideSlot => _guide;
    public Slot? TipSlot => _tip;
    public RoundedRectTextureProvider? RoundedSprite => _rounded;

    // Depth-writing material every window backing shares: the cover that hides what is under a window.
    public UIUnlitMaterial? BackingMaterial => _backing;

    public event Action<DashboardScreen?>? ScreenChanged;

    // The dash an app is drawing for: its own, or the one it was popped out of. A popped app's canvas sits on
    // a rig of its own, so its panel is found through the canvas, not up the tree.
    public static Dashboard? Of(Slot? slot)
    {
        if (slot == null)
            return null;
        return slot.GetComponentInParents<Dashboard>() ?? DashWindowPanel.Of(slot)?.Owner
            ?? slot.GetComponentInParents<DashWindowPanel>()?.Owner;
    }

    public Dashboard()
    {
        Size = new Sync<float2>(this, new float2(1180f, 720f));
        Title = new Sync<string>(this, "Lumora");
        Version = new Sync<string>(this, BuildInfo.Version == "0.0.0-dev" ? "Lumora development build" : $"Lumora v{BuildInfo.Version}");
        Font = new AssetRef<FontSet>(this);
        FontSemibold = new AssetRef<FontSet>(this);
        FontBold = new AssetRef<FontSet>(this);
        FontMono = new AssetRef<FontSet>(this);
    }

    public override void OnStart()
    {
        base.OnStart();
        EnsureBuilt();
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        ApplyRootSize();
    }

    public override void OnDestroy()
    {
        if (_canvas != null)
            _canvas.PressBegan -= OnPressBegan;
        base.OnDestroy();
    }

    // Nine-slice corner sprites, one per radius the theme uses: a sprite sliced at another border width cuts
    // through its own arc and the corner comes out dented. Cached per dash.
    public RoundedRectTextureProvider RoundedSpriteFor(float radius)
    {
        int r = (int)MathF.Round(radius);
        if (r < 2) r = 2;
        if (r > 32) r = 32;
        if (_roundedByRadius.TryGetValue(r, out var existing) && !existing.IsDestroyed)
            return existing;

        var provider = Slot.AddSlot("Theme" + r).AttachComponent<RoundedRectTextureProvider>();
        provider.Size.Value = r * 4;
        provider.Radius.Value = r;
        _roundedByRadius[r] = provider;
        return provider;
    }

    // One provider per bundled icon for the whole dash, so a glyph repeated on every window is one texture.
    public ImageProvider IconProvider(string icon)
    {
        if (_icons.TryGetValue(icon, out var cached) && !cached.IsDestroyed)
            return cached;
        _iconSlot ??= Slot.AddSlot("Icons");
        var provider = _iconSlot.AttachComponent<ImageProvider>();
        provider.URL.Value = new Uri("res://Assets/Textures/Icons/" + icon + ".svg");
        _icons[icon] = provider;
        return provider;
    }

    // SCREENS

    // A screen becomes an app with a window on the workspace, closed until asked for. showInNav false keeps
    // it reachable (a card can open it) without a plate on the dock. The home screen is the desk itself.
    public T AddScreen<T>(string label, color? activeColor = null, bool showInNav = true, string? icon = null,
        int dockGroup = 0) where T : DashboardScreen, new()
    {
        EnsureBuilt();

        if (typeof(HomeScreen).IsAssignableFrom(typeof(T)))
        {
            var deskSlot = _deskSlot!.AddSlot(label);
            Fill(deskSlot.AttachComponent<RectTransform>());
            var desk = deskSlot.AttachComponent<T>();
            desk.Label.Value = label;
            if (activeColor.HasValue)
                desk.ActiveColor.Value = activeColor.Value;
            _desk = desk;
            _screens.Add(desk);
            desk.ShowScreen();
            return desk;
        }

        var tint = activeColor ?? DashTheme.Accent;
        var window = _workspace!.AddWindow(KeyFor(typeof(T), label), label, icon ?? "workspace", tint, showInNav, dockGroup,
            DefaultSizeFor(typeof(T)), MinSizeFor(typeof(T)));
        window.OpensFilled = OpensFilledFor(typeof(T));
        window.MaxSize = MaxSizeFor(typeof(T));
        var slot = window.Body!.AddSlot(label);
        Fill(slot.AttachComponent<RectTransform>());
        var screen = slot.AttachComponent<T>();
        screen.Label.Value = label;
        if (activeColor.HasValue)
            screen.ActiveColor.Value = activeColor.Value;
        window.Attach(screen);
        _screens.Add(screen);
        screen.HideScreen();
        return screen;
    }

    // A placeholder screen keys off its label; every real screen off its type, which survives a relabel.
    private static string KeyFor(Type type, string label)
    {
        if (type != typeof(DashboardScreen))
            return type.Name;
        var sb = new System.Text.StringBuilder();
        foreach (char c in label)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    // The apps that are a place of their own rather than a tool beside the others: they take the whole desk
    // until somebody arranges them.
    private static bool OpensFilledFor(Type type)
        => type == typeof(WorldsScreen) || type == typeof(InventoryScreen) || type == typeof(DesktopScreen);

    // Sizes an app opens at and will not go under, picked so its own layout has room at the dash's 720
    // units of height. Anything not listed opens at the workspace default.
    private static float2 DefaultSizeFor(Type type)
    {
        if (type == typeof(WorldsScreen)) return new float2(1120f, 640f);
        if (type == typeof(DesktopScreen)) return new float2(900f, 540f);
        if (type == typeof(SessionScreen)) return new float2(760f, 560f);
        if (type == typeof(SettingsScreen)) return new float2(900f, 600f);
        if (type == typeof(InventoryScreen)) return new float2(960f, 600f);
        if (type == typeof(GroupsScreen)) return new float2(1000f, 620f);
        if (type == typeof(PeopleScreen)) return new float2(1000f, 620f);
        if (type == typeof(FileBrowserScreen)) return new float2(800f, 540f);
        if (type == typeof(ExitScreen)) return new float2(660f, 420f);
        if (type == typeof(DebugScreen)) return new float2(600f, 460f);
        return new float2(420f, 320f);
    }

    // Each floor is where that app's own layout stops fitting: Worlds' and Inventory's filter rails need
    // their height, the desktop's control strip its width.
    private static float2 MinSizeFor(Type type)
    {
        if (type == typeof(WorldsScreen)) return new float2(680f, 500f);
        if (type == typeof(InventoryScreen)) return new float2(680f, 560f);
        if (type == typeof(SessionScreen)) return new float2(520f, 420f);
        // Narrower than this the list and the page stack; the header still needs its height.
        if (type == typeof(GroupsScreen)) return new float2(460f, 500f);
        if (type == typeof(PeopleScreen)) return new float2(460f, 500f);
        if (type == typeof(SettingsScreen)) return new float2(600f, 440f);
        if (type == typeof(DesktopScreen)) return new float2(DesktopScreen.StripWidth + 2f * DashWindow.BodyInset, 380f);
        if (type == typeof(ExitScreen)) return new float2(480f, 410f);
        if (type == typeof(FileBrowserScreen)) return new float2(460f, 400f);
        if (type == typeof(DebugScreen)) return new float2(480f, 360f);
        if (type == typeof(DashboardScreen)) return new float2(300f, 200f);
        return new float2(380f, 280f);
    }

    private static float2 MaxSizeFor(Type type)
        => type == typeof(ExitScreen) ? new float2(700f, 520f) : new float2(3200f, 2000f);

    public DashWindow? WindowOf(DashboardScreen? screen) => _workspace?.WindowOf(screen);

    // Bring a screen in front: its window opens (or comes back from the dock or the room) and takes the
    // keyboard. The desk takes it by having no window in front.
    public void SwitchTo(DashboardScreen screen)
    {
        if (ReferenceEquals(screen, _desk))
        {
            _workspace?.FocusDesk();
            return;
        }
        var window = WindowOf(screen);
        if (window != null)
            _workspace!.Present(window);
    }

    // A screen shown on its own across the whole workspace, the way a stand in a world shows one.
    public void OpenFilled(DashboardScreen screen)
    {
        SwitchTo(screen);
        var window = WindowOf(screen);
        if (window != null && window.Snap != DashWindow.WindowSnap.Fill)
            _workspace!.ApplySnap(window, DashWindow.WindowSnap.Fill);
    }

    // One flag for every grid and every popped-out panel; the grids mirror it on their next update.
    public void ToggleWidgetEdit() => WidgetPanel.EditMode = !WidgetPanel.EditMode;

    // A freshly opened dash re-shows what is on it: the canvas only renders on a dirty, and the parked render
    // rig coming back does not dirty it on its own. Showing a screen again is also its cue to refresh.
    public void ForceRebuild()
    {
        _desk?.ShowScreen();
        if (_workspace != null)
        {
            foreach (var window in _workspace.Windows)
            {
                if (window.State == DashWindow.WindowState.Open)
                    window.Screen?.ShowScreen();
            }
        }
        _canvas?.MarkDirty();
    }

    // Put back last session's arrangement, or open the first-run pair. The user's own dash only keeps one.
    public void RestoreLayout(bool persist, params DashboardScreen?[] firstRun)
    {
        EnsureBuilt();
        if (_workspace == null)
            return;
        _workspace.PersistLayout = persist;
        (_desk as HomeScreen)?.RestoreDesk(persist);
        var windows = new List<DashWindow>();
        foreach (var screen in firstRun)
        {
            var window = WindowOf(screen);
            if (window != null)
                windows.Add(window);
        }
        _workspace.RestoreLayout(windows);
    }

    public void FlushLayout()
    {
        _workspace?.Flush();
        (_desk as HomeScreen)?.SaveDesk();
    }

    // GEOMETRY, top-down canvas units: x right and y down from the canvas's top-left.

    public Rect DeskRect
    {
        get
        {
            var size = Size.Value;
            float left = Outer + DashDock.Width + DockGap;
            float top = IslandTop + IslandHeight + DeskTopGap;
            return new Rect(left, top, MathF.Max(1f, size.x - left - Outer), MathF.Max(1f, size.y - top - Outer));
        }
    }

    // Where windows fill and snap: the whole desk. Widgets stay behind the windows.
    public Rect WindowZone => DeskRect;

    // The desk short of the widget column on the right: where the first windows of a first run go, so the
    // widgets are in view until somebody moves a window over them.
    public Rect OpenZone
    {
        get
        {
            var desk = DeskRect;
            float width = desk.width - GadgetColumnWidth - GadgetColumnGap;
            if (width < desk.width * 0.5f)
                width = desk.width;
            return new Rect(desk.x, desk.y, width, desk.height);
        }
    }

    public float2 TopDown(in float2 canvasPoint)
        => new(canvasPoint.x + Size.Value.x * 0.5f, Size.Value.y * 0.5f - canvasPoint.y);

    public float2 CanvasPoint(in float2 topDown)
        => new(topDown.x - Size.Value.x * 0.5f, Size.Value.y * 0.5f - topDown.y);

    // STACK

    // How many windows are lifted: the shell rides a step over the top one, and overlays a step over that.
    internal void SetStackHeight(int lifted)
    {
        float shell = DashWorkspace.LiftStep * (lifted + 1);
        if (_shellLayer != null && !_shellLayer.IsDestroyed && _shellLayer.Lift.Value != shell)
            _shellLayer.Lift.Value = shell;
        if (_canvas != null)
        {
            float overlay = shell + DashWorkspace.LiftStep;
            if (_canvas.OverlayLift != overlay)
            {
                _canvas.OverlayLift = overlay;
                _canvas.MarkPlacementDirty();
            }
        }
    }

    internal void NotifyFocus()
    {
        var current = CurrentScreen;
        if (ReferenceEquals(current, _lastCurrent))
            return;
        _lastCurrent = current;
        ScreenChanged?.Invoke(current);
    }

    // A press inside a window raises it; a press on the desk hands the keyboard to the desk.
    private void OnPressBegan(IUIInteractable? target, UIInteractionContext context)
    {
        // A press on the dash takes the keyboard back from any app out in the room.
        DashWindowPanel.ClearKeyFocus();
        var slot = (target as Component)?.Slot;
        _topBar?.OnDashPress(slot);
        _snapPicker?.OnDashPress(slot);
        if (slot == null || _workspace == null)
            return;
        var window = slot.GetComponentInParents<DashWindow>();
        if (window != null)
        {
            if (!window.IsFocused)
                _workspace.Focus(window);
            return;
        }
        if (_deskSlot != null && (ReferenceEquals(slot, _deskSlot) || slot.IsDescendantOf(_deskSlot)))
            _workspace.FocusDesk();
    }

    // A window let go of over the dash, from the room, docks back into it.
    public bool TryReceive(IReadOnlyList<IGrabbable> items, in UIInteractionContext context)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var panel = DashWindowPanel.From(items[i]);
            if (panel == null || !ReferenceEquals(panel.Owner, this) || panel.Window == null)
                continue;
            var grab = panel.GrabHandle;
            grab?.Grabber?.Release(grab);
            _workspace?.DockBack(panel.Window);
            return true;
        }
        return false;
    }

    // GUIDE AND TIP

    // The half (or whole) a window would land in if let go now, or null to take the outline away.
    internal void ShowGuide(Rect? area)
    {
        if (_guide == null || _guide.IsDestroyed)
            return;
        if (area.HasValue)
        {
            var r = area.Value;
            var rect = SettingsUI.Rect(_guide);
            SettingsUI.SetOffsets(rect, new float2(r.x, -(r.y + r.height)), new float2(r.x + r.width, -r.y));
            LayGuideDashes(r.Size);
        }
        if (_guide.ActiveSelf.Value != area.HasValue)
            _guide.ActiveSelf.Value = area.HasValue;
    }

    public int GuideDashCount
    {
        get
        {
            int count = 0;
            foreach (var dash in _guideDashes)
            {
                if (dash.ActiveSelf.Value)
                    count++;
            }
            return count;
        }
    }

    // Dashes along all four edges, kept off the rounded corners, evenly spread over what is left.
    private void LayGuideDashes(float2 size)
    {
        if (_guide == null || size == _guideDashed)
            return;
        _guideDashed = size;
        float inset = DashWindow.Corner;
        int used = GuideEdge(0, true, size.x, inset, size.y - GuideStroke);
        used = GuideEdge(used, true, size.x, inset, 0f);
        used = GuideEdge(used, false, size.y, inset, 0f);
        used = GuideEdge(used, false, size.y, inset, size.x - GuideStroke);
        for (int i = used; i < _guideDashes.Count; i++)
        {
            if (_guideDashes[i].ActiveSelf.Value)
                _guideDashes[i].ActiveSelf.Value = false;
        }
    }

    private int GuideEdge(int used, bool horizontal, float length, float inset, float across)
    {
        float span = length - inset * 2f;
        if (span < GuideDash)
            return used;
        int count = (int)((span + GuideGap) / (GuideDash + GuideGap));
        float total = count * GuideDash + (count - 1) * GuideGap;
        float start = inset + (span - total) * 0.5f;
        for (int i = 0; i < count; i++)
        {
            var rect = SettingsUI.Rect(GuideDashAt(used++));
            float at = start + i * (GuideDash + GuideGap);
            if (horizontal)
                SettingsUI.SetOffsets(rect, new float2(at, across), new float2(at + GuideDash, across + GuideStroke));
            else
                SettingsUI.SetOffsets(rect, new float2(across, at), new float2(across + GuideStroke, at + GuideDash));
        }
        return used;
    }

    private Slot GuideDashAt(int index)
    {
        while (_guideDashes.Count <= index)
        {
            var slot = SettingsUI.Child(_guide!, "Dash", float2.Zero, float2.Zero, float2.Zero, float2.Zero);
            var image = slot.AttachComponent<Image>();
            image.Tint.Value = DashTheme.Accent.WithAlpha(0.8f);
            image.InteractionTarget.Value = false;
            _guideDashes.Add(slot);
        }
        var dash = _guideDashes[index];
        if (!dash.ActiveSelf.Value)
            dash.ActiveSelf.Value = true;
        return dash;
    }

    // The name of a dock plate, beside the dock at that plate's height; null hides it.
    internal void ShowTip(string? text, float centerY, float maxWidth = 260f)
    {
        if (_tip == null || _tip.IsDestroyed || _tipText == null)
            return;
        if (text != null)
        {
            float left = Outer + DashDock.Width + 8f;
            float available = MathF.Max(80f, Size.Value.x - left - Outer);
            float width = MathF.Min(available, MathF.Max(80f, MathF.Min(maxWidth, 24f + text.Length * 8f)));
            SettingsUI.SetOffsets(SettingsUI.Rect(_tip), new float2(left, -(centerY + 16f)), new float2(left + width, -(centerY - 16f)));
            Helio.UI.Listing.ListingStyle.SetText(_tipText, text);
        }
        if (_tip.ActiveSelf.Value != (text != null))
            _tip.ActiveSelf.Value = text != null;
    }

    // A name under the top bar, centred on what it names and kept on the dash; null hides it.
    internal void ShowTipBelow(string? text, float centerX, float top)
    {
        if (_tip == null || _tip.IsDestroyed || _tipText == null)
            return;
        if (text != null)
        {
            float width = MathF.Max(80f, MathF.Min(260f, 24f + text.Length * 8f));
            float left = MathF.Max(Outer, MathF.Min(centerX - width * 0.5f, Size.Value.x - Outer - width));
            SettingsUI.SetOffsets(SettingsUI.Rect(_tip), new float2(left, -(top + 32f)), new float2(left + width, -top));
            Helio.UI.Listing.ListingStyle.SetText(_tipText, text);
        }
        if (_tip.ActiveSelf.Value != (text != null))
            _tip.ActiveSelf.Value = text != null;
    }

    public string? TipText => _tip != null && !_tip.IsDestroyed && _tip.ActiveSelf.Value ? _tipText?.Content.Value : null;

    // A widget that lands on the desk off a carried panel gets the pill chrome and the mono font when it is
    // one of the text widgets, so it looks like the ones that were placed there.
    public void StyleDroppedWidget(WidgetPreset preset)
    {
        if (preset is ConnectionWidgetPreset connection)
        {
            connection.Font.Target = Font.Target;
            connection.BackgroundSprite.Target = _rounded!;
            connection.CornerRadius.Value = DashTheme.RadiusCard;
            connection.Background.Value = DashTheme.GlassCard;
            connection.BorderColor.Value = DashTheme.Outline;
            connection.LabelColor.Value = DashTheme.TextDim;
            return;
        }
        if (preset is not TextWidgetPreset text)
            return;
        text.Font.Target = FontMono.Target ?? Font.Target;
        text.TextSize.Value = DashTheme.FontBody;
        text.TextColor.Value = DashTheme.TextDim;
        text.Background.Value = DashTheme.GlassCard;
        text.BorderColor.Value = DashTheme.Outline;
        text.BackgroundSprite.Target = _rounded!;
        text.CornerRadius.Value = DashTheme.RadiusCard;
    }

    // BUILD

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;

        _rootRect = RectTransform ?? Slot.GetComponent<RectTransform>() ?? Slot.AttachComponent<RectTransform>();
        ApplyRootSize();
        _canvas = Slot.GetComponent<Canvas>() ?? Slot.AttachComponent<Canvas>();
        _canvas.PressBegan += OnPressBegan;

        // A veil, not a wall: the capture is laid over a blurred, darkened view of the world, so this only
        // has to settle the room down behind the widgets, not hide it.
        var background = Slot.GetComponent<Image>() ?? Slot.AttachComponent<Image>();
        background.Tint.Value = DashTheme.Veil;

        _rounded = Slot.AddSlot("Theme").AttachComponent<RoundedRectTextureProvider>();
        _rounded.Size.Value = 48;
        _rounded.Radius.Value = 14;

        var backingSlot = Slot.AddSlot("Backing");
        _backing = backingSlot.AttachComponent<UIUnlitMaterial>();
        _backing.ZWrite.Value = ZWrite.On;

        // The desk: home's widgets, behind every window.
        _deskSlot = Slot.AddSlot("Desk");
        _deskSlot.OrderOffset.Value = DeskOrder;
        var deskRect = _deskSlot.AttachComponent<RectTransform>();
        float deskLeft = Outer + DashDock.Width + DockGap;
        float deskTop = IslandTop + IslandHeight + DeskTopGap;
        deskRect.AnchorMin.Value = float2.Zero;
        deskRect.AnchorMax.Value = float2.One;
        deskRect.OffsetMin.Value = new float2(deskLeft, Outer);
        deskRect.OffsetMax.Value = new float2(-Outer, -deskTop);
        // Flat, but a layer all the same: a desk widget at an overlay level stays on the desk instead of
        // taking the lift a modal hung on the canvas root gets.
        _deskSlot.AttachComponent<StackLayer>();

        _windowsSlot = Slot.AddSlot("Windows");
        _windowsSlot.OrderOffset.Value = WindowsOrder;
        Fill(_windowsSlot.AttachComponent<RectTransform>());
        _workspace = _windowsSlot.AttachComponent<DashWorkspace>();
        _workspace.Bind(this);

        _shellSlot = Slot.AddSlot("Shell");
        _shellSlot.OrderOffset.Value = ShellOrder;
        Fill(_shellSlot.AttachComponent<RectTransform>());
        _shellLayer = _shellSlot.AttachComponent<StackLayer>();

        var dockSlot = _shellSlot.AddSlot("Dock");
        _dock = dockSlot.AttachComponent<DashDock>();
        _dock.Bind(this, _workspace);

        var barSlot = _shellSlot.AddSlot("TopBar");
        _topBar = barSlot.AttachComponent<DashTopBar>();
        var islandSlot = _shellSlot.AddSlot("Island");
        _island = islandSlot.AttachComponent<DashStatusIsland>();
        _island.Bind(this);
        _topBar.Bind(this, _island);

        BuildGuide();
        _snapPicker = _shellSlot.AddSlot("SnapPicker").AttachComponent<DashSnapPicker>();
        _snapPicker.Bind(this);
        BuildTip();
        SetStackHeight(0);
    }

    private void BuildGuide()
    {
        _guide = _shellSlot!.AddSlot("SnapGuide");
        var rect = _guide.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0f, 1f);
        rect.AnchorMax.Value = new float2(0f, 1f);
        // Over the windows it previews for: a tint has to land after their content to tint it.
        _guide.AttachComponent<GraphicChunkRoot>().OverlayLevel = 1;
        var panel = _guide.AttachComponent<RoundedPanel>();
        panel.CornerRadius.Value = DashWindow.Corner;
        panel.Color.Value = DashTheme.Accent.WithAlpha(0.10f);
        panel.OutlineThickness.Value = 0f;
        _guide.ActiveSelf.Value = false;
    }

    private void BuildTip()
    {
        _tip = _shellSlot!.AddSlot("DockTip");
        var rect = _tip.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0f, 1f);
        rect.AnchorMax.Value = new float2(0f, 1f);
        // Over an open top bar popover too, which draws at the modal level.
        _tip.AttachComponent<GraphicChunkRoot>().OverlayLevel = 3;
        // It hangs over whatever window is beside the dock, so it covers like a window does.
        var panel = _tip.AttachComponent<RoundedPanel>();
        panel.CornerRadius.Value = 8f;
        panel.Color.Value = DashTheme.GlassWindow;
        panel.OutlineColor.Value = DashTheme.OutlineStrong;
        panel.OutlineThickness.Value = DashTheme.OutlineWidth;
        panel.Material.Target = _backing!;
        _tipText = SettingsUI.FillLabel(_tip, "Text", FontSemibold.Target ?? Font.Target, DashTheme.FontSmall, DashTheme.Text,
            TextHorizontalAlignment.Center, true);
        _tipText.InteractionTarget.Value = false;
        _tip.ActiveSelf.Value = false;
    }

    private void ApplyRootSize()
    {
        if (_rootRect == null) return;
        SettingsUI.SetAnchors(_rootRect, new float2(0.5f, 0.5f), new float2(0.5f, 0.5f));
        SettingsUI.SetOffsets(_rootRect, Size.Value * -0.5f, Size.Value * 0.5f);
    }

    private static void Fill(RectTransform rect)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }
}

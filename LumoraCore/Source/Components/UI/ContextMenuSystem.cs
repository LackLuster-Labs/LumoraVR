// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Helio.UI;
using Lumora.Core.Audio;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Touch;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.UI;

// what the menu was opened against: the pointer that summoned it and the slot the laser was
// hitting at open time. item sources use this to add contextual actions (equip the avatar you
// point at, etc.).
public sealed class ContextMenuContext
{
    public Slot? Pointer;
    public Slot? Target;

    // which hand summoned the menu - enables stick flick-select
    public Input.Chirality? Side;

    // Whether the summoning hand has a tool equipped. With no hand known, it has none.
    public bool HandHasTool(Slot? userRoot)
    {
        if (Side == null || userRoot == null)
            return false;
        foreach (var hand in userRoot.GetComponentsInChildren<Interaction.HandTool>())
        {
            if (hand.Side.Value == Side)
                return hand.ActiveToolItem.Target is { IsDestroyed: false };
        }
        return false;
    }

    // The summoning hand's grabber when it was holding something at open time: the held objects are
    // the menu's subject then (destroy, duplicate, save, and whatever the items themselves offer). -xlinka
    public Interaction.Grabber? Grabber;
}

// The user's radial context menu. Owns the page stack, collects items from sources, and draws itself as a
// Helio canvas under its own slot, out in the world: everyone in the session sees it open, turn its pages
// and close. Only the owner builds and drives it (see Open/Close), and the canvas answers the owner's
// pointers alone (Canvas.Exclusive), so another user's laser goes straight through it.
//
// Every slot of it is minted in the owner's own byte. The gate lets a user make things under its own root on
// structure alone, but taking something down needs ownership by id; a ring minted in any other byte (the
// world's, which is what a guest's machine used to mint in before its byte was granted) could be put up and
// never taken down again, and the rings stacked. The menu's own slot stays between opens with its font and
// icons; each page is a fresh canvas under it, and a close takes the canvas away. -xlinka
[ComponentCategory("UI/Context Menu")]
public class ContextMenuSystem : Component
{
    // Canvas units are millimetres at the menu's own scale (the user's scale in VR).
    private const float CanvasScale = 0.001f;
    // The look: a hub in the middle that names whatever is under the pointer, separate plates on a ring
    // around it with no text outside, and a thin outer row for history and page turns. Every action keeps
    // its own direction, so a flick of the stick reaches each one. -xlinka
    private const float HubRadius = 48f;
    private const float PlateSize = 50f;
    private const float PlateCorner = 17f;
    // A plate answers inside a rounded square a little wider than it is drawn, nearly a circle, so hover
    // can lift and grow the plate while the area that answers stays put. PlateReach bounds that shape.
    private const float PlateHitSize = 56f;
    private const float PlateHitCorner = 24f;
    private const float PlateReach = 30f;
    private const float PlateGap = 3f;
    private const float IconSize = 24f;
    // The ring the plates sit on grows only when more plates need the room, up to RingMax; a page with more
    // actions than fit there turns over in windows instead.
    private const float RingMin = HubRadius + 8f + PlateReach;
    private const float RingMax = 104f;
    private const float OuterGap = 5f;
    private const float OuterThickness = 18f;
    private const float OuterLength = 48f;
    // The pointer may wander this far past the outermost control before the menu closes.
    private const float EdgeMargin = 30f;
    private const float EdgeMin = 150f;
    private const float AnimationRate = 8f;
    private const float OpeningScale = 0.92f;
    private const float ClosingScale = 1.04f;
    // Plates come in one after another over this share of the opening.
    private const float Stagger = 0.35f;
    private const float FocusArc = 28f;
    // The dial a value item opens (see ContextMenuDial): a track round the hub with the value filled in
    // along it from the lower left, a handle on it at the value, and a tick every tenth outside it. The
    // pointer turns it anywhere past DialFollow, out to the edge; inside that it rests.
    private const float DialTrackInner = 60f;
    private const float DialTrackOuter = 80f;
    private const float DialFillInset = 3f;
    private const float DialHandle = 11f;
    private const float DialTickInner = 84f;
    private const float DialTickOuter = 88f;
    private const float DialFollow = HubRadius + 6f;
    private const float DialReach = 112f;
    // The track a value plate wears along its bottom edge.
    private const float ValueBar = 22f;
    // Where the ring opens past the summoning pointer in VR. A hand carrying something on its beam
    // reads this to keep the load behind the ring while the menu is up. -xlinka
    public const float OpenDistance = 0.075f;
    // A ring build slower than this logs at the normal level, so a stall in someone's log can be put down
    // to the ring or ruled out.
    private const double SlowBuildMs = 50.0;

    private static readonly int PrimaryLimit = LargestRing();

    public readonly Sync<bool> IsOpen = null!;

    // Whether this machine has the menu up. IsOpen only tells other people, and the menu's own logic never
    // asks a synced field whether it is open: one that reads closed while the ring is still up would turn
    // every Close away, leaving the ring and its hold on the sticks behind. -xlinka
    public bool IsShown => _shown;
    private bool _shown;

    // null when closed
    public ContextMenuPage? CurrentPage { get; private set; }

    // null when closed
    public ContextMenuContext? CurrentContext { get; private set; }

    public bool HasPageHistory => _pageStack.Count > 0;

    // Which window of the current page shows, and how many it has, when it holds more actions than the ring.
    public int Window => _window;
    public int WindowCount => _windowCount;

    public event Action<ContextMenuPage>? MenuOpened;
    public event Action? MenuClosed;
    public event Action<ContextMenuPage>? PageChanged;

    private readonly Stack<ContextMenuPage> _pageStack = new();
    // The window each page further back was showing, so going back lands where it was left.
    private readonly Dictionary<ContextMenuPage, int> _windows = new();
    private int _window;
    private int _windowCount = 1;

    private Slot _menuRoot = null!;

    // The menu's own slot, kept between opens; the ring under it comes and goes with the menu.
    public Slot? VisualRoot => _menuRoot != null && !_menuRoot.IsDestroyed ? _menuRoot : null;
    private Slot _canvasSlot = null!;
    private FontProvider _font = null!;
    private readonly Dictionary<string, Lumora.Core.Assets.ImageProvider> _icons = new();

    // Flick select: deflect the summoning hand's stick toward an item, then
    // release to pick it.
    private const float FlickEngageThreshold = 0.65f;
    private const float FlickReleaseThreshold = 0.35f;
    private readonly List<BuiltItem> _builtItems = new();
    private BuiltCenter? _center;
    private ContextMenuItem? _flickItem;
    // The dial on show, and where the next one centres in the menu's own space.
    private BuiltDial? _dial;
    private float3 _dialCentre;

    // The page turns on the outer row. They are items like any other, so hover, flicks and the hub treat
    // them the same; Select knows them by reference.
    private readonly ContextMenuItem _previousWindow = new() { Label = "Previous", IconPath = "back", OnPressed = _ => { } };
    private readonly ContextMenuItem _nextWindow = new() { Label = "Next", IconPath = "forward", OnPressed = _ => { } };

    // Public API

    public void Toggle(ContextMenuContext? context = null)
    {
        if (_shown) Close();
        else Open(context);
    }

    // collects items from RootContextMenuItem and ContextMenuItemSource components under this
    // slot's user hierarchy
    public void Open(ContextMenuContext? context = null)
    {
        // Only the owning user builds/drives their own menu. This component also runs on observers for a
        // REMOTE user's hand; without this gate the observer's own input could open a ghost menu on the
        // remote user's ContextMenuSystem. -xlinka
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
        {
            return;
        }

        // A hand the dash owns has no world pointer to hang a menu off: the press that summoned it is
        // on the panel, and the menu would open on the world behind. -xlinka
        if (IsHandOwnedByUserspace(context?.Side))
        {
            return;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        CurrentContext = context ?? new ContextMenuContext();
        _guardOpeningPress = false;  // radial menu opens on a non-select button; no opening-press guard needed
        _disableAutoClose = false;   // radial keeps its edge-close (point away to dismiss)
        var page = BuildRootPage(CurrentContext);
        if (page.Items.Count == 0)
        {
            LumoraLogger.Debug("ContextMenuSystem: No items to show");
            CurrentContext = null;
            return;
        }

        page.LayoutItems();

        _pageStack.Clear();
        _windows.Clear();
        _window = 0;
        CurrentPage = page;
        _shown = true;
        IsOpen.Value = true;

        _openTime = 0f;
        _edgeArmed = Engine.Current?.InputInterface?.VR_Active == true;
        PositionMenu(CurrentContext.Pointer);
        RebuildVisual();
        SuppressLocomotion();

        MenuOpened?.Invoke(page);
        if (!ReportSlowBuild("Opened", page, started))
            LumoraLogger.Debug($"ContextMenuSystem: Opened '{page.Title}' ({page.Items.Count} items)");
    }

    // Open a small two-button confirm menu (a confirm action + Cancel), bypassing the source-collected root
    // page. Used for the touch-to-equip confirmation popup. -xlinka
    public void OpenConfirm(string title, string confirmLabel, float[] confirmColor, Action onConfirm, ContextMenuContext? context = null)
    {
        var page = new ContextMenuPage(title);
        page.AddItem(new ContextMenuItem
        {
            Label = confirmLabel,
            FillColor = confirmColor,
            OnPressed = _ => { Close(); onConfirm?.Invoke(); },
        });
        page.AddItem(new ContextMenuItem
        {
            Label = "Cancel",
            FillColor = new[] { 0.30f, 0.30f, 0.32f, 0.92f },
            OnPressed = _ => Close(),
        });
        OpenPage(page, context);
    }

    // Open a prebuilt custom page, bypassing the source-collected root page. Item handlers are
    // responsible for calling Close themselves (so multi-step flows can PushPage instead).
    // Persistent: stays open until an item is picked rather than edge-closing when the pointer drifts. -xlinka
    public void OpenPage(ContextMenuPage page, ContextMenuContext? context = null, bool persistent = true)
    {
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;
        if (page == null || page.Items.Count == 0)
            return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        // Carry the summoning hand's laser slot + side (like the radial menu does), or the edge-close ray-cast and
        // desktop mouse-aim have no laser to work with and the menu dismisses itself a frame after opening. -xlinka
        CurrentContext = context ?? new ContextMenuContext { Pointer = Slot };
        _guardOpeningPress = true;   // opened by the primary button, still held: nothing on the ring acts until it lets go
        _openingPrimaryReleased = false;
        _disableAutoClose = persistent;
        page.LayoutItems();
        _pageStack.Clear();
        _windows.Clear();
        _window = 0;
        CurrentPage = page;
        _shown = true;
        IsOpen.Value = true;
        _openTime = 0f;
        _edgeArmed = Engine.Current?.InputInterface?.VR_Active == true;
        PositionMenu(CurrentContext.Pointer);
        RebuildVisual();
        SuppressLocomotion();
        MenuOpened?.Invoke(page);
        ReportSlowBuild("Opened", page, started);
    }

    // True when the work since started took long enough to log.
    private static bool ReportSlowBuild(string what, ContextMenuPage? page, long started)
    {
        double took = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (took <= SlowBuildMs)
            return false;
        LumoraLogger.Log($"ContextMenuSystem: {what} '{page?.Title}' ({page?.Items.Count ?? 0} items) in {took:0} ms");
        return true;
    }

    // PopPage goes back
    public void PushPage(ContextMenuPage page)
    {
        if (page == null || CurrentPage == null) return;

        _windows[CurrentPage] = _window;
        _pageStack.Push(CurrentPage);
        CurrentPage = page;
        _window = 0;
        CurrentPage.LayoutItems();
        RebuildVisual(pageChange: true);
        PageChanged?.Invoke(CurrentPage);
    }

    // closes the menu if already at the root page
    public void PopPage()
    {
        // Same guard as SelectItem: the press that OPENED the menu lands on the center "back" disc (the menu opens
        // under the laser), and an unguarded PopPage there closes the menu instantly. Ignore until that press is
        // released once. -xlinka
        if (_guardOpeningPress) return;

        if (_pageStack.Count == 0) { Close(); return; }

        CurrentPage = _pageStack.Pop();
        _window = RecallWindow(CurrentPage);
        RebuildVisual(pageChange: true);
        PageChanged?.Invoke(CurrentPage);
    }

    private int RecallWindow(ContextMenuPage page)
    {
        if (!_windows.Remove(page, out int window))
            window = 0;
        return window;
    }

    public void Close()
    {
        // Only the owner tears down their own menu - see Open. -xlinka
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
        {
            return;
        }

        if (!_shown) return;

        _pageStack.Clear();
        _windows.Clear();
        _window = 0;
        CurrentPage = null;
        CurrentContext = null;
        _shown = false;
        IsOpen.Value = false;

        ReleaseLocomotionSuppression();
        BeginClosing();
        MenuClosed?.Invoke();
    }

    public void SelectItem(ContextMenuItem item) => Select(item, null, null);

    // Shows the next or previous window of a page too big for one ring, wrapping round at either end.
    public void TurnWindow(int step)
    {
        if (!_shown || CurrentPage == null || _windowCount <= 1)
            return;
        _window = ((_window + step) % _windowCount + _windowCount) % _windowCount;
        RebuildVisual(pageChange: true);
    }

    private void Select(ContextMenuItem item, Button? button, UIInteractionContext? context)
    {
        if (item == null || !item.IsEnabled) return;

        // Ignore the very press that OPENED this menu. The confirm opens on the primary (left/trigger) button, the
        // same button used to select, and that press is still down when the ring appears under it. The canvas
        // already refuses a press that began somewhere else; this is the second lock, for a press that reaches
        // Select some other way. Selection arms once that press is released (UpdateOpeningPressGuard). The radial
        // menu opens on a different button, so it isn't guarded. -xlinka
        if (_guardOpeningPress) return;

        // A plate pressed on the canvas pulses through the canvas itself; a flick has no element to do it.
        if (button == null)
            PulseHand(TouchHaptics.Click);

        if (ReferenceEquals(item, _previousWindow) || ReferenceEquals(item, _nextWindow))
        {
            TurnWindow(ReferenceEquals(item, _nextWindow) ? 1 : -1);
        }
        else if (item.IsValue)
        {
            OpenDial(item, context);
        }
        else if (item.SubPage != null)
        {
            // Back from it lands on the window that holds it, even when code pressed it from another.
            if (WindowOf(item) is int at and >= 0)
                _window = at;
            PushPage(item.SubPage);
        }
        else if (item.OnButton != null)
        {
            // Heard while the ring is still up, so whatever listens sees a button that can be pressed. An item
            // in another window of the page is brought round first, so it has one.
            var by = button ?? ButtonFor(item);
            if (by == null && Reveal(item))
                by = ButtonFor(item);
            if (by == null || by.IsDestroyed)
                return;
            item.OnButton(this, by, context ?? PressAt(by));
            if (!item.KeepsOpen && _shown)
                Close();
        }
        else if (item.KeepsOpen)
        {
            // Its Refresh shows what the press changed on the plate that is still under the pointer.
            item.OnPressed?.Invoke(item);
        }
        else
        {
            var pressed = item.OnPressed;
            Close();
            pressed?.Invoke(item);
        }
    }

    // A value item's page is its dial, centred under the pointer that pressed it: the pointer starts in the
    // hub and the value only moves once it goes out onto the ring. A flick opens it in the middle.
    private void OpenDial(ContextMenuItem item, UIInteractionContext? context)
    {
        if (WindowOf(item) is int window and >= 0)
            _window = window;
        _dialCentre = float3.Zero;
        if (context is { } press && _menuRoot != null && !_menuRoot.IsDestroyed)
        {
            var under = _menuRoot.GlobalPointToLocal(press.WorldPoint);
            _dialCentre = new float3(under.x, under.y, 0f);
        }
        PushPage(new ContextMenuPage(item.Label) { Dial = item });
    }

    private void PulseHand(TouchHaptics preset)
    {
        if (CurrentContext?.Side is Input.Chirality side)
            Haptics.Pulse(World?.LocalUser, side, preset);
    }

    private Button? ButtonFor(ContextMenuItem item)
    {
        foreach (var built in _builtItems)
        {
            if (ReferenceEquals(built.Item, item))
                return built.Button;
        }
        return null;
    }

    // Turns to the window holding this item of the current page; false when no other window does.
    private bool Reveal(ContextMenuItem item)
    {
        int window = WindowOf(item);
        if (window < 0 || window == _window)
            return false;
        _window = window;
        RebuildVisual(pageChange: true);
        return true;
    }

    // The window of the current page this item shows on, or -1.
    private int WindowOf(ContextMenuItem item)
    {
        var page = CurrentPage;
        if (page == null || !page.Items.Contains(item))
            return -1;
        for (int window = 0; window < _windowCount; window++)
        {
            foreach (var placed in Arrange(page, window).Items)
            {
                if (ReferenceEquals(placed.Item, item))
                    return window;
            }
        }
        return -1;
    }

    // A flick has no pointer on the button; the press lands on its middle, from the summoning pointer.
    private UIInteractionContext PressAt(Button button)
    {
        var point = button.Slot.GlobalPosition;
        var origin = CurrentContext?.Pointer is { IsDestroyed: false } pointer ? pointer.GlobalPosition : point;
        var toward = point - origin;
        float distance = toward.Length;
        var source = CurrentContext?.Side switch
        {
            Input.Chirality.Left => UIInteractionSource.VRLeft,
            Input.Chirality.Right => UIInteractionSource.VRRight,
            _ => UIInteractionSource.Desktop,
        };
        return new UIInteractionContext(_canvasSlot.GetComponent<Canvas>()!, source, 0, float2.Zero, point, origin,
            distance > 1e-6f ? toward / distance : float3.Forward, distance, World?.LocalUser);
    }

    // A page of entries from the world. One the ring passed on its way here is gone back to, as it stands
    // now; any other goes on top.
    public void OpenBranch(ContextMenuPage page)
    {
        if (!_shown || CurrentPage == null || page == null || page.Items.Count == 0)
            return;
        ContextMenuPage? passed = null;
        int window = 0;
        if (page.Key != null && Equals(CurrentPage.Key, page.Key))
        {
            passed = CurrentPage;
            window = _window;
        }
        else if (page.Key != null && _pageStack.Any(earlier => Equals(earlier.Key, page.Key)))
        {
            while (!Equals(_pageStack.Peek().Key, page.Key))
                _windows.Remove(_pageStack.Pop());
            passed = _pageStack.Pop();
            window = RecallWindow(passed);
        }
        if (passed == null)
        {
            PushPage(page);
            return;
        }
        // Back on a ring it showed before, under the name it had then, whatever the entry leading back says.
        page.Title = passed.Title;
        CurrentPage = page;
        _window = window;
        CurrentPage.LayoutItems();
        RebuildVisual(pageChange: true);
        PageChanged?.Invoke(CurrentPage);
    }

    // Items that follow something in the world show it as it is now: a label a toggle rewrote, a colour, an
    // icon. A change of state remakes the ring, since it decides whether the plate is a button at all.
    private void RefreshItems()
    {
        if (!_shown || _closing || _canvasSlot == null || _canvasSlot.IsDestroyed)
            return;
        bool remake = false;
        foreach (var built in _builtItems)
        {
            var item = built.Item;
            if (item.Refresh == null)
                continue;
            bool enabled = item.IsEnabled;
            if (!item.Refresh(item))
                continue;
            if (item.IsEnabled != enabled || built.HasIcon != HasIcon(item) || (!built.Outer && (built.Branch != null) != LeadsOn(item)))
            {
                remake = true;
                continue;
            }
            (built.Rich, built.Content) = LabelText(item);
            if (built.Label != null)
            {
                built.Label.RichText.Value = built.Rich;
                built.Label.Content.Value = built.Content;
            }
            built.Accent = ItemAccent(item);
            built.Tinted = item.OutlineColor != null;
            if (built.Icon != null && item.IconAsset != null && !ReferenceEquals(built.Icon.Texture.Target, item.IconAsset))
                built.Icon.Texture.Target = item.IconAsset;
        }
        if (remake)
            RebuildVisual(pageChange: true);
    }

    public override void OnDestroy()
    {
        ReleaseLocomotionSuppression();
        DropStickHold();
        DestroyVisual();
        base.OnDestroy();
    }

    // ── Flick select ────────────────────────────────────────────────────────

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        // The summoning hand moved onto the dash: its world laser is quiet now, so nothing could pick an
        // item or dismiss the ring by pointing away. Close it rather than leave it hanging.
        if (_shown && IsHandOwnedByUserspace(CurrentContext?.Side))
        {
            Close();
            return;
        }
        // The ring taken down by anything but Close still closes properly, and a hold on the sticks never
        // outlives the ring by more than a frame.
        if (_shown && (VisualRoot == null || _canvasSlot == null || _canvasSlot.IsDestroyed))
        {
            Close();
            return;
        }
        // Another world in front: the ring goes, and the hands are that world's now.
        if (_shown && World?.Focus == World.WorldFocus.Background)
            Close();
        if ((_heldState != null || _cursorFreed) && !_shown)
            ReleaseLocomotionSuppression();
        UpdateStickHold();
        RefreshItems();
        UpdateDialInput();
        PinToView();
        UpdateVisuals(delta);
        UpdateOpeningPressGuard();
        UpdateFlickSelect();
        UpdateEdgeClose(delta);
    }

    // VR only: on desktop the hand tools stand down as a whole while the dash is up and never reach the
    // menu, and the dash flag there is set hit or no hit.
    public static bool IsHandOwnedByUserspace(Input.Chirality? side)
    {
        if (side == null)
            return false;
        var input = Engine.Current?.InputInterface;
        return input != null && input.IsVRActive && input.IsUserspaceLaserActive(side.Value);
    }

    // One lerp drives open and close. Opening scales the canvas up from 92% as it fades in, and the plates
    // slide out from the hub one after another; a page change replays it faster. Closing fades everything
    // together while the canvas drifts a touch larger, then takes it down. Hover eases on each item's own
    // lerp and never moves a hit area. Fields are rewritten each frame from these; one that does not move
    // costs nothing. -xlinka
    private float _openLerp = 1f;
    private float _openRate = AnimationRate;
    private bool _closing;
    // Whether the ring is still easing onto the summoning pointer (VR, while it opens).
    private bool _placing;

    private void UpdateVisuals(float delta)
    {
        if (_canvasSlot == null || _canvasSlot.IsDestroyed)
            return;

        if (_closing)
        {
            _openLerp = MathF.Max(0f, _openLerp - delta * AnimationRate);
            if (_openLerp <= 0f)
            {
                DestroyCanvas();
                return;
            }
        }
        else if (_openLerp < 1f)
        {
            _openLerp = MathF.Min(1f, _openLerp + delta * _openRate);
            if (_placing)
                TrackPointer();
            if (_openLerp >= 1f)
                _placing = false;
        }

        float shown = Smoother(_openLerp);
        float scale = _closing ? ClosingScale + (1f - ClosingScale) * shown : OpeningScale + (1f - OpeningScale) * shown;
        _canvasSlot.LocalScale.Value = float3.One * (CanvasScale * scale);
        LayoutVisuals(delta, shown);
    }

    private static float Smoother(float t)
    {
        t = System.Math.Clamp(t, 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    private static float MoveTowards(float value, float target, float step)
        => value < target ? MathF.Min(value + step, target) : MathF.Max(value - step, target);

    private void LayoutVisuals(float delta, float shown)
    {
        BuiltItem? hovered = null;
        int count = _builtItems.Count;
        for (int i = 0; i < count; i++)
        {
            var built = _builtItems[i];
            bool hover = !_closing && built.Item.IsEnabled && built.Button != null && !built.Button.IsDestroyed
                && (built.Button.IsHovering.Value || ReferenceEquals(built.Item, _flickItem));
            if (hover)
                hovered = built;
            built.Hovered = hover;
            built.Hover = MoveTowards(built.Hover, hover ? 1f : 0f, delta * AnimationRate * 1.5f);
            ApplyItem(built, Appear(i, count, shown));
        }
        UpdateCenter(hovered, shown, delta);
        if (_dial != null)
            LayoutDial(delta, shown);
    }

    // How far in an item is: the whole menu fades as one when closing, and on opening each item starts a
    // little after the one before it.
    private float Appear(int index, int count, float shown)
    {
        if (_closing || count <= 1)
            return shown;
        float delay = Stagger * index / (count - 1);
        return Smoother((_openLerp - delay) / (1f - Stagger));
    }

    // The ground, edge, ink and size of one item for this frame, from its state. -xlinka
    private void ApplyItem(BuiltItem built, float appear)
    {
        var item = built.Item;
        bool enabled = item.IsEnabled;
        bool actionable = built.Button != null && !built.Button.IsDestroyed;
        bool pressed = actionable && built.Button!.IsPressed.Value;
        bool on = item.IsToggle && item.IsToggled;
        float hover = Smoother(built.Hover);

        color ground, edge;
        float edgeWidth;
        if (!enabled)
        {
            ground = Faded(PlateRest, 0.45f);
            edge = Faded(Hairline, 0.5f);
            edgeWidth = 1f;
        }
        else if (!actionable)
        {
            ground = color.Transparent;
            edge = Hairline;
            edgeWidth = 1f;
        }
        else
        {
            ground = color.Lerp(built.Outer ? OuterRest : PlateRest, PlateHover, hover);
            if (on)
                ground = color.Lerp(ground, built.Accent, 0.14f);
            if (pressed)
                ground = PlatePressed;
            var rest = built.Tinted ? built.Accent : Hairline;
            edge = color.Lerp(rest, built.Accent, hover);
            edgeWidth = built.Tinted || hover > 0.5f ? 1.5f : 1f;
        }
        var ink = !enabled ? Faded(DashTheme.TextMuted, 0.7f) : !actionable ? DashTheme.TextDim : DashTheme.Text;

        if (built.Plate != null)
        {
            built.Plate.Color.Value = Faded(ground, appear);
            built.Plate.OutlineColor.Value = Faded(edge, appear);
            built.Plate.OutlineThickness.Value = edgeWidth;
            // Lifted outward on hover and sunk on a press; slid out from the hub while it opens.
            float lift = hover * 1.5f - (pressed ? 1f : 0f) - (1f - appear) * 10f;
            float size = PlateSize + hover * 2f - (pressed ? 2f : 0f);
            CenterRect(built.PlateRect!, built.Direction * lift, new float2(size, size));
        }
        if (built.Arc != null)
        {
            built.Arc.Tint.Value = Faded(ground, appear);
            built.Arc.OutlineColor.Value = Faded(edge, appear);
            built.Arc.OutlineThickness.Value = edgeWidth;
        }
        if (built.Icon != null)
            built.Icon.Tint.Value = Faded(ink, appear);
        if (built.Label != null)
            built.Label.Color.Value = Faded(ink, appear);
        if (built.Status != null)
            built.Status.Color.Value = Faded(on ? built.Accent : Faded(DashTheme.TextMuted, 0.5f), appear);
        if (built.ValueFill != null && built.ValueFillRect != null && item.ReadValue != null)
        {
            float value = ContextMenuDial.Clamp01(item.ReadValue());
            float width = MathF.Max(3f, ValueBar * value);
            CenterRect(built.ValueFillRect, new float2((width - ValueBar) * 0.5f, -19.5f), new float2(width, 3f));
            built.ValueTrack!.Color.Value = Faded(Faded(DashTheme.TextMuted, 0.35f), appear);
            built.ValueFill.Color.Value = Faded(value > 0.001f ? built.Accent : color.Transparent, appear);
        }
        if (built.Branch != null)
            built.Branch.Tint.Value = Faded(DashTheme.TextDim, appear * (enabled ? 0.85f : 0.4f));
    }

    // The hub names the hovered item, with the page above and its state below, and a short arc on its rim
    // turns to point at it. With nothing hovered it says what pressing the hub does. -xlinka
    private void UpdateCenter(BuiltItem? hovered, float shown, float delta)
    {
        var center = _center;
        if (center == null || center.Disc.IsDestroyed)
            return;
        bool own = hovered == null && !_closing && !center.Button.IsDestroyed;
        bool hubHover = own && center.Button.IsHovering.Value;
        bool hubPress = hubHover && center.Button.IsPressed.Value;
        center.Disc.Tint.Value = Faded(hubPress ? PlatePressed : hubHover ? PlateHover : HubRest, shown);
        center.Disc.OutlineColor.Value = Faded(hubHover ? DashTheme.Accent : Hairline, shown);
        center.Rail.Tint.Value = Faded(RailColor, shown);
        center.Title.Color.Value = Faded(DashTheme.TextMuted, shown);
        center.Label.Color.Value = Faded(DashTheme.Text, shown);

        center.Title.Content.Value = center.TitleText;

        string detail;
        bool lit = false;
        if (hovered != null)
        {
            var item = hovered.Item;
            center.Label.RichText.Value = hovered.Rich;
            center.Label.Content.Value = hovered.Content;
            if (ReferenceEquals(item, _previousWindow))
                detail = center.PreviousText;
            else if (ReferenceEquals(item, _nextWindow))
                detail = center.NextText;
            else if (item.IsToggle)
            {
                detail = item.IsToggled ? "On" : "Off";
                lit = item.IsToggled;
            }
            else if (item.IsValue)
            {
                detail = ContextMenuDial.Percent(item.ReadValue!());
                lit = true;
            }
            else
                detail = LeadsOn(item) ? "Open" : "Select";
            if (!string.IsNullOrEmpty(item.Detail))
                detail = item.Detail;
        }
        else
        {
            center.Label.RichText.Value = false;
            center.Label.Content.Value = HasPageHistory ? "Back" : "Close";
            detail = center.WindowText;
        }
        center.Detail.Content.Value = detail;
        center.Detail.Color.Value = Faded(lit ? hovered!.Accent : DashTheme.TextDim, shown);

        // The pointer arc swings the short way round to the hovered item and fades out when nothing is. It
        // lands exactly, so a settled hover leaves the mesh alone.
        float target = hovered?.Item.AngleMiddle ?? center.FocusAngle;
        if (center.FocusShown <= 0f)
            center.FocusAngle = target;
        float turn = target - center.FocusAngle;
        turn -= MathF.Floor((turn + 180f) / 360f) * 360f;
        center.FocusAngle = MathF.Abs(turn) < 0.05f ? target : center.FocusAngle + turn * MathF.Min(1f, delta * 16f);
        center.FocusShown = MoveTowards(center.FocusShown, hovered != null ? 1f : 0f, delta * AnimationRate * 1.5f);
        center.Focus.AngleStart.Value = center.FocusAngle - FocusArc * 0.5f;
        center.Focus.Tint.Value = Faded(hovered?.Accent ?? DashTheme.Accent, shown * Smoother(center.FocusShown));
    }

    private static color Faded(in color c, float shown) => new(c.r, c.g, c.b, c.a * shown);

    // Theme colours are linear already, which is what the canvas takes. The plates are a step lighter than
    // the hub so the name in the middle sits on the darkest ground; edges are white hairlines, and colour
    // only marks what means something: the accent on what is hovered or on, red on what destroys, and an
    // item's own colour when it brings one. -xlinka
    private static readonly color HubRest = Faded(DashTheme.Panel, 0.96f);
    private static readonly color PlateRest = Faded(DashTheme.Surface, 0.94f);
    private static readonly color OuterRest = Faded(DashTheme.Panel, 0.92f);
    private static readonly color PlateHover = color.Lerp(DashTheme.SurfaceHover, DashTheme.Accent, 0.12f);
    private static readonly color PlatePressed = DashTheme.SurfacePressed;
    private static readonly color Hairline = new(1f, 1f, 1f, 0.1f);
    private static readonly color RailColor = new(1f, 1f, 1f, 0.06f);
    // The dial's track is solid: the few percent a plate lets through turns a dark ground the colour of a
    // bright sky behind it.
    private static readonly color TrackGround = new(PlateRest.r, PlateRest.g, PlateRest.b, 1f);

    private static color ItemAccent(ContextMenuItem item)
    {
        if (item.OutlineColor is { Length: >= 3 } outline)
            return new color(outline[0], outline[1], outline[2], 1f);
        var fill = item.FillColor;
        if (fill != null && fill.Length >= 3 && fill[0] > 0.3f && fill[0] > fill[1] * 1.6f)
            return DashTheme.Negative;
        return DashTheme.Accent;
    }

    // The confirm menu opens on the primary (left/trigger) button, which is also the select button. Ignore item
    // selection until that opening press is released once, or it picks an item and closes the menu the moment it
    // opens. Cleared the frame AFTER release, so nothing that runs on that release gets through either. -xlinka
    private bool _guardOpeningPress;
    private bool _openingPrimaryReleased;
    // Confirm menus are PERSISTENT: they stay open until a button is clicked (Equip/Cancel), with no auto-close
    // when the pointer wanders. The radial menu keeps its edge-close. -xlinka
    private bool _disableAutoClose;

    private void UpdateOpeningPressGuard()
    {
        if (!_guardOpeningPress) return;
        if (_openingPrimaryReleased)
        {
            _guardOpeningPress = false;
            return;
        }
        if (!OpeningHandPrimaryHeld())
            _openingPrimaryReleased = true;
    }

    private bool OpeningHandPrimaryHeld()
    {
        var side = CurrentContext?.Side;
        var userRoot = Slot?.ActiveUserRoot?.Slot;
        if (side == null || userRoot == null) return false;
        foreach (var hand in userRoot.GetComponentsInChildren<Lumora.Core.Components.Interaction.HandTool>())
            if (hand.Side.Value == side) return hand.PrimaryHeld;
        return false;
    }

    // Close when the pointer wanders past the outermost control by EdgeMargin: moving the aim well off the
    // menu dismisses it without a button press. Set with each layout.
    private float _edgeRadius = EdgeMin;
    private float _openTime;
    // On a screen the edge only closes the ring once the pointer has been over its controls: one that opens
    // with the arrow outside its reach (pulled in from the window's edge) waits for the arrow to come in rather
    // than shutting itself a moment later. In a headset it is armed from the start, as it always was.
    private bool _edgeArmed;

    private void UpdateEdgeClose(float delta)
    {
        if (!_shown || _canvasSlot == null || _canvasSlot.IsDestroyed)
            return;

        // Persistent (confirm) menus never auto-close on pointer movement - only an item click closes them. -xlinka
        if (_disableAutoClose)
            return;

        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;

        // Grace so the menu doesn't instantly dismiss while it settles.
        _openTime += delta;
        if (_openTime < 0.35f)
            return;

        // Pointing away from the plane entirely also counts as leaving.
        if (!TryAimPoint(out var point, out bool behind))
        {
            if (behind && _edgeArmed)
                Close();
            return;
        }
        float reach = MathF.Sqrt(point.x * point.x + point.y * point.y);
        if (reach <= _edgeRadius - EdgeMargin)
            _edgeArmed = true;
        else if (reach > _edgeRadius && _edgeArmed)
            Close();
    }

    // Where the summoning pointer's ray crosses the ring's plane, in canvas units about the ring's middle.
    // False with no ray to cast, and with behind set when it points away from the plane.
    private bool TryAimPoint(out float2 point, out bool behind)
    {
        point = float2.Zero;
        behind = false;
        if (_canvasSlot == null || _canvasSlot.IsDestroyed)
            return false;

        Slot? rayOrigin = CurrentContext?.Pointer;
        if (rayOrigin == null || rayOrigin.IsDestroyed)
            rayOrigin = Slot?.ActiveUserRoot?.HeadSlot;
        if (rayOrigin == null || rayOrigin.IsDestroyed)
            return false;

        // Use the laser's actual cast ray when available - on desktop the slot pose isn't the ray (head or
        // camera, plus the mouse deflection, is). The CAST ray specifically: under an external camera the
        // laser's exposed ray is re-anchored at the hand, and a hand standing metres off the view line
        // crosses this plane somewhere else entirely (often behind it, which reads as pointing away and
        // slams the menu shut the moment the aim leaves the ring). -xlinka
        float3 origin, direction;
        var laser = rayOrigin.GetComponent<Interaction.InteractionLaser>();
        var input = Engine.Current?.InputInterface;
        if (input?.IsVRActive == false && input.CursorFree && input.DesktopCursorRayValid
            && TryDesktopView(out var viewPosition, out var viewRotation))
        {
            // The menu has already followed the view this update. Use the same view for its pointer,
            // or walking makes yesterday's cast look like the cursor left the ring. -xlinka
            origin = viewPosition;
            direction = viewRotation * (input.DesktopCameraRotation.Inverse * input.DesktopCursorRayDirection);
        }
        else if (laser != null)
        {
            origin = laser.CastOrigin;
            direction = laser.CastDirection;
        }
        else
        {
            origin = rayOrigin.GlobalPosition;
            direction = rayOrigin.GlobalRotation * float3.Backward;
        }

        var planeNormal = _canvasSlot.GlobalRotation * float3.Forward;
        float denom = float3.Dot(direction, planeNormal);
        if (MathF.Abs(denom) < 1e-4f)
            return false;

        float t = float3.Dot(_canvasSlot.GlobalPosition - origin, planeNormal) / denom;
        if (t < 0f)
        {
            behind = true;
            return false;
        }

        var local = _canvasSlot.GlobalPointToLocal(origin + direction * t);
        point = new float2(local.x, local.y);
        return true;
    }

    // The dial turns to the stick while it is pushed out and to the pointer anywhere past the hub, and
    // the value is written as it turns, so the avatar changes under the turn rather than on a release.
    private void UpdateDialInput()
    {
        var dial = _dial;
        if (dial == null || !_shown || _closing || _canvasSlot == null || _canvasSlot.IsDestroyed)
            return;
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;
        var item = dial.Item;
        item.Refresh?.Invoke(item);
        if (!item.IsEnabled || !item.IsValue)
        {
            PopPage();
            return;
        }

        float current = ContextMenuDial.Clamp01(item.ReadValue!());
        float? wanted = null;
        dial.Turning = false;
        var side = CurrentContext?.Side;
        var actions = Engine.Current?.InputInterface?.Actions;
        if (side != null && actions != null && HasStick(side.Value))
        {
            var push = actions.Interaction(side.Value).Stick.Value;
            float magnitude = MathF.Sqrt(push.x * push.x + push.y * push.y);
            if (magnitude >= FlickEngageThreshold)
            {
                dial.Turning = dial.Stick.IsArmed;
                wanted = dial.Stick.Point(ContextMenuDial.AngleAt(push.x, push.y));
            }
            else if (magnitude <= FlickReleaseThreshold)
            {
                dial.Stick.Centre();
            }
        }
        dial.Aimed = false;
        if (!dial.Turning && TryAimPoint(out var point, out _))
        {
            float radius = MathF.Sqrt(point.x * point.x + point.y * point.y);
            if (radius < DialFollow)
            {
                dial.Pointer.Centre();
            }
            else if (radius <= _edgeRadius)
            {
                wanted = dial.Pointer.Point(ContextMenuDial.AngleAt(point.x, point.y));
                dial.Aimed = dial.Pointer.IsArmed;
            }
        }

        if (wanted is not float value || MathF.Abs(value - current) < 1e-4f)
            return;
        (dial.Turning ? dial.Pointer : dial.Stick).Yield();
        item.WriteValue!(value);
        // A tick in the hand at each tenth passed and a firmer one at either end.
        if ((value <= 0f || value >= 1f) && current > 0f && current < 1f)
            PulseHand(TouchHaptics.Medium);
        else if ((int)MathF.Floor(value * 10f + 1e-4f) != (int)MathF.Floor(current * 10f + 1e-4f))
            PulseHand(TouchHaptics.Light);
    }

    // Deflect the summoning hand's stick toward an item, release to pick it. The ring holds that stick while
    // it is up (UpdateStickHold), so the gesture never walks or snap-turns.
    private void UpdateFlickSelect()
    {
        if (!_shown || _builtItems.Count == 0)
            return;

        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;

        var side = CurrentContext?.Side;
        if (side == null)
            return;

        if (!HasStick(side.Value))
        {
            if (_flickItem != null)
                SetFlickItem(null);
            return;
        }

        var actions = Engine.Current?.InputInterface?.Actions;
        if (actions == null)
            return;

        var stick = actions.Interaction(side.Value).Stick.Value;
        float magnitude = MathF.Sqrt(stick.x * stick.x + stick.y * stick.y);

        if (magnitude >= FlickEngageThreshold)
        {
            // Stick up = top item: same clockwise-from-right angle convention
            // as the arc layout.
            float angle = MathF.Atan2(-stick.y, stick.x) * (180f / MathF.PI);
            SetFlickItem(FindItemAtAngle(angle));
        }
        else if (magnitude <= FlickReleaseThreshold && _flickItem != null)
        {
            var selected = _flickItem;
            SetFlickItem(null);
            Select(selected, null, null);
        }
    }

    // The action, on either row, whose direction is nearest the stick's, so the space between controls is
    // never a dead zone for a flick. Only what can be pressed takes part.
    private ContextMenuItem? FindItemAtAngle(float angle)
    {
        ContextMenuItem? best = null;
        float bestDistance = float.MaxValue;
        foreach (var built in _builtItems)
        {
            var item = built.Item;
            if (!item.IsEnabled || built.Button == null)
                continue;
            float distance = MathF.Abs(angle - item.AngleMiddle);
            distance -= MathF.Floor(distance / 360f) * 360f;
            distance = MathF.Min(distance, 360f - distance);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = item;
            }
        }
        return best;
    }

    // The flicked item shows exactly as a hovered one does; LayoutVisuals reads it.
    private void SetFlickItem(ContextMenuItem? item)
    {
        if (item != null && !ReferenceEquals(item, _flickItem))
            PulseHand(TouchHaptics.Light);
        _flickItem = item;
    }

    // ── The summoning hand's stick ──────────────────────────────────────────

    // In a headset the ring shares one control with locomotion: its own hand's stick, which the flick and the
    // dial read. That is all it takes. The other hand walks, turns and flies as if the ring were not there, and
    // the summoning hand keeps its trigger for the plates, its grip, its stick click and, where the controller
    // has one beside the stick, its trackpad. The ring used to take the whole desktop input channel here, and
    // in a headset that silenced every locomotion module on both hands for as long as it was up.
    //
    // The hold is on the controls the hand's flick action is bound to (the thumbstick, or a wand's trackpad,
    // or whatever the user moved it to), at the hand's own priority in the action map: the flick reads them
    // live, walking and turning a set below read them centred. The ring takes the stick once it has seen it at
    // rest since opening, so a thumb still walking when a confirm comes up keeps walking and a stick already
    // pushed never picks anything, and keeps it past the close until it is back at rest, so the release that
    // picked an item never turns or walks. -xlinka
    private const float StickRest = 0.15f;
    // A ring that has not stepped for this many input passes stops holding, whatever it last said. Counted in
    // passes, not time, so one long hitch never lets a held stick through.
    private const int StickHoldStalePasses = 30;

    private static readonly Input.Chirality[] StickHands = { Input.Chirality.Left, Input.Chirality.Right };

    // One bit per hand whose stick the ring has. Written on the world update, read by the input pass.
    private int _stickHands;
    private int _stickPassesSinceStep;
    private Input.Actions.InputBindingMap? _stickMap;

    private static int StickBit(Input.Chirality side)
        => side == Input.Chirality.Left ? 1 : side == Input.Chirality.Right ? 2 : 0;

    // Whether the flick and the dial may read this hand's stick: in a headset once the ring has it. On a screen
    // a pad's stick is the ring's while it is up; the view stands still for it (SuppressLocomotion).
    private bool HasStick(Input.Chirality side)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive)
            return true;
        return (System.Threading.Volatile.Read(ref _stickHands) & StickBit(side)) != 0;
    }

    private void UpdateStickHold()
    {
        var input = Engine.Current?.InputInterface;
        var map = input?.Actions;
        int hands = System.Threading.Volatile.Read(ref _stickHands);
        if (map == null)
        {
            hands = 0;
        }
        else
        {
            int wanted = 0;
            if (_shown && input!.IsVRActive && World != null && World.Focus != World.WorldFocus.Background
                && Slot?.ActiveUserRoot?.ActiveUser == World.LocalUser && CurrentContext?.Side is Input.Chirality side)
                wanted = StickBit(side);
            foreach (var hand in StickHands)
            {
                int bit = StickBit(hand);
                bool atRest = map.Interaction(hand).Stick.Value.Length <= StickRest;
                if ((wanted & bit) != 0)
                {
                    if (atRest)
                        hands |= bit;
                }
                else if (atRest)
                {
                    hands &= ~bit;
                }
            }
        }
        System.Threading.Volatile.Write(ref _stickPassesSinceStep, 0);
        System.Threading.Volatile.Write(ref _stickHands, hands);
        BindStickHold(hands != 0 ? map : null);
    }

    private void DropStickHold()
    {
        System.Threading.Volatile.Write(ref _stickHands, 0);
        BindStickHold(null);
    }

    private void BindStickHold(Input.Actions.InputBindingMap? map)
    {
        if (ReferenceEquals(map, _stickMap))
            return;
        if (_stickMap != null)
            _stickMap.BeforeEvaluate -= HoldSticks;
        _stickMap = map;
        if (map != null)
            map.BeforeEvaluate += HoldSticks;
    }

    // In the input pass, before any action is read. A hold lapses a pass after the last ask, so this asks every
    // pass; one from a ring that is gone, switched off or behind another world asks nothing.
    private void HoldSticks()
    {
        var map = _stickMap;
        int hands = System.Threading.Volatile.Read(ref _stickHands);
        if (map == null || hands == 0)
            return;
        if (System.Threading.Interlocked.Increment(ref _stickPassesSinceStep) > StickHoldStalePasses
            || IsDestroyed || !Enabled.Value || World == null || World.Focus == World.WorldFocus.Background)
            return;
        foreach (var hand in StickHands)
        {
            if ((hands & StickBit(hand)) == 0)
                continue;
            var actions = map.Interaction(hand);
            var bindings = actions.Stick.Bindings;
            for (int i = 0; i < bindings.Count; i++)
                map.HoldControl(actions.Set, bindings[i].Control);
        }
    }

    // The input state the menu is holding down, so the release lands on the same one. It used to ask for the
    // focused world's on both ends, and a world switch while the ring was up (a join finishing, a portal)
    // released the wrong world's: the one the ring was in kept its locomotion off until the menu was
    // opened and closed there again. -xlinka
    private UserInputState? _heldState;

    private UserInputState? OwnInputState()
        => Slot?.ActiveUserRoot?.Slot?.GetComponent<UserInputState>() ?? UserInputState.ForFocusedLocalUser;

    private void SuppressLocomotion()
    {
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;

        // On a screen the ring frees the cursor while it is up and the cursor is the pointer: the laser rides
        // the ray through it, so in any view the item under the arrow is the one that lights, where a mouse
        // steering an unseen offset off a frozen crosshair was hard to land in third person or free cam. A pad
        // has no cursor to free and keeps steering the frozen aim. -xlinka
        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive
            && input.Actions?.HintDevice != Lumora.Core.Input.Actions.InputDeviceKind.Gamepad)
        {
            input.SetCursorFree(this, true);
            _cursorFreed = true;
        }

        // In a headset the ring takes nothing here: its hand's stick is held in the action map instead
        // (UpdateStickHold), and walking, turning and the other hand carry on.
        if (Engine.Current?.InputInterface?.VR_Active == true)
            return;

        var state = OwnInputState();
        if (state == null)
            return;
        if (_heldState != null && !ReferenceEquals(_heldState, state))
            ReleaseLocomotionSuppression();
        _heldState = state;

        // The view holds still under the ring in every desktop mode: head look, the orbit and the free camera
        // turn for nothing while it is up, and WASD still walks. It used to pick a hold by the mode it opened in
        // and take the third-person flag in first person, so a camera switch while it was up left the head
        // turning under the arrow, and closing it handed the mouse back to the head in third person. -xlinka
        state.SetViewHeld(this, true);
    }

    private bool _cursorFreed;

    private void ReleaseLocomotionSuppression()
    {
        if (_cursorFreed)
        {
            _cursorFreed = false;
            Engine.Current?.InputInterface?.SetCursorFree(this, false);
        }

        var state = _heldState;
        _heldState = null;
        state?.SetDesktopInputSuppressed(this, false);
        state?.SetViewHeld(this, false);
    }

    // Visual (a replicated Helio canvas, built and driven by the owner only)

    private void PositionMenu(Slot? pointer)
    {
        EnsureVisualRoot();
        _placing = false;
        _viewPinned = false;

        var head = Slot.ActiveUserRoot?.HeadSlot;
        var input = Engine.Current?.InputInterface;
        bool vrActive = input?.VR_Active == true;

        // F6 free-cam flies a drone camera away from the body and parks the head wherever mouse look last
        // left it, so the head is the wrong viewpoint the whole time it is up. `external` below is a flag
        // the PLATFORM publishes each frame off the head output's pose override (HeadOutput.
        // UpdateScreenPositioning -> InputInterface.SetDesktopCameraOverride); the free-cam flag is written
        // by the camera controller in the mode switch itself (DesktopCameraController.SwitchMode ->
        // UserInputState.SetFreeCamActive), so it cannot be a frame - or a missed publish - out of step with
        // the mode the user is actually flying. Either one is enough to anchor on the camera, because a
        // free-cam that falls through to the head branch opens the ring at the avatar's body, metres behind
        // the lens the user is looking through. -xlinka
        bool freeCam = !vrActive
            && input?.DesktopCameraPoseValid == true
            && UserInputState.FocusedFreeCamActive;

        // The viewpoint the menu opens in front of and turns to face. First person is the head; third-person
        // and free-cam are a camera flying somewhere else entirely, and putting the menu on the parked head
        // there would drop it off screen (or inside the avatar's skull) where nothing can click it. -xlinka
        bool external = freeCam || input?.DesktopExternalCameraAim == true;
        bool hasView = external || head != null;
        float3 viewPosition = external ? input!.DesktopCameraPosition : (head?.GlobalPosition ?? float3.Zero);
        floatQ viewRotation = external ? input!.DesktopCameraRotation : (head?.GlobalRotation ?? floatQ.Identity);

        // VR: a hand's length past the pointer, eased onto the hand while it opens (TrackPointer).
        if (vrActive && pointer != null && !pointer.IsDestroyed)
        {
            _menuRoot.LocalScale.Value = float3.One;
            if (TryPointerPose(pointer, hasView ? viewPosition : pointer.GlobalPosition, out var position, out var rotation))
            {
                _menuRoot.GlobalPosition = position;
                _menuRoot.GlobalRotation = rotation;
                _placing = true;
            }
            return;
        }

        // Desktop: half a metre out (scaled with the avatar, except for the drone camera, which does not grow
        // with it) under the pointer, the loose arrow or the crosshair, square to the lens and a constant size on
        // screen whatever the distance or the avatar's scale. From then on it is held in the view's own space
        // (PinToView). -xlinka
        if (hasView && !vrActive)
        {
            float scale = freeCam ? 1f : (Slot.ActiveUserRoot?.GlobalScale ?? 1f);
            _viewOffset = OpenDirection(input, viewRotation) * (0.5f * scale);
            _viewPinned = true;
            PinToView();
            return;
        }

        // A headset with no pointer to open off: in front of the head.
        if (hasView)
        {
            float scale = freeCam ? 1f : (Slot.ActiveUserRoot?.GlobalScale ?? 1f);
            float distance = 0.5f * scale;
            _menuRoot.GlobalPosition = viewPosition + viewRotation * float3.Backward * distance;
            if (Utility.UserFacing.TryLookRotation(
                    _menuRoot.GlobalPosition, viewPosition, viewRotation * float3.Up, yawOnly: false, out var facing))
            {
                _menuRoot.GlobalRotation = facing;
            }
            float parentScale = MathF.Max(Slot.GlobalScale.x, 1e-4f);
            _menuRoot.LocalScale.Value = float3.One * (1.5f * distance / parentScale);
            return;
        }

        if (pointer != null && !pointer.IsDestroyed
            && TryPointerPose(pointer, pointer.GlobalPosition, out var fallbackPosition, out var fallbackRotation))
        {
            _menuRoot.LocalScale.Value = float3.One;
            _menuRoot.GlobalPosition = fallbackPosition;
            _menuRoot.GlobalRotation = fallbackRotation;
        }
    }

    // Where the ring sits for a pointer: OpenDistance past its tip along the beam, facing halfway between
    // the beam and the line from the eye, so it is tipped toward the hand that summoned it yet still reads
    // from the head, and kept upright to the user instead of rolling with the wrist. -xlinka
    private bool TryPointerPose(Slot pointer, in float3 eye, out float3 position, out floatQ rotation)
    {
        var beam = (pointer.GlobalRotation * float3.Backward).Normalized;
        float scale = Slot.ActiveUserRoot?.GlobalScale ?? 1f;
        position = pointer.GlobalPosition + beam * (OpenDistance * scale);
        var sight = position - eye;
        var facing = sight.LengthSquared > 1e-8f ? (beam + sight.Normalized) : beam;
        if (facing.LengthSquared < 1e-8f)
            facing = beam;
        facing = facing.Normalized;
        var up = (Slot.ActiveUserRoot?.Slot.GlobalRotation ?? floatQ.Identity) * float3.Up;
        return Utility.UserFacing.TryLookRotation(position, position - facing, up, yawOnly: false, out rotation);
    }

    // While the ring opens it eases onto the moving hand; once open it stays where it settled and rides
    // along with the user. Each frame it keeps the sixth power of the open lerp of where it was, so it
    // follows the hand closely at first and has stopped by the time it is fully open. -xlinka
    private void TrackPointer()
    {
        var pointer = CurrentContext?.Pointer;
        var head = Slot.ActiveUserRoot?.HeadSlot;
        if (_menuRoot == null || _menuRoot.IsDestroyed || pointer == null || pointer.IsDestroyed || head == null)
        {
            _placing = false;
            return;
        }
        if (!TryPointerPose(pointer, head.GlobalPosition, out var position, out var rotation))
            return;
        float keep = MathF.Pow(_openLerp, 6f);
        _menuRoot.GlobalPosition = float3.Lerp(position, _menuRoot.GlobalPosition, keep);
        _menuRoot.GlobalRotation = floatQ.Slerp(rotation, _menuRoot.GlobalRotation, keep);
    }

    // DESKTOP: THE RING STAYS IN VIEW
    //
    // On a screen the ring is held where it opened in the view's own space for as long as it is up. It used to
    // be left in the user's space, so anything that moved the view off the body took it out of sight: a camera
    // switch (the third-person lens two metres back, the free camera flying back to the head), the third-person
    // body turning to face where it walks, the shoulder easing over while it opened. The edge close then read
    // the arrow as gone off the ring and shut it, which is the menu that kept vanishing. -xlinka
    private bool _viewPinned;
    // Metres from the eye in the view's space, the view looking down -Z.
    private float3 _viewOffset;
    // A canvas unit is 1.5 mm per metre of distance, so the ring keeps one size on screen. FitRadius is the
    // largest ring a page can make, outer row included, so one opened near the window's edge comes in far enough
    // to show whole.
    private const float DesktopScalePerMetre = 1.5f;
    private const float FitRadius = RingMax + PlateSize * 0.5f + OuterGap + OuterThickness;

    // The eye a desktop ring sits in front of: the camera while it flies on its own (third person, the free
    // camera, a view taken by something worn, or on its way back to the head), the head otherwise.
    private bool TryDesktopView(out float3 position, out floatQ rotation)
    {
        var input = Engine.Current?.InputInterface;
        bool external = input != null && !input.VR_Active && input.DesktopCameraPoseValid
            && (input.DesktopExternalCameraAim || UserInputState.FocusedFreeCamActive);
        if (external)
        {
            position = input!.DesktopCameraPosition;
            rotation = input.DesktopCameraRotation;
            return true;
        }
        var head = Slot.ActiveUserRoot?.HeadSlot;
        position = head?.GlobalPosition ?? float3.Zero;
        rotation = head?.GlobalRotation ?? floatQ.Identity;
        return head != null && !head.IsDestroyed;
    }

    // Which way from the eye the ring opens, in the view's space: through the loose arrow when there is one,
    // straight ahead on the crosshair otherwise, then pulled in until the largest ring fits the window.
    private static float3 OpenDirection(Input.InputInterface? input, in floatQ viewRotation)
    {
        float3 look = float3.Backward;
        if (input == null)
            return look;
        if (input.CursorFree && input.DesktopCursorRayValid)
        {
            float3 through = viewRotation.Inverse * input.DesktopCursorRayDirection;
            if (through.z < -0.05f)
                look = through;
        }
        float tanY = MathF.Tan(input.DesktopCameraFovY * 0.5f * (MathF.PI / 180f));
        float tanX = tanY * input.DesktopViewportAspect;
        // The ring's reach as a slope off the line of sight, the same at any distance since it grows with it.
        float reach = FitRadius * CanvasScale * DesktopScalePerMetre;
        float limitX = MathF.Max(tanX - reach, 0f);
        float limitY = MathF.Max(tanY - reach, 0f);
        float x = System.Math.Clamp(look.x / -look.z, -limitX, limitX);
        float y = System.Math.Clamp(look.y / -look.z, -limitY, limitY);
        return new float3(x, y, -1f).Normalized;
    }

    // Puts the ring at its offset from wherever the eye is now, square to the lens. Written only on a change,
    // so a ring riding along with a walking body in first person sends nothing.
    private void PinToView()
    {
        if (!_viewPinned || _menuRoot == null || _menuRoot.IsDestroyed)
            return;
        if (Engine.Current?.InputInterface?.VR_Active == true)
        {
            _viewPinned = false;
            return;
        }
        if (!TryDesktopView(out var viewPosition, out var viewRotation))
            return;

        var parent = _menuRoot.Parent;
        float3 position = viewPosition + viewRotation * _viewOffset;
        float3 localPosition = parent != null ? parent.GlobalPointToLocal(position) : position;
        floatQ localRotation = parent != null ? parent.GlobalRotationToLocal(viewRotation) : viewRotation;
        float parentScale = MathF.Max(Slot.GlobalScale.x, 1e-4f);
        // Screen size follows depth, so an off-centre ring fits the same edge margin. -xlinka
        float3 localScale = float3.One * (DesktopScalePerMetre * MathF.Max(-_viewOffset.z, 0.001f) / parentScale);

        if ((localPosition - _menuRoot.LocalPosition.Value).LengthSquared > 1e-10f)
            _menuRoot.LocalPosition.Value = localPosition;
        float dot = MathF.Abs(floatQ.Dot(localRotation, _menuRoot.LocalRotation.Value));
        if (1f - dot > 1e-9f)
            _menuRoot.LocalRotation.Value = localRotation;
        if ((localScale - _menuRoot.LocalScale.Value).LengthSquared > 1e-10f)
            _menuRoot.LocalScale.Value = localScale;
    }

    private void EnsureVisualRoot()
    {
        if (_menuRoot != null && !_menuRoot.IsDestroyed)
            return;

        // The menu is a child of the user root, so it already moves + turns rigidly with you. Re-facing
        // it toward the head every frame (in world space) lagged the body translation by a frame and
        // made it jitter while moving. We face it once on open (in PositionMenu) and animate open/close
        // by scale only. Never saved: it is a person's menu, not part of the world.
        using (World.EnterLocalUserAllocation())
        {
            _menuRoot = Slot.AddSlot("ContextMenu");
            _menuRoot.Persistent.Value = false;
            _font = _menuRoot.AttachComponent<FontProvider>();
            _font.URL.Value = DashTheme.FontSemibold;
        }
        _icons.Clear();
    }

    private sealed class BuiltItem
    {
        public ContextMenuItem Item = null!;
        public bool Outer;
        public float2 Direction;
        public Button? Button;
        public RoundedPanel? Plate;
        public RectTransform? PlateRect;
        public ArcSegment? Arc;
        public Text? Label;
        public Image? Icon;
        public bool HasIcon;
        public RoundedPanel? Status;
        public RoundedPanel? ValueTrack;
        public RoundedPanel? ValueFill;
        public RectTransform? ValueFillRect;
        public Image? Branch;
        public color Accent;
        public bool Tinted;
        public bool Hovered;
        public float Hover;
        public bool Rich;
        public string Content = "";
    }

    private sealed class BuiltCenter
    {
        public ArcSegment Disc = null!;
        public ArcButton Button = null!;
        public ArcSegment Rail = null!;
        public ArcSegment Focus = null!;
        public Text Title = null!;
        public Text Label = null!;
        public Text Detail = null!;
        public float FocusAngle;
        public float FocusShown;
        public string TitleText = "";
        public string WindowText = "";
        public string PreviousText = "";
        public string NextText = "";
    }

    private sealed class BuiltDial
    {
        public ContextMenuItem Item = null!;
        public readonly ContextMenuDial.Follower Pointer = new();
        public readonly ContextMenuDial.Follower Stick = new();
        public ArcButton Face = null!;
        public ArcSegment Track = null!;
        public ArcSegment Fill = null!;
        public readonly List<(ArcSegment Arc, float At)> Ticks = new();
        public Text Low = null!;
        public Text High = null!;
        public ArcSegment? Default;
        public RectTransform HandleRect = null!;
        public ArcSegment Handle = null!;
        public ArcSegment Dot = null!;
        public ArcSegment Hub = null!;
        public ArcButton HubButton = null!;
        public ArcSegment Focus = null!;
        public Text Title = null!;
        public Text Value = null!;
        public Text Detail = null!;
        public float Shown;
        public float Grip;
        public bool Aimed;
        public bool Turning;
    }

    private readonly struct Placed
    {
        public readonly ContextMenuItem Item;
        public readonly bool Outer;
        public readonly float Angle;
        public readonly float Arc;

        public Placed(ContextMenuItem item, bool outer, float angle, float arc)
        {
            Item = item;
            Outer = outer;
            Angle = angle;
            Arc = arc;
        }
    }

    private sealed class Arrangement
    {
        public readonly List<Placed> Items = new();
        public float Ring = RingMin;
        public float OuterInner;
        public float OuterOuter;
        public float Edge = EdgeMin;
        public int Window;
        public int Windows = 1;
    }

    // Where everything on a page goes. History sits on the outer row of the first page; the rest are plates
    // clockwise from twelve o'clock, as many as the ring takes, turned half a step when history needs the
    // top, so the whole menu stands symmetric about its upright. A page with more than that shows them a
    // window at a time, split evenly, with page turns on the outer row; the items themselves are the page's
    // own, so their presses, refreshes and the way back are untouched. Outer items take the gap between
    // plates nearest where they belong (history across the top, page turns across the bottom), sharing a gap
    // evenly, so no two actions ever share a direction. -xlinka
    private Arrangement Arrange(ContextMenuPage page, int window)
    {
        var result = new Arrangement();
        var history = new List<ContextMenuItem>();
        var primary = new List<ContextMenuItem>();
        foreach (var item in page.Items)
        {
            if (!HasPageHistory && item.Group == ContextMenuGroup.History && history.Count < 2)
                history.Add(item);
            else
                primary.Add(item);
        }
        if (primary.Count == 0)
        {
            primary.AddRange(history);
            history.Clear();
        }

        int windows = System.Math.Max(1, (primary.Count + PrimaryLimit - 1) / PrimaryLimit);
        int per = (primary.Count + windows - 1) / windows;
        window = System.Math.Clamp(window, 0, windows - 1);
        int first = window * per;
        int count = System.Math.Min(per, primary.Count - first);
        result.Window = window;
        result.Windows = windows;

        float ring = RingFor(count);
        float step = 360f / System.Math.Max(count, 1);
        float start = history.Count > 0 ? 270f + step * 0.5f : 270f;
        result.Ring = ring;
        for (int i = 0; i < count; i++)
            result.Items.Add(new Placed(primary[first + i], false, Wrap(start + step * i), step));

        // Preferences sit a few degrees either side of straight up and straight down: well inside the gap
        // the plates leave there, on the side each belongs.
        var outer = new List<(ContextMenuItem Item, float Prefer)>();
        for (int i = 0; i < history.Count; i++)
            outer.Add((history[i], history.Count == 1 ? 270f : 262f + 16f * i));
        if (windows > 1)
        {
            outer.Add((_previousWindow, 98f));
            outer.Add((_nextWindow, 82f));
        }

        float reach = ring + PlateReach;
        if (outer.Count > 0)
        {
            result.OuterInner = ring + PlateReach + OuterGap;
            result.OuterOuter = result.OuterInner + OuterThickness;
            float middle = (result.OuterInner + result.OuterOuter) * 0.5f;
            float length = OuterLength / middle * (180f / MathF.PI);
            var angles = OuterAngles(outer, count, start, length + 4f);
            float closest = 360f;
            for (int i = 0; i < angles.Length; i++)
            {
                for (int j = i + 1; j < angles.Length; j++)
                {
                    float apart = MathF.Abs(angles[i] - angles[j]);
                    closest = MathF.Min(closest, MathF.Min(apart, 360f - apart));
                }
            }
            float arc = MathF.Min(length, closest - 3f);
            for (int i = 0; i < outer.Count; i++)
                result.Items.Add(new Placed(outer[i].Item, true, angles[i], arc));
            reach = result.OuterOuter;
        }
        result.Edge = MathF.Max(EdgeMin, reach + EdgeMargin);
        return result;
    }

    // Items sharing a gap sit side by side about its middle, a pill's length apart, or spread evenly across
    // it when it is too narrow for that.
    private static float[] OuterAngles(List<(ContextMenuItem Item, float Prefer)> outer, int plates, float start, float pitch)
    {
        var angles = new float[outer.Count];
        if (plates <= 1)
        {
            // A lone plate sits at the bottom under history, and every preferred direction is clear of it.
            for (int i = 0; i < outer.Count; i++)
                angles[i] = outer[i].Prefer;
            return angles;
        }
        float step = 360f / plates;
        var gaps = new Dictionary<int, List<int>>();
        for (int i = 0; i < outer.Count; i++)
        {
            float from = Wrap(outer[i].Prefer - start);
            int gap = System.Math.Min((int)MathF.Floor(from / step), plates - 1);
            if (!gaps.TryGetValue(gap, out var members))
                gaps[gap] = members = new List<int>();
            members.Add(i);
        }
        foreach (var (gap, members) in gaps)
        {
            members.Sort((a, b) => Wrap(outer[a].Prefer - start).CompareTo(Wrap(outer[b].Prefer - start)));
            float spacing = MathF.Min(step / (members.Count + 1), pitch);
            float middle = start + step * (gap + 0.5f);
            for (int j = 0; j < members.Count; j++)
                angles[members[j]] = Wrap(middle + spacing * (j - (members.Count - 1) * 0.5f));
        }
        return angles;
    }

    private static float Wrap(float degrees) => degrees - MathF.Floor(degrees / 360f) * 360f;

    // The smallest ring that keeps this many plates clear of the hub and of each other.
    private static float RingFor(int plates)
    {
        if (plates <= 1)
            return RingMin;
        float apart = (2f * PlateReach + PlateGap) / (2f * MathF.Sin(MathF.PI / plates));
        return MathF.Max(RingMin, apart);
    }

    private static int LargestRing()
    {
        int largest = 1;
        for (int plates = 2; plates <= 24; plates++)
        {
            if (RingFor(plates) <= RingMax)
                largest = plates;
        }
        return largest;
    }

    private void RebuildVisual(bool pageChange = false)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        using (World.EnterLocalUserAllocation())
            BuildVisual(pageChange);
        // An open times itself, sources and all.
        if (pageChange)
            ReportSlowBuild("Built", CurrentPage, started);
    }

    private void BuildVisual(bool pageChange)
    {
        EnsureVisualRoot();
        _builtItems.Clear();
        _center = null;
        _dial = null;
        _flickItem = null;
        _closing = false;
        ClearCanvases();
        _canvasSlot = _menuRoot.AddSlot("Canvas");
        _openLerp = 0f;
        _openRate = pageChange ? AnimationRate * 1.5f : AnimationRate;
        _canvasSlot.LocalScale.Value = float3.One * (CanvasScale * OpeningScale);

        var page = CurrentPage;
        var dialed = page?.Dial;
        Arrangement? layout = null;
        if (page != null && dialed == null)
        {
            foreach (var item in page.Items)
                item.Refresh?.Invoke(item);
            layout = Arrange(page, _window);
            _window = layout.Window;
            _windowCount = layout.Windows;
        }
        else
        {
            _window = 0;
            _windowCount = 1;
        }
        _edgeRadius = dialed != null ? MathF.Max(EdgeMin, DialReach + EdgeMargin) : layout?.Edge ?? EdgeMin;
        if (dialed != null)
            _canvasSlot.LocalPosition.Value = _dialCentre;

        // Canvas root size comes from a centred RectTransform on the canvas slot: everything is laid out
        // around its middle, and it reaches as far as the pointer may go before the menu closes.
        var rootRect = _canvasSlot.AttachComponent<RectTransform>();
        rootRect.OffsetMin.Value = new float2(-_edgeRadius, -_edgeRadius);
        rootRect.OffsetMax.Value = new float2(_edgeRadius, _edgeRadius);
        var canvas = _canvasSlot.AttachComponent<Canvas>();
        // The owner's pointers alone reach it, and the overlay below holds on the owner's machine only.
        canvas.Exclusive.Target = World.LocalUser;
        // The menu is overlay UI: it renders over everything. SortingOrder wins the transparent sort;
        // Overlay makes every canvas material skip the DEPTH test too - without it, world geometry still
        // clips into the menu (the "objects showing through the ring" render bug). For anyone else the
        // ring stands in the world, depth tested. -xlinka
        canvas.SortingOrder.Value = 10000;
        canvas.Overlay.Value = true;
        // Both faces: someone facing the owner sees the ring from behind, plates and icons, its words
        // reading the owner's side only.
        canvas.Culling.Value = Lumora.Core.Assets.Culling.None;
        // Every word sits on the menu's own dark ground, so text needs no outline.
        canvas.TextOutlineThickness.Value = 0f;
        canvas.TextFaceDilate.Value = 0f;
        // Its parts now, not at its start a frame later, so they reach everyone with it.
        canvas.EnsureRoot();
        if (dialed != null)
        {
            BuildDial(dialed);
            LayoutVisuals(0f, 0f);
            return;
        }
        if (layout == null)
            return;

        BuildCenter(layout.Ring);
        foreach (var placed in layout.Items)
        {
            var item = placed.Item;
            item.AngleStart = placed.Angle - placed.Arc * 0.5f;
            item.ArcLength = placed.Arc;
            if (placed.Outer)
            {
                item.RadiusStart = layout.OuterInner;
                item.Thickness = OuterThickness;
                BuildOuter(item, placed.Angle, placed.Arc, layout.OuterInner, layout.OuterOuter);
            }
            else
            {
                item.RadiusStart = layout.Ring - PlateSize * 0.5f;
                item.Thickness = PlateSize;
                BuildPlate(item, placed.Angle, layout.Ring);
            }
        }
        LayoutVisuals(0f, 0f);
    }

    private ArcSegment DecorativeArc(Slot parent, string name, float inner, float outer, float length = 360f)
    {
        var slot = parent.AddSlot(name);
        Fill(slot.AttachComponent<RectTransform>());
        var arc = slot.AttachComponent<ArcSegment>();
        arc.AngleStart.Value = 0f;
        arc.ArcLength.Value = length;
        arc.InnerRadius.Value = inner;
        arc.OuterRadius.Value = outer;
        arc.OutlineThickness.Value = 0f;
        arc.EdgeSoftness.Value = 0.5f;
        arc.CornerRadius.Value = length < 360f ? (outer - inner) * 0.5f : 0f;
        arc.Tint.Value = color.Transparent;
        return arc;
    }

    private void BuildCenter(float ring)
    {
        var center = _canvasSlot.AddSlot("Center");
        Fill(center.AttachComponent<RectTransform>());
        var disc = center.AttachComponent<ArcSegment>();
        disc.AngleStart.Value = 0f;
        disc.ArcLength.Value = 360f;
        disc.InnerRadius.Value = 0f;
        disc.OuterRadius.Value = HubRadius;
        disc.OutlineThickness.Value = 1f;
        disc.Tint.Value = color.Transparent;
        disc.OutlineColor.Value = color.Transparent;
        var button = center.AttachComponent<ArcButton>();
        button.Clicked += (_, _) => PopPage();
        _center = new BuiltCenter
        {
            Disc = disc,
            Button = button,
            // A hairline through the middle of the plates ties them into one ring without filling the gaps.
            Rail = DecorativeArc(center, "Rail", ring - 0.6f, ring + 0.6f),
            Focus = DecorativeArc(center, "Focus", HubRadius + 2.5f, HubRadius + 5f, FocusArc),
            Title = AddMenuText(center, "Page", new float2(0f, 25f), new float2(66f, 11f), 8.5f, 7f),
            Label = AddMenuText(center, "Action", new float2(0f, 1f), new float2(82f, 36f), 15f, 10.5f),
            Detail = AddMenuText(center, "State", new float2(0f, -24f), new float2(66f, 12f), 9.5f, 8f),
        };
        _center.Title.RichText.Value = false;
        _center.Detail.RichText.Value = false;
        // Worked out once per layout, not per frame.
        string title = CurrentPage?.Title is { Length: > 0 } named ? (named == "Menu" ? "LUMORA" : named.ToUpperInvariant()) : "";
        _center.TitleText = _windowCount > 1 ? $"{title}  {_window + 1}/{_windowCount}" : title;
        if (_windowCount > 1)
        {
            _center.WindowText = $"Page {_window + 1} of {_windowCount}";
            _center.PreviousText = $"Page {(_window + _windowCount - 1) % _windowCount + 1} of {_windowCount}";
            _center.NextText = $"Page {(_window + 1) % _windowCount + 1} of {_windowCount}";
        }
    }

    private static bool Actionable(ContextMenuItem item)
        => item.IsEnabled && (item.OnPressed != null || item.SubPage != null || item.OnButton != null || item.IsValue);

    private static bool LeadsOn(ContextMenuItem item) => item.SubPage != null || item.OpensPage;

    private static float2 Direction(float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        return new float2(MathF.Cos(radians), -MathF.Sin(radians));
    }

    private void BuildPlate(ContextMenuItem item, float angle, float ring)
    {
        var slot = _canvasSlot.AddSlot(item.Label);
        var direction = Direction(angle);
        CenterRect(slot.AttachComponent<RectTransform>(), direction * ring, new float2(PlateHitSize, PlateHitSize));
        RoundedButton? button = null;
        if (Actionable(item))
        {
            button = slot.AttachComponent<RoundedButton>();
            button.CornerRadius.Value = PlateHitCorner;
            button.Clicked += (pressed, context) => Select(item, pressed, context);
        }

        var plateSlot = slot.AddSlot("Plate");
        var plateRect = plateSlot.AttachComponent<RectTransform>();
        CenterRect(plateRect, float2.Zero, new float2(PlateSize, PlateSize));
        var plate = plateSlot.AttachComponent<RoundedPanel>();
        plate.CornerRadius.Value = PlateCorner;
        plate.Color.Value = color.Transparent;
        plate.OutlineColor.Value = color.Transparent;
        plate.OutlineThickness.Value = 1f;

        var (rich, content) = LabelText(item);
        var icon = AddIcon(plateSlot, item, new float2(0f, 1f), IconSize);
        Text? label = null;
        if (icon == null)
        {
            bool marked = item.IsToggle || item.IsValue || LeadsOn(item);
            label = AddMenuText(plateSlot, "Label", float2.Zero, marked ? new float2(42f, 30f) : new float2(44f, 40f), 11f, 8.5f);
            label.RichText.Value = rich;
            label.Content.Value = content;
        }

        // A toggle wears a short bar along its bottom edge, lit while on; one that leads to another page has
        // a small chevron in its upper corner.
        RoundedPanel? status = null;
        if (item.IsToggle)
        {
            var statusSlot = plateSlot.AddSlot("State");
            CenterRect(statusSlot.AttachComponent<RectTransform>(), new float2(0f, -19.5f), new float2(14f, 3f));
            status = statusSlot.AttachComponent<RoundedPanel>();
            status.CornerRadius.Value = 1.5f;
            status.OutlineThickness.Value = 0f;
            status.Color.Value = color.Transparent;
        }
        // A value has a longer track there instead, filled as far as the value goes.
        RoundedPanel? valueTrack = null;
        RoundedPanel? valueFill = null;
        RectTransform? valueFillRect = null;
        if (item.IsValue)
        {
            var trackSlot = plateSlot.AddSlot("Value");
            CenterRect(trackSlot.AttachComponent<RectTransform>(), new float2(0f, -19.5f), new float2(ValueBar, 3f));
            valueTrack = trackSlot.AttachComponent<RoundedPanel>();
            valueTrack.CornerRadius.Value = 1.5f;
            valueTrack.OutlineThickness.Value = 0f;
            valueTrack.Color.Value = color.Transparent;
            var fillSlot = plateSlot.AddSlot("Filled");
            valueFillRect = fillSlot.AttachComponent<RectTransform>();
            CenterRect(valueFillRect, new float2((3f - ValueBar) * 0.5f, -19.5f), new float2(3f, 3f));
            valueFill = fillSlot.AttachComponent<RoundedPanel>();
            valueFill.CornerRadius.Value = 1.5f;
            valueFill.OutlineThickness.Value = 0f;
            valueFill.Color.Value = color.Transparent;
        }
        Image? branch = null;
        if (LeadsOn(item))
        {
            var branchSlot = plateSlot.AddSlot("Opens");
            CenterRect(branchSlot.AttachComponent<RectTransform>(), new float2(15.5f, 15.5f), new float2(8f, 8f));
            branch = branchSlot.AttachComponent<Image>();
            branch.Texture.Target = IconProvider("forward");
            branch.InteractionTarget.Value = false;
            branch.Tint.Value = color.Transparent;
        }

        _builtItems.Add(new BuiltItem
        {
            Item = item, Direction = direction, Button = button, Plate = plate, PlateRect = plateRect,
            Label = label, Icon = icon, HasIcon = icon != null, Status = status, Branch = branch,
            ValueTrack = valueTrack, ValueFill = valueFill, ValueFillRect = valueFillRect,
            Accent = ItemAccent(item), Tinted = item.OutlineColor != null, Rich = rich, Content = content,
        });
    }

    // An outer item is a thin curved pill on its own band, answering exactly inside its shape.
    private void BuildOuter(ContextMenuItem item, float angle, float arcLength, float inner, float outer)
    {
        var slot = _canvasSlot.AddSlot(item.Label);
        Fill(slot.AttachComponent<RectTransform>());
        var arc = slot.AttachComponent<ArcSegment>();
        arc.AngleStart.Value = angle - arcLength * 0.5f;
        arc.ArcLength.Value = arcLength;
        arc.InnerRadius.Value = inner;
        arc.OuterRadius.Value = outer;
        arc.CornerRadius.Value = (outer - inner) * 0.5f - 0.5f;
        arc.OutlineThickness.Value = 1f;
        arc.Tint.Value = color.Transparent;
        arc.OutlineColor.Value = color.Transparent;
        ArcButton? button = null;
        if (Actionable(item))
        {
            button = slot.AttachComponent<ArcButton>();
            button.Clicked += (pressed, context) => Select(item, pressed, context);
        }
        var direction = Direction(angle);
        var middle = direction * ((inner + outer) * 0.5f);
        var (rich, content) = LabelText(item);
        var icon = AddIcon(slot, item, middle, 12f);
        Text? label = null;
        if (icon == null)
        {
            label = AddMenuText(slot, "Label", middle, new float2(OuterLength - 10f, 14f), 8.5f, 7f);
            label.RichText.Value = rich;
            label.Content.Value = content;
        }
        _builtItems.Add(new BuiltItem
        {
            Item = item, Outer = true, Direction = direction, Button = button, Arc = arc, Label = label,
            Icon = icon, HasIcon = icon != null, Accent = ItemAccent(item), Tinted = item.OutlineColor != null,
            Rich = rich, Content = content,
        });
    }

    // The dial: a hub naming the value and showing it, a track round it from the lower left over the top to
    // the lower right with the value filled in and a handle at the value, a tick every tenth outside it, and
    // nought and the whole marked in the gap at the bottom. The face past the hub answers a press: the value
    // is already set as it turned, so a press hands it back and goes back a page, as the hub does. -xlinka
    private void BuildDial(ContextMenuItem item)
    {
        var dial = new BuiltDial { Item = item };
        float value = item.ReadValue != null ? ContextMenuDial.Clamp01(item.ReadValue()) : 0f;
        dial.Shown = value;

        var face = DecorativeArc(_canvasSlot, "Face", HubRadius, DialReach);
        dial.Face = face.Slot.AttachComponent<ArcButton>();
        dial.Face.Clicked += (_, _) => PopPage();

        // The hub goes first, as on the ring: its words then stay over its disc whatever the surfaces
        // around it are merged into.
        var hub = _canvasSlot.AddSlot("Center");
        Fill(hub.AttachComponent<RectTransform>());
        dial.Hub = hub.AttachComponent<ArcSegment>();
        dial.Hub.AngleStart.Value = 0f;
        dial.Hub.ArcLength.Value = 360f;
        dial.Hub.InnerRadius.Value = 0f;
        dial.Hub.OuterRadius.Value = HubRadius;
        dial.Hub.OutlineThickness.Value = 1f;
        dial.Hub.Tint.Value = color.Transparent;
        dial.Hub.OutlineColor.Value = color.Transparent;
        dial.HubButton = hub.AttachComponent<ArcButton>();
        dial.HubButton.Clicked += (_, _) => PopPage();
        dial.Focus = DecorativeArc(hub, "Focus", HubRadius + 2.5f, HubRadius + 5f, FocusArc);
        dial.Title = AddMenuText(hub, "Page", new float2(0f, 24f), new float2(70f, 11f), 8.5f, 6.5f);
        dial.Title.RichText.Value = false;
        dial.Title.Content.Value = ContextMenuEntry.Plain(item.Label).ToUpperInvariant();
        dial.Value = AddMenuText(hub, "Value", new float2(0f, 1f), new float2(84f, 30f), 24f, 14f);
        dial.Value.RichText.Value = false;
        dial.Detail = AddMenuText(hub, "State", new float2(0f, -24f), new float2(70f, 12f), 9.5f, 8f);
        dial.Detail.RichText.Value = false;

        dial.Track = DecorativeArc(_canvasSlot, "Track", DialTrackInner, DialTrackOuter, ContextMenuDial.Sweep);
        dial.Track.AngleStart.Value = ContextMenuDial.Start;
        dial.Track.OutlineThickness.Value = 1f;
        dial.Track.OutlineColor.Value = color.Transparent;
        dial.Fill = DecorativeArc(_canvasSlot, "Fill", DialTrackInner + DialFillInset, DialTrackOuter - DialFillInset, ContextMenuDial.Sweep);
        dial.Fill.AngleStart.Value = ContextMenuDial.Start;
        dial.Fill.ArcLength.Value = 0f;

        for (int i = 0; i <= 10; i++)
        {
            bool major = i % 5 == 0;
            float outer = major ? DialTickOuter + 3f : DialTickOuter;
            float width = (major ? 1.6f : 1.1f) / ((DialTickInner + outer) * 0.5f) * (180f / MathF.PI);
            var tick = DecorativeArc(_canvasSlot, "Tick", DialTickInner, outer, width);
            tick.AngleStart.Value = ContextMenuDial.Start + ContextMenuDial.Sweep * (i / 10f) - width * 0.5f;
            tick.CornerRadius.Value = 0f;
            dial.Ticks.Add((tick, i / 10f));
        }

        // The ends are named inside the gap, below the track, clear of the handle sitting on either end.
        dial.Low = AddMenuText(_canvasSlot, "Nought", Direction(ContextMenuDial.Start - 17f) * DialTickInner, new float2(22f, 12f), 8.5f, 7f);
        dial.Low.RichText.Value = false;
        dial.Low.Content.Value = "0";
        dial.High = AddMenuText(_canvasSlot, "Whole", Direction(ContextMenuDial.Start + ContextMenuDial.Sweep + 17f) * DialTickInner, new float2(22f, 12f), 8.5f, 7f);
        dial.High.RichText.Value = false;
        dial.High.Content.Value = "100";

        if (item.DefaultValue is float fallback)
        {
            var mark = _canvasSlot.AddSlot("Default");
            CenterRect(mark.AttachComponent<RectTransform>(), Direction(ContextMenuDial.AngleOf(fallback)) * (DialTickOuter + 7f), new float2(8f, 8f));
            dial.Default = mark.AttachComponent<ArcSegment>();
            dial.Default.AngleStart.Value = 0f;
            dial.Default.ArcLength.Value = 360f;
            dial.Default.InnerRadius.Value = 0f;
            dial.Default.OuterRadius.Value = 2.4f;
            dial.Default.OutlineThickness.Value = 0f;
            dial.Default.EdgeSoftness.Value = 0.5f;
            dial.Default.Tint.Value = color.Transparent;
        }

        var handle = _canvasSlot.AddSlot("Handle");
        dial.HandleRect = handle.AttachComponent<RectTransform>();
        CenterRect(dial.HandleRect, Direction(ContextMenuDial.AngleOf(value)) * ((DialTrackInner + DialTrackOuter) * 0.5f), new float2(32f, 32f));
        dial.Handle = handle.AttachComponent<ArcSegment>();
        dial.Handle.AngleStart.Value = 0f;
        dial.Handle.ArcLength.Value = 360f;
        dial.Handle.InnerRadius.Value = 0f;
        dial.Handle.OuterRadius.Value = DialHandle;
        dial.Handle.OutlineThickness.Value = 2f;
        dial.Handle.EdgeSoftness.Value = 0.6f;
        dial.Handle.Tint.Value = color.Transparent;
        dial.Handle.OutlineColor.Value = color.Transparent;
        dial.Dot = DecorativeArc(handle, "Dot", 0f, 4f);

        _dial = dial;
    }

    // The fill and handle ease after the value, near enough behind a turn to read as the turn itself, and
    // the fill sweeps out from nought while the dial opens. The handle swells while something is turning
    // it, and the arc on the hub's rim points at it.
    private void LayoutDial(float delta, float shown)
    {
        var dial = _dial;
        if (dial == null || dial.Hub.IsDestroyed)
            return;
        var item = dial.Item;
        float value = item.ReadValue != null ? ContextMenuDial.Clamp01(item.ReadValue()) : dial.Shown;
        float ease = 1f - MathF.Exp(-delta * 24f);
        dial.Shown = MathF.Abs(value - dial.Shown) < 0.001f ? value : dial.Shown + (value - dial.Shown) * ease;
        float drawn = dial.Shown * (_closing ? 1f : Smoother(_openLerp));
        bool hubHover = !_closing && !dial.HubButton.IsDestroyed && dial.HubButton.IsHovering.Value;
        bool hubPress = hubHover && dial.HubButton.IsPressed.Value;
        dial.Grip = MoveTowards(dial.Grip, !_closing && (dial.Aimed || dial.Turning) ? 1f : 0f, delta * AnimationRate * 1.5f);
        float grip = Smoother(dial.Grip);
        float angle = ContextMenuDial.Start + ContextMenuDial.Sweep * drawn;

        dial.Hub.Tint.Value = Faded(hubPress ? PlatePressed : hubHover ? PlateHover : HubRest, shown);
        dial.Hub.OutlineColor.Value = Faded(hubHover ? DashTheme.Accent : Hairline, shown);
        dial.Title.Color.Value = Faded(DashTheme.TextMuted, shown);
        dial.Value.Color.Value = Faded(DashTheme.Text, shown);
        dial.Value.Content.Value = ContextMenuDial.Percent(value);
        dial.Detail.Content.Value = hubHover ? "Back"
            : item.DefaultValue is float fallback && MathF.Abs(fallback - value) < 0.005f ? "Default" : "Turn to set";
        dial.Detail.Color.Value = Faded(hubHover ? DashTheme.Accent : DashTheme.TextDim, shown);

        dial.Track.Tint.Value = Faded(TrackGround, shown);
        dial.Track.OutlineColor.Value = Faded(color.Lerp(Hairline, DashTheme.AccentSoft, grip), shown);
        dial.Fill.ArcLength.Value = ContextMenuDial.Sweep * drawn;
        dial.Fill.Tint.Value = Faded(color.Lerp(DashTheme.AccentPressed, DashTheme.AccentHover, grip), shown);
        var unlit = Faded(DashTheme.TextMuted, 0.6f);
        foreach (var (tick, at) in dial.Ticks)
            tick.Tint.Value = Faded(at <= drawn + 0.001f && drawn > 0.001f ? DashTheme.Accent : unlit, shown);
        dial.Low.Color.Value = Faded(drawn <= 0.001f ? DashTheme.Text : DashTheme.TextMuted, shown);
        dial.High.Color.Value = Faded(drawn >= 0.999f ? DashTheme.Text : DashTheme.TextMuted, shown);
        if (dial.Default != null)
            dial.Default.Tint.Value = Faded(DashTheme.TextDim, shown);

        float middle = (DialTrackInner + DialTrackOuter) * 0.5f;
        CenterRect(dial.HandleRect, Direction(angle) * middle, new float2(32f, 32f));
        dial.Handle.OuterRadius.Value = DialHandle + grip * 2f;
        dial.Handle.Tint.Value = Faded(DashTheme.Text, shown);
        dial.Handle.OutlineColor.Value = Faded(color.Lerp(DashTheme.AccentPressed, DashTheme.Accent, grip), shown);
        dial.Dot.OuterRadius.Value = 4f + grip * 1.5f;
        dial.Dot.Tint.Value = Faded(DashTheme.Accent, shown);
        dial.Focus.AngleStart.Value = angle - FocusArc * 0.5f;
        dial.Focus.Tint.Value = Faded(DashTheme.Accent, shown * (0.35f + 0.65f * grip));
    }

    // The item's own picture, its named icon, or one picked by what its label says.
    private Image? AddIcon(Slot parent, ContextMenuItem item, float2 at, float size)
    {
        var picture = SharedPicture(item);
        string? name = picture == null ? IconName(item) : null;
        if (picture == null && name == null)
            return null;
        var slot = parent.AddSlot("Icon");
        CenterRect(slot.AttachComponent<RectTransform>(), at, new float2(size, size));
        var icon = slot.AttachComponent<Image>();
        icon.Texture.Target = picture ?? IconProvider(name!);
        icon.PreserveAspect.Value = true;
        icon.InteractionTarget.Value = false;
        icon.Tint.Value = color.Transparent;
        return icon;
    }

    private static string? IconName(ContextMenuItem item)
    {
        if (!string.IsNullOrEmpty(item.IconPath))
            return item.IconPath;
        return item.SafeMarkup ? null : IconFor(item.Label);
    }

    private bool HasIcon(ContextMenuItem item) => SharedPicture(item) != null || IconName(item) != null;

    // An item's own picture, when every machine can see it: the ring is out in the world, and a picture
    // from this machine alone or from another world could not be pointed at from it.
    private Lumora.Core.Assets.IAssetProvider<Lumora.Core.Assets.TextureAsset>? SharedPicture(ContextMenuItem item)
        => item.IconAsset is IWorldElement element && (element.ReferenceID.IsLocalID || element.World != World) ? null : item.IconAsset;

    private Text AddMenuText(Slot parent, string name, float2 at, float2 size, float fontSize, float smallest)
    {
        var slot = parent.AddSlot(name);
        CenterRect(slot.AttachComponent<RectTransform>(), at, size);
        var text = slot.AttachComponent<Text>();
        text.Font.Target = _font;
        text.Size.Value = fontSize;
        text.Color.Value = color.Transparent;
        text.LineSpacing.Value = 0.9f;
        text.WordWrap.Value = true;
        text.AutoSizeHorizontal.Value = true;
        text.AutoSizeVertical.Value = true;
        text.AutoSizeMin.Value = smallest;
        text.ClipToRect.Value = true;
        text.InteractionTarget.Value = false;
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        return text;
    }

    private static void CenterRect(RectTransform rect, float2 at, float2 size)
    {
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = at - size * 0.5f;
        rect.OffsetMax.Value = at + size * 0.5f;
    }

    // Exactly the canvas. A fresh rect carries 50-unit offsets; left on a stretch rect they grow it past
    // the canvas and anything anchored by fraction drifts out from where it belongs. -xlinka
    private static void Fill(RectTransform rect)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }

    // A second line is the item's detail, set smaller. Text from a world never gets markup read into it,
    // unless it went through the entry's sanitizer, which keeps only its colours and emphasis.
    private static (bool Rich, string Content) LabelText(ContextMenuItem item)
    {
        bool plain = !item.SafeMarkup && item.Label.Contains('<');
        return (!plain, plain ? item.Label : LabelMarkup(item.Label));
    }

    private static string LabelMarkup(string label)
    {
        int newline = label.IndexOf('\n');
        if (newline < 0)
            return label;
        return label[..newline] + "\n<size=75%>" + label[(newline + 1)..] + "</size>";
    }

    // One provider per icon while the menu is up, shared by every item that shows it, so a repeated icon
    // is one texture and one draw.
    private Lumora.Core.Assets.ImageProvider IconProvider(string icon)
    {
        if (_icons.TryGetValue(icon, out var cached) && !cached.IsDestroyed)
            return cached;
        var provider = _menuRoot.AttachComponent<Lumora.Core.Assets.ImageProvider>();
        provider.URL.Value = icon.Contains("://")
            ? new Uri(icon)
            : new Uri("res://Assets/Textures/Icons/" + icon + ".svg");
        _icons[icon] = provider;
        return provider;
    }

    // An icon for the common items by what their label says, for sources that name none.
    private static string? IconFor(string label)
    {
        var text = label.ToLowerInvariant();
        int newline = text.IndexOf('\n');
        if (newline >= 0)
            text = text[..newline];
        if (text.StartsWith("undo")) return "undo";
        if (text.StartsWith("redo")) return "redo";
        if (text.Contains("locomotion")) return "walk";
        if (text.Contains("laser")) return "laser";
        if (text.Contains("inspector")) return "inspect";
        if (text.Contains("rotate")) return "rotate";
        if (text.Contains("scal")) return "scale";
        if (text.Contains("gizmo") || text.Contains("translate")) return "move";
        if (text.StartsWith("create") || text == "new" || text.StartsWith("new ")) return "plus";
        if (text.Contains("duplicate") || text.StartsWith("copy")) return "copy";
        if (text.Contains("destroy") || text.StartsWith("delete")) return "trash";
        if (text.StartsWith("unequip") || text.StartsWith("eject")) return "eject";
        if (text.StartsWith("equip avatar")) return "avatar";
        if (text.StartsWith("equip")) return "wrench";
        if (text.StartsWith("save")) return "save";
        if (text.StartsWith("pack") || text.StartsWith("unpack")) return "package";
        if (text.StartsWith("edit")) return "edit-widgets";
        if (text.Contains("material") || text.StartsWith("convert")) return "material";
        if (text.StartsWith("settings")) return "settings";
        if (text.StartsWith("cancel") || text.StartsWith("deselect") || text.StartsWith("clear")
            || text.StartsWith("close") || text.StartsWith("stop")) return "close";
        return null;
    }

    // Close fades the menu out (UpdateVisuals takes it down when the fade ends); nothing on it answers
    // the pointer meanwhile.
    private void BeginClosing()
    {
        if (_canvasSlot == null || _canvasSlot.IsDestroyed)
        {
            DestroyCanvas();
            return;
        }
        _closing = true;
        _placing = false;
        _flickItem = null;
        foreach (var built in _builtItems)
        {
            if (built.Button != null && !built.Button.IsDestroyed)
                built.Button.Enabled.Value = false;
        }
        if (_center != null && !_center.Button.IsDestroyed)
            _center.Button.Enabled.Value = false;
        if (_dial != null)
        {
            if (!_dial.Face.IsDestroyed)
                _dial.Face.Enabled.Value = false;
            if (!_dial.HubButton.IsDestroyed)
                _dial.HubButton.Enabled.Value = false;
        }
    }

    private void DestroyVisual()
    {
        if (_menuRoot != null && !_menuRoot.IsDestroyed)
            _menuRoot.Destroy();
        _menuRoot = null!;
        _font = null!;
        _icons.Clear();
        DestroyCanvas();
    }

    // Takes the ring down and keeps the menu's own slot, with its font and icons, for the next open.
    private void DestroyCanvas()
    {
        if (_menuRoot != null && !_menuRoot.IsDestroyed)
            ClearCanvases();
        else if (_canvasSlot != null && !_canvasSlot.IsDestroyed)
            _canvasSlot.Destroy();
        _canvasSlot = null!;
        _builtItems.Clear();
        _center = null;
        _dial = null;
        _closing = false;
        _placing = false;
        _viewPinned = false;
    }

    // Every ring under the menu's slot goes, the current one and any other, so opens can never stack.
    private void ClearCanvases()
    {
        foreach (var child in _menuRoot.Children.ToArray())
            child.Destroy();
    }

    // Item collection

    private ContextMenuPage BuildRootPage(ContextMenuContext context)
    {
        var page = new ContextMenuPage("Menu");

        // 1. Fixed items from RootContextMenuItem components (sorted by Priority desc)
        var rootItems = new List<(int priority, ContextMenuItem item)>();
        foreach (var root in CollectComponents<RootContextMenuItem>())
            rootItems.Add((root.Priority.Value, root.ToMenuItem()));

        foreach (var (_, item) in rootItems.OrderByDescending(x => x.priority))
            page.AddItem(item);

        // 2. Contextual items from ContextMenuItemSource components. Held objects hang under the user
        // root, so their own sources are collected here too, and keyed ones collapse per
        // ContextMenuItemSource.SingleItemKey. -xlinka
        var sources = CollectComponents<ContextMenuItemSource>()
            .OrderByDescending(s => s.Priority.Value)
            .ToList();
        CollapseKeyedSources(sources);
        foreach (var source in sources)
        {
            try
            {
                source.PopulateContextMenu(page, context);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"ContextMenuSystem: {source.GetType().Name}.PopulateContextMenu threw: {ex.Message}");
            }
        }

        // In group order around the ring; OrderBy is stable, so a source's items stay in its order.
        var ordered = page.Items.OrderBy(item => (int)item.Group).ToList();
        page.Items.Clear();
        page.Items.AddRange(ordered);
        return page;
    }

    private static void CollapseKeyedSources(List<ContextMenuItemSource> sources)
    {
        Dictionary<string, int>? counts = null;
        foreach (var source in sources)
        {
            var key = source.SingleItemKey;
            if (key == null)
                continue;
            counts ??= [];
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        if (counts == null)
            return;

        HashSet<string> kept = [];
        sources.RemoveAll(source =>
        {
            var key = source.SingleItemKey;
            if (key == null || counts[key] <= 1)
                return false;
            if (source.KeepFirstSingleItem && kept.Add(key))
                return false;
            return true;
        });
    }

    private IEnumerable<T> CollectComponents<T>() where T : Component
    {
        var results = new List<T>();
        var root = Slot.ActiveUserRoot?.Slot ?? Slot;
        CollectRecursive<T>(root, results);
        return results;
    }

    private static void CollectRecursive<T>(Slot slot, List<T> results) where T : Component
    {
        if (!slot.IsActive)
            return;
        foreach (var comp in slot.Components)
            if (comp is T t) results.Add(t);
        foreach (var child in slot.Children)
            CollectRecursive<T>(child, results);
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The window manager behind the dash. Every app has a window from the start (closed until asked for), and
// the ones that were ever opened sit in one stack, bottom to top. Its order is both the hit order (tree
// order, last match wins) and the depth order (each open window a step further toward the reader, so its
// depth-writing backing covers the windows below). Focus is the top open window, or nothing, which hands
// the keyboard to the desk.
//
// The user's own dash keeps its arrangement in the settings store, written a moment after the last change
// rather than on every drag frame, because each write saves the whole store: the windows under one key, the
// apps out in the room as panels under a second, each panel's pose in the frame its placement keeps (the
// user's, the head's, a hand's) so it comes back around the user wherever they start. -xlinka
[ComponentCategory("Hidden")]
[UserspaceOnly]
public sealed class DashWorkspace : UIComponent
{
    // 6 mm a window on the dash's canvas, clear of the 4 mm a window backing pushes its depth back.
    public const float LiftStep = 6f;
    public const string LayoutKey = "Dash.Workspace.Layout";
    public const string PanelsKey = "Dash.Workspace.Panels";
    private const float SnapEdge = 36f;
    // Fill is the very top of the desk and anything above it. The top row's title bars start at the desk top,
    // so a band as deep as the side ones filled a window that was only being moved along the row.
    private const float FillEdge = 8f;
    private const float SnapGap = 12f;
    private const float CascadeStep = 28f;
    private const double SaveDelay = 0.5;
    private const long OrderBase = 100L;
    // How far a window shrinks and drops on its way in and out, and how small it gets on its plate.
    private const float OpenScale = 0.96f;
    private const float OpenRise = 12f;
    private const float CloseScale = 0.96f;
    private const float CloseDrop = 8f;
    private const float DockScale = 0.18f;

    private readonly List<DashWindow> _windows = new();
    private readonly List<DashWindow> _stack = new();
    private readonly Dictionary<DashWindow, float2> _pendingSize = new();
    private readonly List<DashWindow> _scratch = new();
    private readonly HashSet<DashWindow> _dragged = new();
    private Dashboard? _dash;
    private DashWindow? _focused;
    private double _saveAt = -1;
    private string? _lastSaved;
    private string? _lastPanels;
    private float2 _lastCanvas;
    private bool _lastVr;

    public IReadOnlyList<DashWindow> Windows => _windows;
    public IReadOnlyList<DashWindow> Stack => _stack;
    public DashWindow? Focused => _focused is { IsDestroyed: false, State: DashWindow.WindowState.Open } focused ? focused : null;

    // The user's own dash only. A dash standing in a world arranges itself and keeps nothing.
    public bool PersistLayout { get; set; }

    // Goes up on every change the dock and the tray draw from, so they can poll it for free.
    public int Revision { get; private set; }

    internal void Bind(Dashboard dash) => _dash = dash;

    public DashWindow? WindowOf(DashboardScreen? screen)
    {
        if (screen == null)
            return null;
        for (int i = 0; i < _windows.Count; i++)
        {
            if (ReferenceEquals(_windows[i].Screen, screen))
                return _windows[i];
        }
        return null;
    }

    public DashWindow? WindowByKey(string key)
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            if (_windows[i].Key == key)
                return _windows[i];
        }
        return null;
    }

    internal DashWindow AddWindow(string key, string label, string icon, color tint, bool inDock, int dockGroup, float2 defaultSize, float2 minSize)
    {
        var slot = Slot.AddSlot(label);
        slot.ActiveSelf.Value = false;
        slot.OrderOffset.Value = OrderBase;
        var window = slot.AttachComponent<DashWindow>();
        window.DefaultSize = defaultSize;
        window.MinSize = minSize;
        window.Build(_dash!, this, key, label, icon, tint, inDock, dockGroup);
        _windows.Add(window);
        Revision++;
        return window;
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (_dash == null)
            return;

        if (_pendingSize.Count > 0)
            ApplyPendingSizes(force: false);

        var canvas = _dash.Size.Value;
        if (canvas != _lastCanvas)
        {
            bool first = _lastCanvas == float2.Zero;
            _lastCanvas = canvas;
            if (!first)
                RefitAll();
        }

        for (int i = 0; i < _windows.Count; i++)
        {
            var window = _windows[i];
            if (window.State == DashWindow.WindowState.Out && (window.Popped == null || window.Popped.IsDestroyed))
                PanelGone(window);
        }

        // The headset coming on or going off decides whether a window can leave for the room.
        bool vr = Engine.Current?.InputInterface?.IsVRActive == true;
        if (vr != _lastVr)
        {
            _lastVr = vr;
            for (int i = 0; i < _windows.Count; i++)
                _windows[i].SetPopAvailable(CanPopOut(_windows[i]));
        }

        if (_saveAt >= 0 && World != null && World.Time.TotalTime >= _saveAt)
            Flush();
    }

    public override void OnDestroy()
    {
        Flush();
        base.OnDestroy();
    }

    // OPEN, FOCUS, CLOSE

    // Whatever it takes to have this window up and in front: open it, bring it back from the dock or from
    // the room, then raise it.
    public void Present(DashWindow window)
    {
        switch (window.State)
        {
            case DashWindow.WindowState.Out:
                DockBack(window);
                return;
            case DashWindow.WindowState.Closed:
                Open(window);
                break;
            case DashWindow.WindowState.Minimized:
                Show(window);
                break;
        }
        Focus(window);
    }

    // A dock plate: open and in front goes to the dock, open behind comes forward, anything else comes up.
    public void Toggle(DashWindow window)
    {
        if (window.State == DashWindow.WindowState.Open && ReferenceEquals(Focused, window))
            Minimize(window);
        else
            Present(window);
    }

    public void Focus(DashWindow window)
    {
        if (window.State != DashWindow.WindowState.Open)
        {
            Present(window);
            return;
        }
        _stack.Remove(window);
        _stack.Add(window);
        _focused = window;
        Restack();
        Touch();
    }

    public void FocusDesk()
    {
        if (_focused == null)
            return;
        _focused = null;
        Restack();
    }

    public void Minimize(DashWindow window)
    {
        if (window.State != DashWindow.WindowState.Open)
            return;
        window.State = DashWindow.WindowState.Minimized;
        Conceal(window, toDock: true);
        GiveUpTile(window);
        if (ReferenceEquals(_focused, window))
            _focused = TopOpen();
        Restack();
        Touch();
    }

    public void Close(DashWindow window)
    {
        if (window.State == DashWindow.WindowState.Closed)
            return;
        var popped = window.Popped;
        window.Popped = null;
        bool wasOpen = window.State == DashWindow.WindowState.Open;
        if (wasOpen)
            Conceal(window, toDock: false);
        window.State = DashWindow.WindowState.Closed;
        _stack.Remove(window);
        if (wasOpen)
            GiveUpTile(window);
        if (ReferenceEquals(_focused, window))
            _focused = TopOpen();
        // Out in the room: the panel shrinks away and hands the app back to the window on its way.
        if (popped != null && !popped.IsDestroyed)
            popped.Retire(animate: true);
        Restack();
        Touch();
    }

    // The Workspace plate: every open window goes to the dock and the desk is in front.
    public void MinimizeAll()
    {
        _scratch.Clear();
        _scratch.AddRange(_stack);
        foreach (var window in _scratch)
        {
            if (window.State == DashWindow.WindowState.Open)
            {
                window.State = DashWindow.WindowState.Minimized;
                Conceal(window, toDock: true);
            }
        }
        _scratch.Clear();
        _focused = null;
        Restack();
        Touch();
    }

    private void Open(DashWindow window, bool restoring = false)
    {
        if (!window.Placed)
            PlaceNew(window);
        window.State = DashWindow.WindowState.Open;
        _stack.Remove(window);
        _stack.Add(window);
        // The big apps take the whole desk until somebody arranges them; after that they come back as left.
        if (!restoring && window.OpensFilled && !window.UserArranged && window.Snap != DashWindow.WindowSnap.Fill)
            ApplySnap(window, DashWindow.WindowSnap.Fill);
        else
            ReclaimTile(window);
        Reveal(window, fromDock: false);
    }

    private void Show(DashWindow window)
    {
        bool fromDock = window.State == DashWindow.WindowState.Minimized;
        window.State = DashWindow.WindowState.Open;
        if (!_stack.Contains(window))
            _stack.Add(window);
        ReclaimTile(window);
        Reveal(window, fromDock);
    }

    // The window has to be up before the app builds: text built under an inactive slot never gets laid out.
    private void Reveal(DashWindow window, bool fromDock)
    {
        Reclaim(window);
        window.SetShown(true);
        window.Screen?.ShowScreen();
        if (fromDock && _dash?.Dock?.PlateCenterOf(window) is { } plate)
            window.Animate(DockScale, plate - window.DrawnCenter, 1f, float2.Zero, DashWindow.DockSeconds, null);
        else
            window.Animate(OpenScale, new float2(0f, -OpenRise), 1f, float2.Zero, DashWindow.OpenSeconds, null);
    }

    // Away with motion: down to its plate, or a small shrink and drop, then hidden. The state has already
    // changed; a window brought back before the motion ends just turns around.
    private void Conceal(DashWindow window, bool toDock)
    {
        float2? plate = toDock ? _dash?.Dock?.PlateCenterOf(window) : null;
        var to = plate.HasValue ? plate.Value - window.DrawnCenter : new float2(0f, -CloseDrop);
        window.Animate(1f, float2.Zero, plate.HasValue ? DockScale : CloseScale, to,
            plate.HasValue ? DashWindow.DockSeconds : DashWindow.CloseSeconds, () => FinishHide(window));
    }

    private static void FinishHide(DashWindow window)
    {
        if (window.IsDestroyed || window.State == DashWindow.WindowState.Open)
            return;
        if (window.Screen != null && window.Body != null && ReferenceEquals(window.Screen.Slot.Parent, window.Body))
            window.Screen.HideScreen();
        window.SetShown(false);
        window.CancelMotion();
    }

    // An app still on a panel that is on its way out comes home at once.
    private static void Reclaim(DashWindow window)
    {
        var screen = window.Screen;
        if (screen == null || window.Body == null || ReferenceEquals(screen.Slot.Parent, window.Body))
            return;
        DashWindowPanel.Of(screen.Slot)?.Retire(animate: false);
    }

    private DashWindow? TopOpen()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].State == DashWindow.WindowState.Open)
                return _stack[i];
        }
        return null;
    }

    // Tree order and lift from the stack. Closed windows sit at the bottom of the tree, hidden. A window on
    // its way to the dock or away is still drawn until its motion ends, so it keeps a lift until then: at
    // lift 0 the desk drew over it for the length of the motion. One being closed has left the stack and
    // goes over the rest, where it was when it was closed. -xlinka
    private void Restack()
    {
        int lifted = 0;
        for (int i = 0; i < _stack.Count; i++)
        {
            var window = _stack[i];
            bool open = window.State == DashWindow.WindowState.Open;
            float lift = open || window.Moving ? LiftStep * ++lifted : 0f;
            window.SetStack(OrderBase + 1 + i, lift, open && ReferenceEquals(window, _focused));
        }
        for (int i = 0; i < _windows.Count; i++)
        {
            var window = _windows[i];
            if (!_stack.Contains(window))
                window.SetStack(OrderBase, window.Moving ? LiftStep * ++lifted : 0f, false);
            window.SetFilledGlyph(window.Snap == DashWindow.WindowSnap.Fill);
            window.SetPopAvailable(CanPopOut(window));
        }
        UpdateEdges();
        _dash?.SetStackHeight(lifted);
        Revision++;
        _dash?.NotifyFocus();
    }

    // PLACEMENT

    private void PlaceNew(DashWindow window)
    {
        var zone = _dash!.WindowZone;
        var size = ClampSize(window, window.DefaultSize);
        int open = 0;
        for (int i = 0; i < _stack.Count; i++)
        {
            if (_stack[i].State == DashWindow.WindowState.Open)
                open++;
        }
        float step = CascadeStep * (open % 6);
        var position = new float2(zone.x + 12f + step, zone.y + 4f + step);
        window.SetSize(size);
        window.SetPosition(ClampPosition(position, size));
        window.Placed = true;
    }

    // exact keeps the tile assignment, but never bypasses the app's size limits. A tile that no longer
    // fits (including an old saved layout) returns to a usable floating rect.
    public void SetBounds(DashWindow window, in Rect bounds, bool exact = false)
    {
        if (exact && window.Snap is not (DashWindow.WindowSnap.None or DashWindow.WindowSnap.Fill)
            && !CanSnap(window, window.Snap))
        {
            Float(window);
            return;
        }
        if (!exact && window.Snap != DashWindow.WindowSnap.None)
        {
            window.Snap = DashWindow.WindowSnap.None;
            window.RestoreBounds = null;
            window.FillFrom = DashWindow.WindowSnap.None;
            window.SetFilledGlyph(false);
        }
        var size = ClampSize(window, bounds.Size);
        window.SetSize(size);
        var position = new float2(bounds.x, bounds.y);
        if (exact && window.Snap == DashWindow.WindowSnap.Fill)
            position += (bounds.Size - size) * 0.5f;
        window.SetPosition(ClampPosition(position, size));
        window.Placed = true;
        _pendingSize.Remove(window);
    }

    private float2 ClampPosition(float2 position, in float2 size)
    {
        var desk = _dash!.DeskRect;
        float maxX = desk.x + desk.width - size.x;
        float maxY = desk.y + desk.height - size.y;
        position.x = MathF.Max(desk.x, MathF.Min(position.x, maxX));
        position.y = MathF.Max(desk.y, MathF.Min(position.y, maxY));
        return position;
    }

    private float2 ClampSize(DashWindow window, float2 size)
        => window.ClampSize(size, _dash!.DeskRect.Size);

    public bool CanSnap(DashWindow window, DashWindow.WindowSnap tile)
    {
        if (_dash == null || tile == DashWindow.WindowSnap.None)
            return false;
        // Fill uses the available desk, capped at the app's maximum (small dialogs stay small).
        if (tile == DashWindow.WindowSnap.Fill)
            return true;
        var size = SnapRect(tile).Size;
        return size.x >= window.MinSize.x && size.y >= window.MinSize.y
            && size.x <= window.MaxSize.x && size.y <= window.MaxSize.y;
    }

    // The canvas changed size (the window it is shown in did). Snapped windows follow their snap, the rest
    // are pulled back inside the desk.
    private void RefitAll()
    {
        foreach (var window in _windows)
        {
            if (!window.Placed)
                continue;
            if (window.Snap != DashWindow.WindowSnap.None)
                SetBounds(window, SnapRect(window.Snap), exact: true);
            else
                SetBounds(window, window.Bounds);
        }
        Touch();
    }

    // DRAG, SNAP, FILL

    public void DragTo(DashWindow window, float2 position, in float2 pointer)
    {
        if (_dash == null)
            return;
        if (window.State != DashWindow.WindowState.Open)
        {
            if (_dragged.Remove(window))
                _dash.ShowGuide(null);
            return;
        }
        _dragged.Add(window);
        window.SetPosition(ClampPosition(position, window.Size));
        window.UserArranged = true;
        var snap = SnapFor(window, pointer);
        _dash.ShowGuide(snap != DashWindow.WindowSnap.None ? SnapRect(snap) : null);
    }

    // Only a window that was dragged lands anywhere: a press let go where it went down is a click. One that left
    // the desk under the drag (closed, docked, out in the room) lands nowhere either. A drag cut short (the beam
    // off the dash, the dash put away) lands where the pointer was last seen, the tile its guide was showing.
    public void EndDrag(DashWindow window, in float2 pointer)
    {
        if (_dash == null || !_dragged.Remove(window))
            return;
        _dash.ShowGuide(null);
        if (window.State != DashWindow.WindowState.Open)
            return;
        var snap = SnapFor(window, pointer);
        if (snap != DashWindow.WindowSnap.None)
        {
            PlaceTile(window, snap);
        }
        else if (window.Snap != DashWindow.WindowSnap.None)
        {
            // Dragged out of a snap: it keeps the size it has now and is a free window again.
            window.Snap = DashWindow.WindowSnap.None;
            window.RestoreBounds = null;
            window.SetFilledGlyph(false);
        }
        Touch();
    }

    // TILES
    //
    // The desk is four cells, two across and two down, split where the user last moved the lines between
    // them. A tile is the cells a window covers: all four (filled), a column (a half) or one (a quarter).
    // Sending a window to a tile moves aside whatever stood there: a filled window keeps what is left of the
    // desk, a half keeps what is left of its column, and a window with nothing left goes to whatever is free,
    // or, with nothing free, back to floating where it was before it was tiled. A window that leaves the desk
    // (closed, sent to the dock, out in the room) gives its cells to the neighbour that can grow into them,
    // and takes them back when it returns. Filling does none of this: it covers, like any window on top. -xlinka

    private const int CellTopLeft = 1;
    private const int CellTopRight = 2;
    private const int CellBottomLeft = 4;
    private const int CellBottomRight = 8;
    private const int AllCells = 15;
    private const float SplitMin = 0.25f;
    private const float SplitMax = 0.75f;
    // How far down from the top, or up from the bottom, of an edge a drag picks the corner instead.
    private const float CornerReach = 140f;

    // Halves before quarters: what a window moved aside lands in, largest first.
    private static readonly DashWindow.WindowSnap[] AsideTiles =
    {
        DashWindow.WindowSnap.Left, DashWindow.WindowSnap.Right,
        DashWindow.WindowSnap.TopLeft, DashWindow.WindowSnap.TopRight, DashWindow.WindowSnap.BottomLeft, DashWindow.WindowSnap.BottomRight,
    };

    // Where the line between the columns sits, and the line between top and bottom in each column, as a share
    // of the room the tiles have.
    public float SplitX { get; private set; } = 0.5f;
    public float SplitYLeft { get; private set; } = 0.5f;
    public float SplitYRight { get; private set; } = 0.5f;

    public static int CellsOf(DashWindow.WindowSnap snap) => snap switch
    {
        DashWindow.WindowSnap.Fill => AllCells,
        DashWindow.WindowSnap.Left => CellTopLeft | CellBottomLeft,
        DashWindow.WindowSnap.Right => CellTopRight | CellBottomRight,
        DashWindow.WindowSnap.TopLeft => CellTopLeft,
        DashWindow.WindowSnap.TopRight => CellTopRight,
        DashWindow.WindowSnap.BottomLeft => CellBottomLeft,
        DashWindow.WindowSnap.BottomRight => CellBottomRight,
        _ => 0,
    };

    private static bool Tiled(DashWindow window) => window.State == DashWindow.WindowState.Open && window.Snap != DashWindow.WindowSnap.None;

    private static bool InLeftColumn(DashWindow.WindowSnap snap)
        => snap is DashWindow.WindowSnap.Left or DashWindow.WindowSnap.TopLeft or DashWindow.WindowSnap.BottomLeft;

    // The open windows tiled on the desk other than this one, the one in front first.
    private List<DashWindow> TiledOthers(DashWindow except)
    {
        var list = new List<DashWindow>();
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            var window = _stack[i];
            if (!ReferenceEquals(window, except) && Tiled(window))
                list.Add(window);
        }
        return list;
    }

    // Sends a window to a tile, moving aside what stood there.
    public void PlaceTile(DashWindow window, DashWindow.WindowSnap tile)
    {
        if (!CanSnap(window, tile))
            return;
        if (tile == DashWindow.WindowSnap.Fill)
        {
            // Filled from a tile, it goes back to that tile when filled again.
            if (window.Snap is not (DashWindow.WindowSnap.None or DashWindow.WindowSnap.Fill))
                window.FillFrom = window.Snap;
            ApplySnap(window, tile);
            return;
        }
        int target = CellsOf(tile);
        var others = TiledOthers(window);
        int taken = target;
        foreach (var other in others)
        {
            if (other.Snap != DashWindow.WindowSnap.Fill && (CellsOf(other.Snap) & target) == 0)
                taken |= CellsOf(other.Snap);
        }
        foreach (var other in others)
        {
            if (other.Snap != DashWindow.WindowSnap.Fill && (CellsOf(other.Snap) & target) == 0)
                continue;
            int was = CellsOf(other.Snap);
            var aside = LargestTile(other, was & ~taken, was);
            if (aside == DashWindow.WindowSnap.None)
                aside = LargestTile(other, AllCells & ~taken, was);
            if (aside != DashWindow.WindowSnap.None)
            {
                SetTile(other, aside);
                taken |= CellsOf(aside);
            }
            else
            {
                Float(other);
            }
        }
        SetTile(window, tile);
        Restack();
        Touch();
    }

    // The largest tile that fits in the free cells, nearest to where the window was.
    private DashWindow.WindowSnap LargestTile(DashWindow window, int free, int near)
    {
        var best = DashWindow.WindowSnap.None;
        foreach (var tile in AsideTiles)
        {
            int cells = CellsOf(tile);
            if ((cells & ~free) != 0 || !CanSnap(window, tile))
                continue;
            if ((cells & near) != 0)
                return tile;
            if (best == DashWindow.WindowSnap.None)
                best = tile;
        }
        return best;
    }

    private void SetTile(DashWindow window, DashWindow.WindowSnap tile)
    {
        if (window.Snap == DashWindow.WindowSnap.None)
            window.RestoreBounds = window.Bounds;
        window.Snap = tile;
        SetBounds(window, SnapRect(tile), exact: true);
        window.SetFilledGlyph(tile == DashWindow.WindowSnap.Fill);
    }

    // Off the tiles, back where it floated before.
    private void Float(DashWindow window)
    {
        var back = window.RestoreBounds;
        window.Snap = DashWindow.WindowSnap.None;
        window.RestoreBounds = null;
        var zone = _dash!.WindowZone;
        SetBounds(window, back ?? new Rect(zone.x + 12f, zone.y + 4f, window.DefaultSize.x, window.DefaultSize.y));
        window.SetFilledGlyph(false);
    }

    // A tiled window left the desk: its neighbours grow into its cells, the smallest growth first, so a
    // quarter becomes the half of its column before anything becomes the whole desk.
    private void GiveUpTile(DashWindow window)
    {
        if (window.Snap is DashWindow.WindowSnap.None or DashWindow.WindowSnap.Fill)
            return;
        int vacated = CellsOf(window.Snap);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var other in TiledOthers(window))
            {
                if (other.Snap == DashWindow.WindowSnap.Fill)
                    continue;
                int cells = CellsOf(other.Snap);
                int occupied = 0;
                foreach (var third in TiledOthers(window))
                {
                    if (!ReferenceEquals(third, other) && third.Snap != DashWindow.WindowSnap.Fill)
                        occupied |= CellsOf(third.Snap);
                }
                foreach (var bigger in new[] { DashWindow.WindowSnap.Left, DashWindow.WindowSnap.Right, DashWindow.WindowSnap.Fill })
                {
                    int grown = CellsOf(bigger);
                    if ((grown & cells) != cells || grown == cells || (grown & occupied) != 0 || (grown & ~cells & ~vacated) != 0
                        || !CanSnap(other, bigger))
                        continue;
                    SetTile(other, bigger);
                    grew = true;
                    break;
                }
                if (grew)
                    break;
            }
        }
    }

    // A window coming back takes its tile back.
    private void ReclaimTile(DashWindow window)
    {
        if (window.Snap is not (DashWindow.WindowSnap.None or DashWindow.WindowSnap.Fill))
            PlaceTile(window, window.Snap);
    }

    // The line between tiles moved by its strip: every tile on either side follows. Sizes land one per
    // finished rebuild while dragging, like a corner resize.
    public void DragEdge(DashWindow window, DashWindow.TileEdge edge, in float2 pointer, bool final)
    {
        if (_dash == null || window.Snap == DashWindow.WindowSnap.None)
            return;
        var zone = _dash.WindowZone;
        if (edge is DashWindow.TileEdge.Left or DashWindow.TileEdge.Right)
        {
            SplitX = ClampSplit((pointer.x - zone.x - SnapGap * 0.5f) / MathF.Max(1f, zone.width - SnapGap),
                horizontal: true, leftColumn: false);
        }
        else
        {
            float share = ClampSplit((pointer.y - zone.y - SnapGap * 0.5f) / MathF.Max(1f, zone.height - SnapGap),
                horizontal: false, leftColumn: InLeftColumn(window.Snap));
            if (InLeftColumn(window.Snap))
                SplitYLeft = share;
            else
                SplitYRight = share;
        }
        foreach (var tiled in _windows)
        {
            if (!Tiled(tiled) || tiled.Snap == DashWindow.WindowSnap.Fill)
                continue;
            var rect = SnapRect(tiled.Snap);
            tiled.SetPosition(new float2(rect.x, rect.y));
            _pendingSize[tiled] = rect.Size;
        }
        ApplyPendingSizes(final);
        if (final)
        {
            window.UserArranged = true;
            Touch();
        }
    }

    // The shared divider must satisfy both neighbours, including their maximum sizes.
    private float ClampSplit(float requested, bool horizontal, bool leftColumn)
    {
        var zone = _dash!.WindowZone;
        float extent = MathF.Max(1f, (horizontal ? zone.width : zone.height) - SnapGap);
        float lower = SplitMin, upper = SplitMax;
        foreach (var tiled in _windows)
        {
            var snap = tiled.Snap;
            if (!Tiled(tiled) || snap == DashWindow.WindowSnap.Fill)
                continue;
            if (!horizontal && (InLeftColumn(snap) != leftColumn
                || snap is DashWindow.WindowSnap.Left or DashWindow.WindowSnap.Right))
                continue;
            bool first = horizontal ? InLeftColumn(snap)
                : snap is DashWindow.WindowSnap.TopLeft or DashWindow.WindowSnap.TopRight;
            float min = MathF.Ceiling(horizontal ? tiled.MinSize.x : tiled.MinSize.y);
            float max = MathF.Floor(horizontal ? tiled.MaxSize.x : tiled.MaxSize.y);
            lower = MathF.Max(lower, first ? min / extent : 1f - max / extent);
            upper = MathF.Min(upper, first ? max / extent : 1f - min / extent);
        }
        float previous = horizontal ? SplitX : leftColumn ? SplitYLeft : SplitYRight;
        return lower <= upper ? System.Math.Clamp(requested, lower, upper) : previous;
    }

    // Which edges of each tiled window face a tiled neighbour: those are the ones that move the line.
    private void UpdateEdges()
    {
        int left = 0, right = 0;
        foreach (var window in _windows)
        {
            if (!Tiled(window) || window.Snap == DashWindow.WindowSnap.Fill)
                continue;
            if (InLeftColumn(window.Snap))
                left |= CellsOf(window.Snap);
            else
                right |= CellsOf(window.Snap);
        }
        foreach (var window in _windows)
        {
            if (!Tiled(window) || window.Snap == DashWindow.WindowSnap.Fill)
            {
                window.SetSharedEdges(false, false, false, false);
                continue;
            }
            var snap = window.Snap;
            bool inLeft = InLeftColumn(snap);
            window.SetSharedEdges(
                !inLeft && left != 0,
                inLeft && right != 0,
                snap == DashWindow.WindowSnap.BottomLeft && (left & CellTopLeft) != 0
                    || snap == DashWindow.WindowSnap.BottomRight && (right & CellTopRight) != 0,
                snap == DashWindow.WindowSnap.TopLeft && (left & CellBottomLeft) != 0
                    || snap == DashWindow.WindowSnap.TopRight && (right & CellBottomRight) != 0);
        }
    }

    // What a drag would drop on: an edge of the desk (a half), its corners (a quarter), its top (the whole
    // desk), or a tiled window under the pointer (the half or quarter of it nearest the pointer). The trigger
    // is the pointer, not the window: a wide window held by its middle would otherwise snap before the hand is
    // anywhere near the edge.
    public DashWindow.WindowSnap SnapFor(in float2 pointer) => SnapFor(null, pointer);

    public DashWindow.WindowSnap SnapFor(DashWindow? dragged, in float2 pointer)
    {
        var tile = FindSnap(pointer, dragged);
        if (dragged == null || tile == DashWindow.WindowSnap.None || CanSnap(dragged, tile))
            return tile;
        // A corner too short for this app can still offer the full column.
        var column = InLeftColumn(tile) ? DashWindow.WindowSnap.Left : DashWindow.WindowSnap.Right;
        return CanSnap(dragged, column) ? column : DashWindow.WindowSnap.None;
    }

    private DashWindow.WindowSnap FindSnap(in float2 pointer, DashWindow? dragged)
    {
        var zone = _dash!.WindowZone;
        float right = zone.x + zone.width;
        float bottom = zone.y + zone.height;
        float reach = MathF.Min(CornerReach, zone.height * 0.22f);
        bool nearLeft = pointer.x < zone.x + SnapEdge;
        bool nearRight = pointer.x > right - SnapEdge && pointer.x < right + SnapEdge;
        if (nearLeft || nearRight)
        {
            if (pointer.y < zone.y + reach)
                return nearLeft ? DashWindow.WindowSnap.TopLeft : DashWindow.WindowSnap.TopRight;
            if (pointer.y > bottom - reach)
                return nearLeft ? DashWindow.WindowSnap.BottomLeft : DashWindow.WindowSnap.BottomRight;
            return nearLeft ? DashWindow.WindowSnap.Left : DashWindow.WindowSnap.Right;
        }
        if (pointer.y < zone.y + FillEdge)
            return DashWindow.WindowSnap.Fill;

        // The window under the pointer, if it is tiled: a floating one on top of it hides it.
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            var window = _stack[i];
            if (ReferenceEquals(window, dragged) || window.State != DashWindow.WindowState.Open || !window.Bounds.Contains(pointer))
                continue;
            return window.Snap == DashWindow.WindowSnap.None ? DashWindow.WindowSnap.None : PartOf(window.Snap, pointer);
        }
        return DashWindow.WindowSnap.None;
    }

    // The half or quarter of a tile nearest a point in it.
    private DashWindow.WindowSnap PartOf(DashWindow.WindowSnap tile, in float2 pointer)
    {
        var left = SnapRect(DashWindow.WindowSnap.Left);
        var topLeft = SnapRect(DashWindow.WindowSnap.TopLeft);
        var topRight = SnapRect(DashWindow.WindowSnap.TopRight);
        bool westSide = pointer.x < left.x + left.width + SnapGap * 0.5f;
        return tile switch
        {
            DashWindow.WindowSnap.Fill => westSide ? DashWindow.WindowSnap.Left : DashWindow.WindowSnap.Right,
            DashWindow.WindowSnap.Left => pointer.y < topLeft.y + topLeft.height + SnapGap * 0.5f
                ? DashWindow.WindowSnap.TopLeft : DashWindow.WindowSnap.BottomLeft,
            DashWindow.WindowSnap.Right => pointer.y < topRight.y + topRight.height + SnapGap * 0.5f
                ? DashWindow.WindowSnap.TopRight : DashWindow.WindowSnap.BottomRight,
            _ => tile,
        };
    }

    public Rect SnapRect(DashWindow.WindowSnap snap)
    {
        var zone = _dash!.WindowZone;
        float leftWidth = MathF.Round((zone.width - SnapGap) * SplitX);
        float rightWidth = zone.width - SnapGap - leftWidth;
        float rightX = zone.x + leftWidth + SnapGap;
        float topLeft = MathF.Round((zone.height - SnapGap) * SplitYLeft);
        float topRight = MathF.Round((zone.height - SnapGap) * SplitYRight);
        return snap switch
        {
            DashWindow.WindowSnap.Left => new Rect(zone.x, zone.y, leftWidth, zone.height),
            DashWindow.WindowSnap.Right => new Rect(rightX, zone.y, rightWidth, zone.height),
            DashWindow.WindowSnap.TopLeft => new Rect(zone.x, zone.y, leftWidth, topLeft),
            DashWindow.WindowSnap.BottomLeft => new Rect(zone.x, zone.y + topLeft + SnapGap, leftWidth, zone.height - topLeft - SnapGap),
            DashWindow.WindowSnap.TopRight => new Rect(rightX, zone.y, rightWidth, topRight),
            DashWindow.WindowSnap.BottomRight => new Rect(rightX, zone.y + topRight + SnapGap, rightWidth, zone.height - topRight - SnapGap),
            _ => zone,
        };
    }

    // From the layouts on a window's fill button: the same as dropping it there.
    public void SendTo(DashWindow window, DashWindow.WindowSnap tile)
    {
        if (!CanSnap(window, tile))
            return;
        if (window.State != DashWindow.WindowState.Open)
            Present(window);
        window.UserArranged = true;
        PlaceTile(window, tile);
        Focus(window);
    }

    public void ApplySnap(DashWindow window, DashWindow.WindowSnap snap)
    {
        if (!CanSnap(window, snap))
            return;
        if (window.Snap == DashWindow.WindowSnap.None)
            window.RestoreBounds = window.Bounds;
        window.Snap = snap;
        SetBounds(window, SnapRect(snap), exact: true);
        window.SetFilledGlyph(snap == DashWindow.WindowSnap.Fill);
        Touch();
    }

    public void ToggleFill(DashWindow window)
    {
        window.UserArranged = true;
        if (window.Snap == DashWindow.WindowSnap.Fill && window.FillFrom != DashWindow.WindowSnap.None)
        {
            var from = window.FillFrom;
            window.FillFrom = DashWindow.WindowSnap.None;
            PlaceTile(window, from);
        }
        else if (window.Snap == DashWindow.WindowSnap.Fill)
        {
            var back = window.RestoreBounds;
            window.Snap = DashWindow.WindowSnap.None;
            window.RestoreBounds = null;
            if (back.HasValue)
                SetBounds(window, back.Value);
            else
                SetBounds(window, new Rect(window.Position, window.DefaultSize));
            window.SetFilledGlyph(false);
            Touch();
        }
        else
        {
            window.FillFrom = window.Snap;
            ApplySnap(window, DashWindow.WindowSnap.Fill);
        }
        if (window.State == DashWindow.WindowState.Open)
            Focus(window);
    }

    // RESIZE

    // Every size change is a relayout and a re-mesh of the whole app, so a drag lands one size per finished
    // canvas rebuild and the release always lands. Stacking them faster than the canvas can build them is
    // what draws half an app at one size and half at another.
    public void ResizeTo(DashWindow window, float2 size, bool final)
    {
        if (_dash == null)
            return;
        if (window.Snap != DashWindow.WindowSnap.None)
        {
            window.Snap = DashWindow.WindowSnap.None;
            window.RestoreBounds = null;
            window.SetFilledGlyph(false);
        }
        var desk = _dash.DeskRect;
        size.x = MathF.Min(size.x, desk.x + desk.width - window.Position.x);
        size.y = MathF.Min(size.y, desk.y + desk.height - window.Position.y);
        _pendingSize[window] = ClampSize(window, size);
        ApplyPendingSizes(final);
        if (final)
        {
            window.UserArranged = true;
            Restack();
            Touch();
        }
    }

    private void ApplyPendingSizes(bool force)
    {
        if (!force && _dash?.Canvas is { IsRebuildInProgress: true })
            return;
        _scratch.Clear();
        _scratch.AddRange(_pendingSize.Keys);
        foreach (var window in _scratch)
        {
            if (!window.IsDestroyed)
            {
                var size = ClampSize(window, _pendingSize[window]);
                window.SetSize(size);
                window.SetPosition(ClampPosition(window.Position, size));
            }
            _pendingSize.Remove(window);
        }
        _scratch.Clear();
    }

    // POP OUT

    // A window can leave the dash only on this machine's own dash: a dash standing in a world is somebody's
    // furniture. Headset or desktop alike; on a desktop the panel floats where the window was on the screen.
    public bool CanPopOut(DashWindow window)
    {
        if (_dash == null || window.Screen == null || window.Placeholder)
            return false;
        var local = UserspaceDashboard.LocalInstance;
        return local != null && !local.IsDestroyed && ReferenceEquals(local.Dashboard, _dash);
    }

    public void PopOut(DashWindow window) => PopOutPanel(window);

    // The window's own button. On a desktop the dash then gets out of the way: it covers the whole window,
    // and the app is out in the room now, standing where it stood on the dash.
    public void PopOutFromBar(DashWindow window)
    {
        if (PopOutPanel(window) != null && Engine.Current?.InputInterface?.IsVRActive != true)
            UserspaceDashboard.LocalInstance?.Close();
    }

    public IGrabbable? GrabOut(DashWindow window)
    {
        var panel = PopOutPanel(window);
        return panel?.GrabHandle;
    }

    private DashWindowPanel? PopOutPanel(DashWindow window)
    {
        if (window.State != DashWindow.WindowState.Open || !CanPopOut(window))
            return null;
        window.CancelMotion();
        var panel = DashWindowPanel.Spawn(_dash!, window);
        if (panel == null)
            return null;
        window.Popped = panel;
        // The app went with the panel; the window is an empty frame now and just goes.
        window.SetShown(false);
        window.State = DashWindow.WindowState.Out;
        GiveUpTile(window);
        if (ReferenceEquals(_focused, window))
            _focused = TopOpen();
        Restack();
        Touch();
        return panel;
    }

    // Back from the room: the app goes home to its window, the panel goes, and the window comes up where it
    // left, as it was.
    public void DockBack(DashWindow window)
    {
        if (window.State != DashWindow.WindowState.Out)
            return;
        var popped = window.Popped;
        window.Popped = null;
        if (popped != null && !popped.IsDestroyed)
        {
            popped.ReturnScreen(hide: false);
            popped.Retire(animate: false);
        }
        Show(window);
        Focus(window);
    }

    // The panel went away on its own (something took it down): the app came home closed.
    private void PanelGone(DashWindow window)
    {
        window.Popped = null;
        window.State = DashWindow.WindowState.Closed;
        _stack.Remove(window);
        Restack();
        Touch();
    }

    // FIRST RUN AND PERSISTENCE

    // Put back what was there last time, or the first-run pair side by side. Called once, after the apps are in.
    public void RestoreLayout(IReadOnlyList<DashWindow> firstRun)
    {
        if (_dash == null)
            return;
        _lastCanvas = _dash.Size.Value;
        string saved = PersistLayout ? Lumora.Core.Settings.ReadValue(LayoutKey, string.Empty) ?? string.Empty : string.Empty;
        if (saved.Length > 0 && Apply(saved))
            _lastSaved = saved;
        else
            PlaceFirstRun(firstRun);
        RestorePanels();
    }

    // version|key=mode,side,scale,width,height,x,y,z,qx,qy,qz,qw|... Anything that does not read leaves that
    // app on the dock instead.
    private void RestorePanels()
    {
        if (!PersistLayout || _dash == null)
            return;
        string saved = Lumora.Core.Settings.ReadValue(PanelsKey, string.Empty) ?? string.Empty;
        _lastPanels = saved;
        var parts = saved.Split('|');
        if (parts.Length < 2 || parts[0] != "1")
            return;
        for (int i = 1; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0)
                continue;
            var window = WindowByKey(parts[i].Substring(0, eq));
            var fields = parts[i].Substring(eq + 1).Split(',');
            if (window == null || window.Screen == null || window.Placeholder || window.State == DashWindow.WindowState.Out
                || fields.Length < 12 || !CanPopOut(window))
                continue;
            if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode)
                || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int side)
                || !Enum.IsDefined(typeof(PanelAnchor), mode) || !Enum.IsDefined(typeof(Lumora.Core.Input.Chirality), side)
                || !TryFloats(fields, 2, 10, out var v))
                continue;
            var rotation = new floatQ(v[6], v[7], v[8], v[9]);
            float length = MathF.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
            if (v[0] <= 0f || v[1] <= 0f || v[2] <= 0f || length < 0.5f)
                continue;
            rotation = new floatQ(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
            if (window.State == DashWindow.WindowState.Open)
            {
                window.Screen.HideScreen();
                window.SetShown(false);
            }
            var panel = DashWindowPanel.SpawnRestored(_dash, window, new float2(v[1], v[2]), (PanelAnchor)mode,
                (Lumora.Core.Input.Chirality)side, new float3(v[3], v[4], v[5]), rotation, v[0]);
            if (panel == null)
                continue;
            window.Popped = panel;
            window.State = DashWindow.WindowState.Out;
            if (!_stack.Contains(window))
                _stack.Add(window);
            if (ReferenceEquals(_focused, window))
                _focused = TopOpen();
        }
        Restack();
    }

    // The first-run pair takes the desk as two panes, the first on the left.
    private void PlaceFirstRun(IReadOnlyList<DashWindow> firstRun)
    {
        var zone = _dash!.OpenZone;
        float left = zone.x + 12f;
        float top = zone.y + 4f;
        float width = zone.width - 12f;
        if (firstRun.Count >= 2)
        {
            var first = firstRun[0];
            var second = firstRun[1];
            PlaceNew(first);
            PlaceNew(second);
            Open(first);
            Open(second);
            PlaceTile(first, DashWindow.WindowSnap.Left);
            PlaceTile(second, DashWindow.WindowSnap.Right);
            _focused = second;
        }
        else if (firstRun.Count == 1)
        {
            SetBounds(firstRun[0], new Rect(left, top, MathF.Min(width, firstRun[0].DefaultSize.x), MathF.Min(zone.height - 8f, firstRun[0].DefaultSize.y)));
            Open(firstRun[0]);
            _focused = firstRun[0];
        }
        Restack();
        Touch();
    }

    // version|focus|splitX,splitYLeft,splitYRight|key=state,x,y,w,h,snap,rx,ry,rw,rh,arranged|... with the
    // windows in stack order, then the ones that were never in the stack but have a place. A window out in the
    // room is kept here as on the dock, so it has its place on the dash, and its panel under PanelsKey. A
    // version 1 layout (no lines, no arranged flag) still reads.
    public string Serialize()
    {
        var sb = new StringBuilder("2|");
        sb.Append(Focused?.Key ?? string.Empty);
        sb.Append('|').Append(F(SplitX, 3)).Append(',').Append(F(SplitYLeft, 3)).Append(',').Append(F(SplitYRight, 3));
        foreach (var window in _stack)
            AppendWindow(sb, window);
        foreach (var window in _windows)
        {
            if (!_stack.Contains(window) && window.Placed)
                AppendWindow(sb, window);
        }
        return sb.ToString();
    }

    private static void AppendWindow(StringBuilder sb, DashWindow window)
    {
        char state = window.State switch
        {
            DashWindow.WindowState.Open => 'o',
            DashWindow.WindowState.Minimized => 'm',
            DashWindow.WindowState.Out => 'm',
            _ => 'c',
        };
        var b = window.Bounds;
        var r = window.RestoreBounds ?? b;
        sb.Append('|').Append(window.Key).Append('=').Append(state)
            .Append(',').Append(F(b.x)).Append(',').Append(F(b.y)).Append(',').Append(F(b.width)).Append(',').Append(F(b.height))
            .Append(',').Append((int)window.Snap)
            .Append(',').Append(F(r.x)).Append(',').Append(F(r.y)).Append(',').Append(F(r.width)).Append(',').Append(F(r.height))
            .Append(',').Append(window.UserArranged ? '1' : '0');
    }

    private static string F(float value, int digits = 1) => MathF.Round(value, digits).ToString(CultureInfo.InvariantCulture);

    private bool Apply(string saved)
    {
        var parts = saved.Split('|');
        if (parts.Length < 2 || (parts[0] != "1" && parts[0] != "2"))
            return false;
        int first = 2;
        if (parts[0] == "2")
        {
            if (parts.Length < 3)
                return false;
            var lines = parts[2].Split(',');
            if (lines.Length == 3 && TryFloats(lines, 0, 3, out var split))
            {
                SplitX = System.Math.Clamp(split[0], SplitMin, SplitMax);
                SplitYLeft = System.Math.Clamp(split[1], SplitMin, SplitMax);
                SplitYRight = System.Math.Clamp(split[2], SplitMin, SplitMax);
            }
            first = 3;
        }
        var placed = new List<(DashWindow Window, char State)>();
        for (int i = first; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0)
                continue;
            var window = WindowByKey(parts[i].Substring(0, eq));
            var fields = parts[i].Substring(eq + 1).Split(',');
            if (window == null || fields.Length < 10 || fields[0].Length != 1)
                continue;
            if (!TryFloats(fields, 1, 4, out var b) || !TryFloats(fields, 6, 4, out var r)
                || !int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int snap))
                continue;
            window.Snap = snap >= 0 && snap <= (int)DashWindow.WindowSnap.BottomRight ? (DashWindow.WindowSnap)snap : DashWindow.WindowSnap.None;
            window.UserArranged = fields.Length > 10 && fields[10] == "1";
            window.RestoreBounds = window.Snap != DashWindow.WindowSnap.None ? new Rect(r[0], r[1], r[2], r[3]) : null;
            if (window.Snap != DashWindow.WindowSnap.None)
                SetBounds(window, SnapRect(window.Snap), exact: true);
            else
                SetBounds(window, new Rect(b[0], b[1], b[2], b[3]));
            placed.Add((window, fields[0][0]));
        }
        if (placed.Count == 0)
            return false;

        foreach (var (window, state) in placed)
        {
            if (state == 'o')
                Open(window, restoring: true);
            else if (state == 'm')
            {
                window.State = DashWindow.WindowState.Minimized;
                _stack.Add(window);
            }
        }
        var focus = WindowByKey(parts[1]);
        _focused = focus is { State: DashWindow.WindowState.Open } ? focus : TopOpen();
        if (_focused != null)
        {
            _stack.Remove(_focused);
            _stack.Add(_focused);
        }
        Restack();
        return true;
    }

    private static bool TryFloats(string[] fields, int start, int count, out float[] values)
    {
        values = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (!float.TryParse(fields[start + i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                return false;
        }
        return true;
    }

    private void Touch()
    {
        Revision++;
        if (PersistLayout && World != null)
            _saveAt = World.Time.TotalTime + SaveDelay;
    }

    // The apps out in the room, in the panel key's format.
    public string SerializePanels()
    {
        var sb = new StringBuilder("1");
        foreach (var window in _windows)
        {
            if (window.State == DashWindow.WindowState.Out && window.Popped is { IsDestroyed: false } panel && panel.TryDescribe(out var entry))
                sb.Append('|').Append(entry);
        }
        return sb.ToString();
    }

    // Something about a panel out in the room changed: written with the rest a moment later.
    public void NoteChange() => Touch();

    // Panels call this from their own update. With the dash closed this component does not run, and an
    // arrangement changed out in the room still has to reach the store.
    public void TickSave()
    {
        if (_saveAt >= 0 && World != null && World.Time.TotalTime >= _saveAt)
            Flush();
    }

    // Writes now if anything changed since the last write.
    public void Flush()
    {
        _saveAt = -1;
        if (!PersistLayout || _dash == null)
            return;
        var value = Serialize();
        if (value != _lastSaved)
        {
            _lastSaved = value;
            Lumora.Core.Settings.WriteValue(LayoutKey, value);
        }
        var panels = SerializePanels();
        if (panels != _lastPanels)
        {
            _lastPanels = panels;
            Lumora.Core.Settings.WriteValue(PanelsKey, panels);
        }
    }
}

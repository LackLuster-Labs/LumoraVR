// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// What the in-world readout says for a spawn from the inventory.
internal static class SpawnLocale
{
    public static readonly LocaleText Spawning = "Inventory.Readout.Spawning".AsLocale("Spawning");
    public static readonly LocaleText Spawned = "Inventory.Readout.Spawned".AsLocale("Spawned");
    public static readonly LocaleText InFront = "Inventory.Readout.InFront".AsLocale("In front of you");
    public static readonly LocaleText PuttingOn = "Inventory.Readout.PuttingOn".AsLocale("Putting on");
    public static readonly LocaleText PutOn = "Inventory.Readout.PutOn".AsLocale("Wearing it");
    public static readonly LocaleText Wearing = "Inventory.Readout.Wearing".AsLocale("It shows as soon as it has loaded");
    public static readonly LocaleText WouldNotEquip = "Inventory.Readout.WouldNotEquip".AsLocale("In front of you, but it would not go on");
}

// What you have saved, as folder trees you can shape: on the cloud, and on this machine until a new
// save has gone up, which the status line follows.
//
// Your own inventory, and one more root for every group you are in. The rail on the left lists the roots
// and, under the open one, the way down to the folder being looked at, any step a press away; its bottom
// holds the kind filters and the new folder and link buttons. The game's own library leads your own
// inventory as one gold folder, and things shared with you sit at the top of it in the share colour. One
// bar above the cards holds the way up, the two ways in from the world (what you hold, what you wear),
// search, sort and Move, and the actions for whatever is
// selected. A card is selected with one press and opened with a second press on the same card. Move lifts
// a thing as a transparent copy that follows the pointer; the next press on a folder card, on a step in
// the rail, or on Up puts it there. The folder open here is where every save lands, wherever it is made
// from. Nothing here touches the disk beyond the download cache. -xlinka
[ComponentCategory("Hidden")]
public sealed partial class InventoryScreen : WidgetScreen, IDashboardKeyInput, ITabbedScreen
{
    private const float BarHeight = 40f;
    private const float BarGap = DashTheme.Gap;
    private const float ControlTextInset = 12f;
    private const float HereMinWidth = 80f;
    private const float StatusHeight = 30f;
    // As many columns as cards of at least MinCardWidth fit, up to six: a narrow window gets fewer, wider
    // cards instead of six slivers.
    private const int MaxColumns = 6;
    private const float MinCardWidth = 118f;
    private const int PageSize = 36;   // six full rows at six across
    private const float CardSpacing = 10f;
    private const float ContentPad = 4f;
    private const int BufferRows = 1;
    private const float LaidOutFloor = 20f;
    private const float DoublePressSeconds = 0.45f;
    private const float ConfirmSeconds = 3f;
    private const float SyncWidth = 280f;
    private const int SyncChars = 46;

    private enum Sort { AToZ, ZToA, Newest, Oldest, ByType }
    private enum Show { All = 0, Worlds = 1, Items = 2, Avatars = 3 }
    private enum Field { None, Search, Dialog }
    private enum DialogKind { None, NewFolder, Rename, AddLink }

    private IInventorySource _source = new CloudInventorySource();

    private BrowserParts _parts = null!;
    private ThumbnailCache _thumbnails = null!;
    private InventoryFavourites? _favourites;
    private InventorySidebar _sidebar = null!;
    private Slot? _bar;
    private RectTransform? _barNav;
    private RectTransform? _barTools;
    private float _barNavMin;
    private float _barToolsMin;
    private float _barToolsPref;
    private int _barRows = 1;
    private Slot? _statusRow;
    private Text? _status;
    private Text? _syncLine;
    private long _listedRevision = -1;
    private RectTransform? _viewportRect;
    private RectTransform? _contentRect;
    private Slot? _contentSlot;
    private ScrollRect? _scroll;
    private DashScrollbar? _scrollbar;
    private InventoryMoveCatcher? _catcher;
    private InventoryGhost? _ghost;
    private Slot? _dialogRoot;
    private Text? _dialogTitle;
    private Text? _dialogText;
    private Text? _dialogError;
    private readonly List<InventoryCard> _pool = new();
    private readonly List<(RectTransform rect, InventoryPlace place)> _dropRects = new();
    private int _boundFirstRow = -1;
    private float _lastCardW = -1f;
    private int _columns = MaxColumns;

    private int Columns => _columns;

    private int _page;
    private Sort _sort = Sort.AToZ;
    private Show _show = Show.All;
    private string _search = string.Empty;
    private Field _focus = Field.None;
    private readonly List<InventoryEntry> _entries = new();
    private readonly List<InventoryEntry> _visible = new();
    private int _loadSerial;
    private bool _loading;
    private bool _busy;

    private string? _selectedId;
    private DateTime _lastPress;
    private string? _lastPressId;

    private bool _pickForMove;
    private InventoryEntry? _carried;
    private string? _dropTarget;
    private float _confirmDeleteLeft;
    // A folder move refused because a folder of its name is there already: the bar offers to merge the two.
    private (InventoryEntry Folder, InventoryPlace Target)? _mergeOffer;

    private DialogKind _dialog = DialogKind.None;
    private string _dialogBuffer = string.Empty;
    private string? _dialogProblem;

    private string? _notice;
    private color _noticeColor = DashTheme.TextDim;
    private bool _artDirty;
    // A picture handed to a card is still decoding for a few frames after its provider exists, so the cards
    // look again until it shows, for this long at most.
    private const float ArtPatience = 10f;
    private float _artWaitLeft;
    private readonly HashSet<string> _thumbnailsAsked = new(StringComparer.Ordinal);

    // NAMED STATES

    // Comma separated so a capture can stack them: "demo,select" looks at the demo tree with a card
    // selected, "demo,carry" mid-move.
    public bool ShowTab(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (name.Contains(','))
        {
            bool any = false;
            foreach (var part in name.Split(','))
                any |= ShowTab(part.Trim());
            return any;
        }
        switch (name.Trim().ToLowerInvariant())
        {
            case "demo":
                _source = new DemoInventorySource();
                _thumbnailsAsked.Clear();
                RefreshGroups();
                NavigateToRoot();
                return true;
            case "group":
                World?.RunInUpdates(60, () =>
                {
                    if (!IsDestroyed && _roots.Count > 1)
                        OpenRoot(1);
                });
                return true;
            case "share":
                World?.RunInUpdates(120, () =>
                {
                    if (IsDestroyed) return;
                    foreach (var entry in _visible)
                    {
                        if (entry.IsFolder && !entry.IsLink)
                        {
                            _selectedId = entry.Id;
                            OpenShare(entry);
                            return;
                        }
                    }
                });
                return true;
            case "select":
                World?.RunInUpdates(120, () =>
                {
                    if (IsDestroyed || _visible.Count == 0) return;
                    int pick = 0;
                    for (int i = 0; i < _visible.Count; i++)
                    {
                        if (!_visible[i].IsFolder) { pick = i; break; }
                    }
                    _selectedId = _visible[pick].Id;
                    RepaintCards();
                    RebuildBar();
                });
                return true;
            case "carry":
                World?.RunInUpdates(120, HarnessCarryNow);
                return true;
            default:
                return false;
        }
    }

    private void HarnessCarryNow()
    {
        if (IsDestroyed || _visible.Count == 0 || _ghost == null || _catcher == null)
            return;
        InventoryEntry? first = null;
        for (int i = 0; i < _visible.Count; i++)
        {
            if (!_visible[i].IsFolder) { first = _visible[i]; break; }
        }
        first ??= _visible[0];
        BeginCarry(first.Value, null);
        if (_pool.Count > 1 && _pool[1].Active)
        {
            var r = _pool[1].Rect.LocalComputeRect;
            var canvas = Slot.GetComponentInParents<Canvas>();
            var offset = canvas != null ? canvas.ScrollOffsetOf(_pool[1].Root) : float2.Zero;
            var seen = new float2(r.x + r.width * 0.5f + offset.x, r.y + r.height * 0.5f + offset.y);
            _ghost.MoveTo(canvas != null ? canvas.CanvasPointToLocal(_catcher.Slot, seen) : seen);
            if (_pool[1].Kind == InventoryEntryKind.Folder)
            {
                _dropTarget = _pool[1].Id;
                RepaintCards();
            }
        }
    }

    // LIFECYCLE

    protected override void OnShow()
    {
        base.OnShow();
        RefreshGroups();
        Reload();
        RecheckEmail();
    }

    public override void OnDestroy()
    {
        InventorySync.Changed -= OnSyncChanged;
        AccountEmail.Changed -= OnEmailChanged;
        Inventory.OpenRequested -= OnOpenRequested;
        _favourites?.Dispose();
        _thumbnails?.Clear();
        base.OnDestroy();
    }

    protected override void BuildContent(UIBuilder builder)
    {
        ResolveDashboard();
        _parts = new BrowserParts
        {
            Regular = _dashboard?.Font.Target,
            Semibold = _dashboard?.FontSemibold.Target ?? _dashboard?.Font.Target,
            Bold = _dashboard?.FontBold.Target ?? _dashboard?.Font.Target,
        };

        var root = builder.Current;
        var col = root.AttachComponent<VerticalLayout>();
        col.Spacing.Value = DashTheme.Gap;
        col.PaddingLeft.Value = DashTheme.GapLarge;
        col.PaddingRight.Value = DashTheme.GapLarge;
        col.PaddingTop.Value = DashTheme.GapLarge;
        col.PaddingBottom.Value = DashTheme.GapLarge;
        col.ForceExpandWidth.Value = true;
        col.ForceExpandHeight.Value = false;

        var thumbHost = Slot.AddSlot("Thumbnails");
        _thumbnails = new ThumbnailCache(this, thumbHost);

        // Two strips that FitBar lays side by side or stacks: where you are, and what can be done here.
        _bar = root.AddSlot("Bar");
        _bar.AttachComponent<RectTransform>();
        BrowserParts.Height(_bar, BarHeight);

        Rule(root, DashTheme.Divider);
        BuildVerifyRow(root);

        var body = root.AddSlot("Body");
        body.AttachComponent<RectTransform>();
        var bodyElement = body.AttachComponent<LayoutElement>();
        bodyElement.FlexibleWidth.Value = 1f;
        bodyElement.FlexibleHeight.Value = 1f;
        bodyElement.MinHeight.Value = 200f;
        var bodyLayout = body.AttachComponent<HorizontalLayout>();
        bodyLayout.Spacing.Value = DashTheme.GapLarge;
        bodyLayout.ForceExpandWidth.Value = false;
        bodyLayout.ForceExpandHeight.Value = true;

        _sidebar = new InventorySidebar(_parts, body, () => OpenDialog(DialogKind.NewFolder), () => OpenDialog(DialogKind.AddLink));
        _sidebar.StepPicked += NavigateBackTo;
        _sidebar.RootPicked += OpenRoot;
        _sidebar.RequestsPicked += OpenRequests;
        _sidebar.FilterPicked += id => { _show = (Show)id; _sidebar.SelectFilter(id); _page = 0; ApplyView(); };
        _sidebar.AddFilter((int)Show.All, "All");
        _sidebar.AddFilter((int)Show.Worlds, "Worlds");
        _sidebar.AddFilter((int)Show.Items, "Items");
        _sidebar.AddFilter((int)Show.Avatars, "Avatars");
        _sidebar.SelectFilter((int)_show);
        RebuildRoots();

        // The rail and the grid had nothing between them, so the screen read as one wide field of
        // stuff with a few pills floating at the left. A one-unit column between them is enough to
        // say these are two areas, and being IN the row layout it gets a real rect. -xlinka
        var railEdge = body.AddSlot("RailEdge");
        railEdge.AttachComponent<RectTransform>();
        var railEdgeElement = railEdge.AttachComponent<LayoutElement>();
        railEdgeElement.MinWidth.Value = 1f;
        railEdgeElement.PreferredWidth.Value = 1f;
        railEdgeElement.FlexibleWidth.Value = 0f;
        railEdgeElement.FlexibleHeight.Value = 1f;
        railEdge.AttachComponent<Image>().Tint.Value = DashTheme.OutlineStrong;

        BuildViewport(body);

        Rule(root, DashTheme.Divider);

        _statusRow = root.AddSlot("Status");
        _statusRow.AttachComponent<RectTransform>();
        BrowserParts.Height(_statusRow, StatusHeight);
        var statusLayout = _statusRow.AttachComponent<HorizontalLayout>();
        statusLayout.Spacing.Value = 6f;
        statusLayout.ForceExpandWidth.Value = false;
        statusLayout.ForceExpandHeight.Value = true;

        // The catcher and the ghost come last so they sit above everything in the hit scan and the draw
        // order. Both stay inactive until a move starts.
        var catcherSlot = root.AddSlot("MoveCatcher");
        BrowserParts.Fill(catcherSlot.AttachComponent<RectTransform>());
        catcherSlot.AttachComponent<IgnoreLayout>();
        _catcher = catcherSlot.AttachComponent<InventoryMoveCatcher>();
        _catcher.Moved = OnCarryMoved;
        _catcher.Dropped = OnCarryDropped;
        catcherSlot.ActiveSelf.Value = false;

        var ghostHost = root.AddSlot("GhostHost");
        var ghostRect = ghostHost.AttachComponent<RectTransform>();
        BrowserParts.Fill(ghostRect);
        ghostHost.AttachComponent<IgnoreLayout>();
        _ghost = new InventoryGhost(_parts, ghostHost, ghostRect);

        BuildDialog(root);
        BuildShareDialog(root);
        BuildRequestsPanel(root);
        InventorySync.Changed += OnSyncChanged;
        Inventory.OpenRequested += OnOpenRequested;
        _favourites = new InventoryFavourites(_parts, OnUi, () => { RepaintCards(); RebuildBar(); });
        RebuildBar();
        RefreshGroups();
        Reload();
    }

    private void BuildViewport(Slot body)
    {
        var host = body.AddSlot("Grid");
        host.AttachComponent<RectTransform>();
        var hostElement = host.AttachComponent<LayoutElement>();
        hostElement.FlexibleWidth.Value = 1f;
        hostElement.FlexibleHeight.Value = 1f;

        var viewport = host.AddSlot("Viewport");
        _viewportRect = viewport.AttachComponent<RectTransform>();
        _viewportRect.AnchorMin.Value = float2.Zero;
        _viewportRect.AnchorMax.Value = float2.One;
        _viewportRect.OffsetMin.Value = float2.Zero;
        _viewportRect.OffsetMax.Value = new float2(-(DashScrollbar.Width + 8f), 0f);
        viewport.AttachComponent<Mask>();
        _scroll = viewport.AttachComponent<ScrollRect>();
        _scroll.ScrollSensitivity.Value = new float2(1f, 1f);
        // Cards open, spawn and pick up for a move on a press; a drag down the grid starts on one.
        _scroll.RowsActOnRelease.Value = true;
        _scroll.ScrollChanged += (_, _) => { _scrollbar?.Refresh(); RelayoutVirtual(force: false); };

        _contentSlot = viewport.AddSlot("Content");
        _contentRect = _contentSlot.AttachComponent<RectTransform>();
        _contentRect.AnchorMin.Value = new float2(0f, 1f);
        _contentRect.AnchorMax.Value = new float2(1f, 1f);
        _contentRect.OffsetMin.Value = new float2(0f, -InventoryCard.Height);
        _contentRect.OffsetMax.Value = float2.Zero;
        _scroll.Content.Target = _contentRect;

        _scrollbar = DashScrollbar.OnRightEdge(host, 0f, 0f, 0f);
        _scrollbar.Bind(_scroll, _viewportRect, _contentRect, MarkDirty);
    }

    // Results from the cloud arrive on a task thread; everything that touches the tree runs here.
    private void OnUi(Action action)
    {
        var world = World;
        if (world == null || IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!IsDestroyed)
                action();
        });
    }

    private void Await<T>(Task<T> task, Action<T> then, Action<string>? failed = null)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
                OnUi(() => then(t.Result));
            else
                OnUi(() =>
                {
                    _busy = false;
                    _loading = false;
                    string why = t.Exception?.GetBaseException().Message ?? "That did not work.";
                    SetStatus(why, DashTheme.Warning);
                    failed?.Invoke(why);
                });
        });
    }

    // THE BAR

    private void RebuildBar()
    {
        var bar = _bar;
        if (bar == null || bar.IsDestroyed)
            return;
        bar.DestroyChildren();
        _dropRects.Clear();
        var nav = BarStrip(bar, "Nav");
        var tools = BarStrip(bar, "Tools");
        _barNav = nav.GetComponent<RectTransform>();
        _barTools = tools.GetComponent<RectTransform>();
        FillBar(nav, tools);
        _barNavMin = StripWidth(nav, preferred: false);
        _barToolsMin = StripWidth(tools, preferred: false);
        _barToolsPref = StripWidth(tools, preferred: true);
        FitBar();
        RebuildStatusRow();
        MarkDirty();
    }

    private void FillBar(Slot nav, Slot tools)
    {
        bool signedIn = _source.SignedIn;
        bool carrying = _carried.HasValue;
        bool canUp = _chain.Count > 1;
        bool canChange = CanEditHere;
        var up = AddBarButton(nav, "Up", canUp ? TabFill : ControlFill, 56f, GoUp);
        if (canUp)
            _dropRects.Add((up.GetComponent<RectTransform>()!, _chain[_chain.Count - 2]));

        var here = nav.AddSlot("Here");
        here.AttachComponent<RectTransform>();
        var hereElement = here.AttachComponent<LayoutElement>();
        hereElement.MinWidth.Value = HereMinWidth;
        hereElement.FlexibleWidth.Value = 1f;
        var hereText = _parts.Label(here, BrowserParts.Truncate(FolderName, 40), DashTheme.FontBody, HereInk(), BrowserParts.Weight.Semibold);
        hereText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        AddPlaceChip(nav);

        if (!signedIn)
            return;

        if (carrying)
        {
            var carried = _carried!.Value;
            var hint = tools.AddSlot("Hint");
            hint.AttachComponent<RectTransform>();
            var hintElement = BrowserParts.Size(hint, 400f, BarHeight);
            hintElement.MinWidth.Value = 120f;
            hintElement.FlexibleWidth.Value = 1f;
            var hintText = _parts.Label(hint, $"Moving \"{BrowserParts.Truncate(carried.Name, 22)}\": press a folder, a step in the rail, or Up",
                DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);
            hintText.HorizontalAlignment.Value = TextHorizontalAlignment.Right;
            AddBarButton(tools, "Cancel", TabFill, 84f, CancelCarry);
            return;
        }

        if (_mergeOffer is { } offer)
        {
            AddMergeOffer(tools, offer.Folder, offer.Target);
            return;
        }

        // The right hand group changes with the selection: with nothing selected it is the two ways
        // in from the world, with something selected it is what can be done to that thing.
        var selected = SelectedEntry();
        if (selected.HasValue)
        {
            var entry = selected.Value;
            AddEntryActions(tools, entry, canChange);
            _favourites?.AddButtons(tools, entry, AddBarButton, (text, tint) => SetStatus(text, tint));
        }
        else if (_search.Length == 0)
        {
            // Saves made from here land here; where they cannot, they land at the top of your own inventory,
            // and the buttons say so rather than vanish. -xlinka
            AddBarButton(tools, canChange ? "Save held" : "Save held to mine", TabFill, canChange ? 96f : 150f, SaveHeld);
            AddBarButton(tools, canChange ? "Save avatar" : "Save avatar to mine", TabFill, canChange ? 108f : 162f, SaveAvatar);
            // the library is no root of the side tree, so its delete requests are reached from in here
            if (Here.IsLibrary && RankIn(Inventory.LibraryId) <= 2 && _requestCounts.TryGetValue(Inventory.LibraryId, out int asked) && asked > 0)
                AddBarButton(tools, InventoryLibraryLocale.Requests(asked).Resolve(), ControlFill, 160f, () => OpenRequests(Inventory.LibraryId, LibraryName));
        }

        BuildSearchWell(tools);
        AddBarButton(tools, SortLabel(_sort), TabFill, 80f, CycleSort);
        if (canChange)
            AddBarButton(tools, _pickForMove ? "Pick" : "Move", _pickForMove ? AccentColor : TabFill, 66f, StartMove);
    }

    // What can be done to the selected thing: change it where this account may, share it where it may share,
    // take a shared thing back out of its inventory, and open, spawn, wear or orb it wherever it is.
    private void AddEntryActions(Slot tools, in InventoryEntry selected, bool canChange)
    {
        var entry = selected;
        bool reachable = entry.LinkAccess != InventoryAccess.None;
        if (entry.IsLibrary)
        {
            AddBarButton(tools, "Open", AccentColor, 76f, () => NavigateInto(entry));
            return;
        }
        if (entry.IsLink)
        {
            if (entry.Link!.Via != Inventory.ShareGroup)
                AddBarButton(tools, _confirmDeleteLeft > 0f ? "Confirm" : "Remove",
                    _confirmDeleteLeft > 0f ? DashTheme.Negative : TabFill, 84f, DeleteSelected);
            if (entry.IsKeptLink)
                AddBarButton(tools, "Rename", TabFill, 80f, () => OpenDialog(DialogKind.Rename));
            if (reachable)
                AddBarButton(tools, "Copy link", TabFill, 96f, () => CopyLink(entry));
        }
        else
        {
            if (canChange)
                AddBarButton(tools, "Rename", TabFill, 80f, () => OpenDialog(DialogKind.Rename));
            AddDeleteAction(tools, entry);
            if (_canShare)
            {
                AddBarButton(tools, entry.IsShared ? "Sharing" : "Share", entry.IsShared ? ShareFill : TabFill, 84f, () => OpenShare(entry));
                if (entry.IsShared)
                    AddBarButton(tools, "Spawn link", TabFill, 104f, () => SpawnLinkCard(entry));
            }
        }
        if (!reachable)
            return;
        switch (entry.Kind)
        {
            case InventoryEntryKind.Folder:
                AddBarButton(tools, "Open", AccentColor, 76f, () => NavigateInto(entry));
                break;
            case InventoryEntryKind.Item:
                if (entry.IsAvatar)
                    AddBarButton(tools, "Equip", TabFill, 70f, () => EquipItem(entry));
                AddBarButton(tools, "Spawn", AccentColor, 76f, () => SpawnItem(entry));
                break;
            case InventoryEntryKind.World:
                AddBarButton(tools, "Orb", TabFill, 60f, () => SpawnWorldOrb(entry));
                AddBarButton(tools, "Open", AccentColor, 76f, () => OpenWorld(entry));
                break;
        }
    }

    // Deleting follows the inventory: anything of yours, anything a write share lets you change, and in a group
    // whatever a moderator or above presses. A group's builder asks instead, and while the request waits can take
    // it back; a locked group still lets moderators clear things out. -xlinka
    private void AddDeleteAction(Slot tools, InventoryEntry entry)
    {
        bool confirming = _confirmDeleteLeft > 0f;
        bool inGroup = entry.OwnerId != null && entry.OwnerId == Here.OwnerId && Here.IsGroup;
        if (inGroup && RankIn(entry.OwnerId) == 3)
        {
            if (entry.DeleteRequest is { } asked)
            {
                bool mine = asked.ByName == _myName;
                AddBarButton(tools, mine ? "Take back request" : "Delete asked", mine ? TabFill : ControlFill, mine ? 150f : 116f,
                    mine ? () => WithdrawRequest(entry, asked) : (Action)(() => SetStatus($"{asked.ByName} asked for this to be deleted.", DashTheme.TextDim)));
                return;
            }
            AddBarButton(tools, confirming ? "Confirm" : "Ask to delete", confirming ? DashTheme.Warning : TabFill, confirming ? 84f : 120f,
                DeleteSelected);
            return;
        }
        if (!_access.CanChange())
            return;
        AddBarButton(tools, confirming ? "Confirm" : "Delete", confirming ? DashTheme.Negative : TabFill, 76f, DeleteSelected);
    }

    private void WithdrawRequest(InventoryEntry entry, Lumora.Nexus.Cloud.Cdn.InventoryDeleteMark asked)
        => RunAction(System.Threading.Tasks.Task.Run(() => Inventory.DropDeleteRequestAsync(entry.OwnerId!, asked.Id, entry.Name, mine: true)));

    // A link card for this shared folder or item, in front of you in the world you are in.
    private void SpawnLinkCard(InventoryEntry entry)
    {
        var world = FocusedWorld;
        if (world == null)
        {
            SetStatus("The world manager isn't up yet.", DashTheme.Warning);
            return;
        }
        string owner = entry.OwnerId == null ? _myName ?? "you" : Here.OwnerName.Length > 0 ? Here.OwnerName : entry.OwnerId;
        if (ShareCard.Spawn(world, entry, owner, out var refusal))
            SetStatus($"A link card for \"{entry.Name}\" is in front of you. Whoever it is shared with can open it or keep it.", DashTheme.TextDim);
        else
            SetStatus($"Couldn't place a link card: {refusal}.", DashTheme.Warning);
    }

    private Slot AddBarButton(Slot row, string label, color fill, float width, Action onClick)
    {
        var cell = AddInlineButton(row, label, fill, width, onClick);
        var element = cell.GetComponent<LayoutElement>()!;
        element.MinWidth.Value = width;
        element.MinHeight.Value = BarHeight;
        element.PreferredHeight.Value = BarHeight;
        var textRect = cell.FindChild("Label", recursive: false)!.GetComponent<RectTransform>()!;
        SettingsUI.SetOffsets(textRect, new float2(ControlTextInset, 0f), new float2(-ControlTextInset, 0f));
        return cell;
    }

    private static Slot BarStrip(Slot bar, string name)
    {
        var strip = bar.AddSlot(name);
        strip.AttachComponent<RectTransform>();
        if (name == "Tools")
        {
            var flow = strip.AttachComponent<WrapLayout>();
            flow.Spacing.Value = BarGap;
            flow.LineSpacing.Value = BarGap;
            return strip;
        }
        var layout = strip.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = BarGap;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;
        return strip;
    }

    private static float StripWidth(Slot strip, bool preferred)
    {
        float sum = 0f;
        int count = 0;
        foreach (var child in strip.Children)
        {
            var element = child.GetComponent<LayoutElement>();
            if (element == null)
                continue;
            sum += preferred ? MathF.Max(element.MinWidth.Value, element.PreferredWidth.Value) : element.MinWidth.Value;
            count++;
        }
        return count > 1 ? sum + BarGap * (count - 1) : sum;
    }

    // The selection adds rename/delete/equip/spawn controls. Let that strip wrap below the folder
    // when necessary, instead of clipping actions beyond the edge of a narrow inventory window.
    private void FitBar()
    {
        var bar = _bar;
        if (bar == null || bar.IsDestroyed || _barNav == null || _barTools == null)
            return;
        float width = bar.GetComponent<RectTransform>()?.LocalComputeRect.width ?? 0f;
        if (width <= 0f)
            return;
        bool hasTools = _barToolsMin > 0f;
        bool inline = !hasTools || width >= _barNavMin + BarGap + _barToolsPref;
        int toolRows = 1;
        float used = 0f;
        if (!inline)
        {
            foreach (var child in _barTools.Slot.Children)
            {
                if (!child.ActiveSelf.Value || child.GetComponent<LayoutElement>() is not { } element)
                    continue;
                float childWidth = MathF.Min(width, MathF.Max(element.MinWidth.Value, element.PreferredWidth.Value));
                if (used > 0f && used + BarGap + childWidth > width)
                {
                    toolRows++;
                    used = 0f;
                }
                used += (used > 0f ? BarGap : 0f) + childWidth;
            }
        }
        int rows = inline ? 1 : toolRows + 1;
        if (rows != _barRows)
        {
            _barRows = rows;
            BrowserParts.Height(bar, BarHeight * rows + BarGap * (rows - 1));
        }
        if (rows == 1)
        {
            float toolsWidth = hasTools ? MathF.Min(_barToolsPref, width - _barNavMin - BarGap) : 0f;
            SettingsUI.SetAnchors(_barNav, float2.Zero, float2.One);
            SettingsUI.SetOffsets(_barNav, float2.Zero, new float2(-(hasTools ? toolsWidth + BarGap : 0f), 0f));
            SettingsUI.SetAnchors(_barTools, new float2(1f, 0f), float2.One);
            SettingsUI.SetOffsets(_barTools, new float2(-toolsWidth, 0f), float2.Zero);
        }
        else
        {
            SettingsUI.SetAnchors(_barNav, new float2(0f, 1f), float2.One);
            SettingsUI.SetOffsets(_barNav, new float2(0f, -BarHeight), float2.Zero);
            SettingsUI.SetAnchors(_barTools, float2.Zero, new float2(1f, 0f));
            SettingsUI.SetOffsets(_barTools, float2.Zero, new float2(0f, BarHeight * toolRows + BarGap * (toolRows - 1)));
        }
    }

    private void BuildSearchWell(Slot bar)
    {
        var cell = bar.AddSlot("SearchWell");
        cell.AttachComponent<RectTransform>();
        var element = cell.AttachComponent<LayoutElement>();
        element.MinWidth.Value = 120f;
        element.PreferredWidth.Value = 180f;
        element.FlexibleWidth.Value = 1f;
        element.FlexibleHeight.Value = 1f;
        element.MinHeight.Value = BarHeight;
        element.PreferredHeight.Value = BarHeight;
        ApplyRoundedPanel(cell, ControlFill, _focus == Field.Search ? AccentColor : RowBorder);
        var button = cell.AttachComponent<Button>();
        button.Clicked += (_, _) => { _focus = _focus == Field.Search ? Field.None : Field.Search; RebuildBar(); };
        bool empty = _search.Length == 0;
        _parts.Label(cell, empty ? (_focus == Field.Search ? "Type to search" : "Search") : _search,
            DashTheme.FontBody, empty ? TextDim : TextPrimary, BrowserParts.Weight.Regular,
            TextHorizontalAlignment.Left, padLeft: ControlTextInset, padRight: ControlTextInset);
    }

    // Left: the last message, or the selection's facts, or the listing. Right: how far the saves made
    // here have got going up, then the pages.
    private void RebuildStatusRow()
    {
        var row = _statusRow;
        if (row == null || row.IsDestroyed)
            return;
        row.DestroyChildren();

        var selected = SelectedEntry();
        string? offer = _mergeOffer is { } pending ? InventoryFolderLocale.MergeOffer(pending.Folder.Name, pending.Target.Name).Resolve() : null;
        string? broken = _notice == null && offer == null && !_loading && selected is { IsBroken: true } lost ? DescribeBroken(lost) : null;
        string text = _notice ?? offer ?? broken ?? (_loading ? "Loading…" : selected.HasValue ? DescribeEntry(selected.Value) : DescribeListing());
        var tint = _notice != null ? _noticeColor
            : offer != null || broken != null || (PlaceWarns() && !selected.HasValue) ? DashTheme.Warning : DashTheme.TextDim;
        _notice = null;

        var textSlot = row.AddSlot("Text");
        textSlot.AttachComponent<RectTransform>();
        textSlot.AttachComponent<LayoutElement>().FlexibleWidth.Value = 1f;
        _status = _parts.Label(textSlot, text, DashTheme.FontSmall, tint, BrowserParts.Weight.Regular);
        _status.HorizontalAlignment.Value = TextHorizontalAlignment.Left;

        _syncLine = null;
        if (_source.SyncStatus.HasValue)
        {
            var syncSlot = row.AddSlot("Sync");
            syncSlot.AttachComponent<RectTransform>();
            BrowserParts.Size(syncSlot, SyncWidth, StatusHeight);
            _syncLine = _parts.Label(syncSlot, string.Empty, DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);
            _syncLine.HorizontalAlignment.Value = TextHorizontalAlignment.Right;
            ShowSyncState();
        }

        int pages = PageCount();
        if (pages <= 1)
            return;
        AddInlineButton(row, "‹", _page > 0 ? TabFill : ControlFill, 40f, PrevPage);
        var pageSlot = row.AddSlot("Page");
        pageSlot.AttachComponent<RectTransform>();
        BrowserParts.Size(pageSlot, 72f, StatusHeight);
        _parts.Label(pageSlot, $"{_page + 1} / {pages}", DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);
        AddInlineButton(row, "›", _page < pages - 1 ? TabFill : ControlFill, 40f, NextPage);
    }

    private void ShowSyncState()
    {
        if (_syncLine == null || _syncLine.IsDestroyed)
            return;
        var state = _source.SyncStatus;
        BrowserParts.SetText(_syncLine, state.HasValue ? BrowserParts.Truncate(InventorySync.Describe(state.Value), SyncChars) : string.Empty);
        BrowserParts.SetColor(_syncLine.Color, state is { IsProblem: true } ? DashTheme.Warning : DashTheme.TextDim);
    }

    // News from the sync comes off its own thread. The line is redrawn here, and the folder read again
    // when a save came, went or moved, or the account changed, while the screen is up to show it.
    private void OnSyncChanged() => OnUi(() =>
    {
        if (InventorySync.Revision != _listedRevision && Slot.ActiveSelf.Value)
        {
            Reload();
            return;
        }
        ShowSyncState();
        if (Slot.ActiveSelf.Value && InventorySync.TakeNotice() is { } note)
            SetStatus(note, DashTheme.Warning);
    });

    private void SetStatus(string text, in color tint)
    {
        _notice = text;
        _noticeColor = tint;
        if (_status != null && !_status.IsDestroyed)
        {
            BrowserParts.SetText(_status, text);
            BrowserParts.SetColor(_status.Color, tint);
        }
    }

    // A save whose parts the server lost says what to do about it: whoever can save over it is told how, anyone
    // else who can.
    private string DescribeBroken(in InventoryEntry entry)
    {
        int count = entry.MissingParts!.Count;
        bool canFix = !entry.IsLink && (entry.OwnerId == null || _access.CanChange());
        return (canFix ? InventoryPartsLocale.FixOwn(count) : InventoryPartsLocale.FixOther(count)).Resolve();
    }

    private static string DescribeEntry(in InventoryEntry entry)
    {
        if (entry.IsLibrary)
            return InventoryLibraryLocale.Describe(entry.Name, entry.ChildCount).Resolve();
        string from = entry.IsLink
            ? entry.LinkAccess == InventoryAccess.None
                ? $" · {Inventory.OwnerLabel(entry)} no longer shares it"
                : $" · shared by {Inventory.OwnerLabel(entry)}{(entry.Link!.Via == Inventory.ShareGroup ? $" with {entry.Link.ViaName}" : string.Empty)}"
            : entry.IsPublic ? " · anyone with the link can see it"
            : entry.ShareCount > 0 ? $" · shared with {(entry.ShareCount == 1 ? "1" : entry.ShareCount.ToString())}"
            : string.Empty;
        if (entry.IsFolder)
            return $"{entry.Name} · folder · {(entry.ChildCount == 1 ? "1 thing inside" : $"{entry.ChildCount} things inside")}{from}"
                + (entry.DeleteRequest is { } folderAsked ? $" · {folderAsked.ByName} asked to delete it" : string.Empty);
        string kind = entry.Kind == InventoryEntryKind.World ? "world" : entry.IsAvatar ? "avatar" : entry.IsImage ? "image" : "item";
        string by = string.IsNullOrEmpty(entry.SavedByName) ? string.Empty : $" · saved by {entry.SavedByName}";
        if (entry.DeleteRequest is { } asked)
            by += $" · {asked.ByName} asked to delete it";
        return $"{entry.Name} · {kind} · {entry.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Inventory.FormatBytes(entry.SizeBytes)}{by}{from}";
    }

    private string DescribeListing()
    {
        if (!_source.SignedIn)
            return "Sign in with the profile widget on the workspace to use your inventory.";
        int folders = 0, worlds = 0, items = 0;
        foreach (var e in _entries)
        {
            if (e.IsFolder) folders++;
            else if (e.Kind == InventoryEntryKind.World) worlds++;
            else items++;
        }
        if (_search.Length > 0)
            return $"{_entries.Count} match \"{_search}\"";
        string where = PlaceLine();
        if (folders == 0 && worlds == 0 && items == 0)
            return _chain.Count == 1 && Here.IsOwn
                ? "Nothing saved yet. Save what you hold or wear, or a world from the Session tab."
                : "This folder is empty." + where;
        var parts = new List<string>(3);
        if (folders > 0) parts.Add(folders == 1 ? "1 folder" : $"{folders} folders");
        if (items > 0) parts.Add(items == 1 ? "1 item" : $"{items} items");
        if (worlds > 0) parts.Add(worlds == 1 ? "1 world" : $"{worlds} worlds");
        return string.Join(" · ", parts) + where;
    }

    private static string SortLabel(Sort sort) => sort switch
    {
        Sort.AToZ => "A to Z",
        Sort.ZToA => "Z to A",
        Sort.Newest => "Newest",
        Sort.Oldest => "Oldest",
        _ => "By type",
    };

    private void CycleSort()
    {
        _sort = _sort switch
        {
            Sort.AToZ => Sort.ZToA,
            Sort.ZToA => Sort.Newest,
            Sort.Newest => Sort.Oldest,
            Sort.Oldest => Sort.ByType,
            _ => Sort.AToZ,
        };
        _page = 0;
        ApplyView();
    }

    private bool Passes(in InventoryEntry entry) => _show switch
    {
        Show.Worlds => entry.IsFolder || entry.Kind == InventoryEntryKind.World,
        Show.Items => entry.IsFolder || (entry.Kind == InventoryEntryKind.Item && !entry.IsAvatar),
        Show.Avatars => entry.IsFolder || entry.IsAvatar,
        _ => true,
    };

    // LOADING

    private void Reload()
    {
        if (_contentSlot == null)
            return;
        if (_source is CloudInventorySource && AccountMoved())
            return;
        int serial = ++_loadSerial;
        _listedRevision = InventorySync.Revision;
        if (!_source.SignedIn)
        {
            _entries.Clear();
            _loading = false;
            ApplyView();
            return;
        }
        _loading = true;
        RebuildStatusRow();
        // Search runs over the open root's inventory: your own, or the group's.
        var task = _search.Length > 0 ? _source.Search(_search, _chain[0].OwnerId) : _source.List(Here);
        bool searching = _search.Length > 0;
        Await(task, listing =>
        {
            if (serial != _loadSerial)
                return;
            _loading = false;
            _entries.Clear();
            _entries.AddRange(listing.Entries);
            if (!searching)
                Listed(listing);
            if (listing.Problem != null)
            {
                _notice = listing.Problem;
                _noticeColor = DashTheme.Warning;
            }
            else if (_source is CloudInventorySource && InventorySync.TakeNotice() is { } note)
            {
                _notice = note;
                _noticeColor = DashTheme.Warning;
            }
            ApplyView();
        });
    }

    // Filter, sort, page and draw what is loaded. Cheap enough to run on every sort or filter press.
    private void ApplyView()
    {
        _visible.Clear();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (Passes(_entries[i]))
                _visible.Add(_entries[i]);
        }
        _visible.Sort(CompareEntries);

        if (_selectedId != null && !_visible.Exists(e => e.Id == _selectedId))
            _selectedId = null;

        int worlds = 0, items = 0, avatars = 0;
        foreach (var e in _entries)
        {
            if (e.Kind == InventoryEntryKind.World) worlds++;
            else if (e.IsAvatar) avatars++;
            else if (e.Kind == InventoryEntryKind.Item) items++;
        }
        _sidebar?.SetCount((int)Show.All, _entries.Count);
        _sidebar?.SetCount((int)Show.Worlds, worlds);
        _sidebar?.SetCount((int)Show.Items, items);
        _sidebar?.SetCount((int)Show.Avatars, avatars);

        if (_page > PageCount() - 1) _page = PageCount() - 1;
        if (_page < 0) _page = 0;
        FitContentHeight();

        if (_scroll != null)
            _scroll.AbsolutePosition = float2.Zero;
        _boundFirstRow = -1;
        RelayoutVirtual(force: true);
        RebuildBar();
        _scrollbar?.Refresh();
        World?.RunInUpdates(2, () => _scrollbar?.Refresh());
    }

    // A hairline row. It has to be a row in the column, not a line pinned inside the thing above it:
    // a pinned child resolves against its parent's AUTHORED rect, and a parent whose rect comes from a
    // layout still carries the RectTransform default. -xlinka
    private static void Rule(Slot column, in color tint)
    {
        var rule = column.AddSlot("Rule");
        rule.AttachComponent<RectTransform>();
        BrowserParts.Height(rule, 1f);
        rule.AttachComponent<Image>().Tint.Value = tint;
    }

    private int CompareEntries(InventoryEntry a, InventoryEntry b)
    {
        if (a.IsLibrary != b.IsLibrary)
            return a.IsLibrary ? -1 : 1;
        if (a.IsFolder != b.IsFolder)
            return a.IsFolder ? -1 : 1;
        switch (_sort)
        {
            case Sort.Newest:
            {
                int byTime = b.ModifiedUtc.CompareTo(a.ModifiedUtc);
                if (byTime != 0) return byTime;
                break;
            }
            case Sort.Oldest:
            {
                int byTime = a.ModifiedUtc.CompareTo(b.ModifiedUtc);
                if (byTime != 0) return byTime;
                break;
            }
            case Sort.ByType:
            {
                int byKind = TypeRank(a).CompareTo(TypeRank(b));
                if (byKind != 0) return byKind;
                break;
            }
            case Sort.ZToA:
                return string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
        }
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int TypeRank(in InventoryEntry entry)
    {
        if (entry.IsFolder) return 0;
        if (entry.Kind == InventoryEntryKind.World) return 1;
        return entry.IsAvatar ? 2 : 3;
    }

    private int PageCount() => System.Math.Max(1, (_visible.Count + PageSize - 1) / PageSize);
    private int PageStart => _page * PageSize;
    private int PageLength => System.Math.Min(PageSize, System.Math.Max(0, _visible.Count - PageStart));

    private void PrevPage()
    {
        if (_page <= 0) return;
        _page--;
        _selectedId = null;
        ApplyView();
    }

    private void NextPage()
    {
        if (_page >= PageCount() - 1) return;
        _page++;
        _selectedId = null;
        ApplyView();
    }

    // THE GRID

    private void FitContentHeight()
    {
        int rows = (PageLength + Columns - 1) / Columns;
        if (rows < 1) rows = 1;
        SetContentHeight(rows * InventoryCard.Height + (rows - 1) * CardSpacing + ContentPad * 2f);
    }

    private void SetContentHeight(float height)
    {
        if (_contentRect != null)
            _contentRect.OffsetMin.Value = new float2(0f, -height);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        FitBar();
        _sidebar?.FitTree();
        _sidebar?.Tick();
        RelayoutVirtual(force: false);
        if (_confirmDeleteLeft > 0f)
        {
            _confirmDeleteLeft -= delta;
            if (_confirmDeleteLeft <= 0f)
            {
                _confirmDeleteLeft = 0f;
                RebuildBar();
            }
        }
        if (_artDirty)
        {
            _artDirty = false;
            bool waiting = false;
            for (int i = 0; i < _pool.Count; i++)
            {
                var card = _pool[i];
                if (!card.Active || card.Kind == InventoryEntryKind.Folder)
                    continue;
                var entry = EntryById(card.Id);
                if (entry.HasValue && !card.ApplyArt(ThumbnailFor(entry.Value)))
                    waiting = true;
            }
            _artWaitLeft -= delta;
            if (waiting && _artWaitLeft > 0f)
                _artDirty = true;
        }
    }

    private void RelayoutVirtual(bool force)
    {
        if (_contentSlot == null || _contentRect == null || _viewportRect == null || _scroll == null)
            return;
        float viewportH = _viewportRect.LocalComputeRect.height;
        float contentW = _contentRect.LocalComputeRect.width;
        if (viewportH <= LaidOutFloor || contentW <= 0f)
            return;

        int columns = System.Math.Clamp((int)MathF.Floor((contentW - 2f * ContentPad + CardSpacing) / (MinCardWidth + CardSpacing)), 1, MaxColumns);
        if (columns != _columns)
        {
            _columns = columns;
            FitContentHeight();
            force = true;
        }

        float rowStride = InventoryCard.Height + CardSpacing;
        int visibleRows = (int)MathF.Ceiling(viewportH / rowStride) + BufferRows;
        int poolCount = System.Math.Max(1, visibleRows * Columns);
        float cardW = (contentW - 2f * ContentPad - (Columns - 1) * CardSpacing) / Columns;
        if (cardW <= 0f)
            return;
        EnsurePool(poolCount, cardW);

        if (MathF.Abs(cardW - _lastCardW) > 0.5f)
        {
            force = true;
            _lastCardW = cardW;
        }

        float scrollY = _scroll.AbsolutePosition.y;
        int firstRow = (int)MathF.Floor((scrollY - ContentPad) / rowStride);
        if (firstRow < 0) firstRow = 0;
        if (!force && firstRow == _boundFirstRow)
            return;
        if (force)
        {
            for (int i = 0; i < _pool.Count; i++)
                _pool[i].EntryIndex = -1;
        }
        _boundFirstRow = firstRow;

        int start = firstRow * Columns;
        int end = System.Math.Min(PageLength, (firstRow + visibleRows) * Columns);
        for (int e = start; e < end; e++)
        {
            var card = _pool[e % poolCount];
            if (card.EntryIndex == e && card.Active)
                continue;
            var entry = _visible[PageStart + e];
            int row = e / Columns;
            int colIndex = e % Columns;
            card.Place(ContentPad + colIndex * (cardW + CardSpacing), ContentPad + row * rowStride, cardW);
            card.Bind(in entry, e);
            if (!card.ApplyArt(entry.IsFolder ? null : ThumbnailFor(entry)))
                LookAgainForArt();
            PaintCard(card);
        }
        for (int i = 0; i < _pool.Count; i++)
        {
            var card = _pool[i];
            if (card.Active && (card.EntryIndex < start || card.EntryIndex >= end))
                card.Hide();
        }
    }

    private void EnsurePool(int count, float cardW)
    {
        if (_contentSlot == null)
            return;
        while (_pool.Count < count)
        {
            var card = new InventoryCard(_parts, _contentSlot, cardW);
            card.Pressed = OnCardPressed;
            _pool.Add(card);
        }
    }

    private void PaintCard(InventoryCard card)
    {
        bool selected = _selectedId != null && card.Id == _selectedId;
        bool target = _dropTarget != null && card.Id == _dropTarget;
        bool carried = _carried.HasValue && card.Id == _carried.Value.Id;
        card.SetState(selected, target, carried);
        _favourites?.Badge(card.Root, EntryById(card.Id));
    }

    private void RepaintCards()
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            if (_pool[i].Active)
                PaintCard(_pool[i]);
        }
    }

    // A saved thing's picture comes down once per hash and lives in the thumbnail cache from then on.
    private IAssetProvider<TextureAsset>? ThumbnailFor(in InventoryEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ThumbnailHash))
            return null;
        string key = "thumb|" + entry.ThumbnailHash;
        var have = _thumbnails.Get(key);
        if (have != null)
            return have;
        if (_thumbnailsAsked.Add(entry.ThumbnailHash!))
        {
            var captured = entry;
            Await(_source.FetchThumbnail(captured), bytes =>
            {
                if (bytes == null || bytes.Length == 0)
                    return;
                _thumbnails.OfferBase64(key, Convert.ToBase64String(bytes), LookAgainForArt);
            });
        }
        return null;
    }

    private void LookAgainForArt()
    {
        _artDirty = true;
        _artWaitLeft = ArtPatience;
    }

    private InventoryEntry? SelectedEntry() => _selectedId == null ? null : EntryById(_selectedId);

    private InventoryEntry? EntryById(string id)
    {
        for (int i = 0; i < _visible.Count; i++)
        {
            if (_visible[i].Id == id)
                return _visible[i];
        }
        return null;
    }

    // One press selects. A second press on the same card within the double press window opens it:
    // a folder navigates, an item spawns, a world opens. Move's pick step takes the press instead.
    private void OnCardPressed(InventoryCard card, UIInteractionContext context)
    {
        if (_carried.HasValue || _busy)
            return;
        var entry = EntryById(card.Id);
        if (!entry.HasValue)
            return;
        _focus = Field.None;
        _mergeOffer = null;
        if (_pickForMove)
        {
            _pickForMove = false;
            BeginCarry(entry.Value, context);
            return;
        }

        var now = DateTime.UtcNow;
        bool second = _lastPressId != null && _lastPressId == card.Id && (now - _lastPress).TotalSeconds <= DoublePressSeconds;
        _lastPress = now;
        _lastPressId = card.Id;
        _confirmDeleteLeft = 0f;

        if (second)
        {
            _lastPressId = null;
            switch (entry.Value.Kind)
            {
                case InventoryEntryKind.Folder: NavigateInto(entry.Value); break;
                case InventoryEntryKind.Item: SpawnItem(entry.Value); break;
                case InventoryEntryKind.World: OpenWorld(entry.Value); break;
            }
            return;
        }

        _selectedId = card.Id;
        RepaintCards();
        RebuildBar();
    }

    // MOVE

    private void StartMove()
    {
        if (_carried.HasValue)
        {
            CancelCarry();
            return;
        }
        if (!CanEditHere)
        {
            SetStatus(EditRefusal("nothing here can be moved"), DashTheme.Warning);
            return;
        }
        var selected = SelectedEntry();
        if (selected.HasValue)
        {
            BeginCarry(selected.Value, null);
            return;
        }
        _mergeOffer = null;
        _pickForMove = !_pickForMove;
        SetStatus(_pickForMove ? "Press the folder, item or world you want to move." : DescribeListing(), DashTheme.TextDim);
        RebuildBar();
    }

    private void BeginCarry(InventoryEntry entry, UIInteractionContext? context)
    {
        _carried = entry;
        _mergeOffer = null;
        _dropTarget = null;
        _selectedId = null;
        _confirmDeleteLeft = 0f;
        _catcher!.Slot.ActiveSelf.Value = true;
        _ghost!.Show(entry.Name, entry.Kind switch
        {
            InventoryEntryKind.Folder => "Folder",
            InventoryEntryKind.World => "World",
            _ => entry.IsAvatar ? "Avatar" : "Item",
        });
        if (context.HasValue)
            _ghost.MoveTo(context.Value.PointIn(_catcher.Slot));
        var back = Engine.Current?.InputInterface?.Actions?.Menu.Back;
        SetStatus(Input.InputHints.PadNames && back != null ? $"{Input.InputHints.Name(back)} puts it back." : "Escape puts it back.", DashTheme.TextDim);
        RepaintCards();
        RebuildBar();
    }

    private void CancelCarry()
    {
        if (!_carried.HasValue)
            return;
        _carried = null;
        _dropTarget = null;
        _pickForMove = false;
        _catcher!.Slot.ActiveSelf.Value = false;
        _ghost!.Hide();
        SetStatus(DescribeListing(), DashTheme.TextDim);
        RepaintCards();
        RebuildBar();
    }

    private void OnCarryMoved(UIInteractionContext context)
    {
        if (!_carried.HasValue || _ghost == null)
            return;
        _ghost.MoveTo(context.PointIn(_catcher?.Slot));
        string? target = FolderCardAt(context.LocalPoint)?.Id;
        if (!string.Equals(target, _dropTarget, StringComparison.Ordinal))
        {
            _dropTarget = target;
            RepaintCards();
        }
    }

    private void OnCarryDropped(UIInteractionContext context)
    {
        if (!_carried.HasValue)
            return;
        var carried = _carried.Value;
        InventoryPlace? target = null;
        var card = FolderCardAt(context.LocalPoint);
        if (card != null && EntryById(card.Id) is { } folder)
        {
            // A shared folder at the top of yours is someone else's: dropping into it is refused as any move
            // across inventories is.
            target = new InventoryPlace(folder.TargetOwnerId, folder.TargetId, folder.Name);
        }
        else
        {
            foreach (var (rect, place) in _dropRects)
            {
                if (rect != null && !rect.IsDestroyed && rect.LocalComputeRect.Contains(context.PointIn(rect.Slot)))
                {
                    target = place;
                    break;
                }
            }
            if (target == null && _sidebar != null)
            {
                foreach (var (rect, place) in _sidebar.TreeRects)
                {
                    if (rect != null && !rect.IsDestroyed && rect.Slot.IsActive && rect.LocalComputeRect.Contains(context.PointIn(rect.Slot)))
                    {
                        target = place;
                        break;
                    }
                }
            }
        }

        _carried = null;
        _dropTarget = null;
        _catcher!.Slot.ActiveSelf.Value = false;
        _ghost!.Hide();
        if (target == null)
        {
            SetStatus(DescribeListing(), DashTheme.TextDim);
            RepaintCards();
            RebuildBar();
            return;
        }
        RunMove(carried, target.Value);
    }

    // A move whose folder meets a folder of its name where it lands comes back as an offer to merge the two, which
    // the bar shows until it is taken, cancelled or left behind; anything else goes the way every change does.
    private void RunMove(InventoryEntry carried, InventoryPlace target)
    {
        if (_busy)
            return;
        _busy = true;
        SetStatus("Working…", DashTheme.TextDim);
        Await(_source.Move(carried, target), result =>
        {
            _busy = false;
            if (!result.Ok && result.Code == Inventory.NameTakenCode && carried.IsFolder && !carried.IsLink)
            {
                _mergeOffer = (carried, target);
                _notice = null;
                RebuildBar();
                return;
            }
            Finished(result);
        });
    }

    private void AddMergeOffer(Slot tools, in InventoryEntry folder, in InventoryPlace target)
    {
        var hint = tools.AddSlot("Hint");
        hint.AttachComponent<RectTransform>();
        var hintElement = BrowserParts.Size(hint, 400f, BarHeight);
        hintElement.MinWidth.Value = 120f;
        hintElement.FlexibleWidth.Value = 1f;
        var hintText = _parts.Label(hint, InventoryFolderLocale.MergeQuestion(BrowserParts.Truncate(folder.Name, 22), BrowserParts.Truncate(target.Name, 22)).Resolve(),
            DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);
        hintText.HorizontalAlignment.Value = TextHorizontalAlignment.Right;
        string cancel = InventoryFolderLocale.Cancel.Resolve();
        string merge = InventoryFolderLocale.MergeButton.Resolve();
        AddBarButton(tools, cancel, TabFill, LabelWidth(cancel), CancelMerge);
        AddBarButton(tools, merge, AccentColor, LabelWidth(merge), ConfirmMerge);
    }

    // Wide enough for a word in any language the bar is shown in.
    private static float LabelWidth(string label) => MathF.Max(84f, 2f * ControlTextInset + 10f * label.Length);

    // One press: everything in both folders ends up in the one already there, and the folder reads again.
    private void ConfirmMerge()
    {
        if (_mergeOffer is not { } offer || _busy)
            return;
        _mergeOffer = null;
        RunAction(_source.Merge(offer.Folder, offer.Target));
    }

    private void CancelMerge()
    {
        if (_mergeOffer == null)
            return;
        _mergeOffer = null;
        SetStatus(DescribeListing(), DashTheme.TextDim);
        RebuildBar();
    }

    private InventoryCard? FolderCardAt(in float2 point)
    {
        var canvas = Slot.GetComponentInParents<Canvas>();
        if (canvas == null || _viewportRect == null || !_viewportRect.LocalComputeRect.Contains(canvas.CanvasPointToLocal(_viewportRect.Slot, point)))
            return null;
        for (int i = 0; i < _pool.Count; i++)
        {
            var card = _pool[i];
            if (!card.Active || card.Kind != InventoryEntryKind.Folder)
                continue;
            if (_carried.HasValue && card.Id == _carried.Value.Id)
                continue;
            if (card.Contains(point, canvas.ScrollOffsetOf(card.Root)))
                return card;
        }
        return null;
    }

    // ACTIONS

    // A change on the cloud: the answer goes on the status line and the folder is read again.
    private void RunAction(Task<InventoryResult> task)
    {
        if (_busy)
            return;
        _busy = true;
        SetStatus("Working…", DashTheme.TextDim);
        Await(task, result =>
        {
            _busy = false;
            Finished(result);
        });
    }

    private void Finished(in InventoryResult result)
    {
        _notice = result.Message;
        _noticeColor = result.Ok ? DashTheme.TextDim : DashTheme.Warning;
        if (result.Ok)
        {
            _selectedId = null;
            Reload();
        }
        else
        {
            RebuildBar();
        }
    }

    private void DeleteSelected()
    {
        var selected = SelectedEntry();
        if (!selected.HasValue)
            return;
        if (_confirmDeleteLeft <= 0f)
        {
            _confirmDeleteLeft = ConfirmSeconds;
            bool asks = !selected.Value.IsLink && Here.IsGroup && selected.Value.OwnerId == Here.OwnerId && RankIn(Here.OwnerId) == 3;
            SetStatus(asks
                ? $"Press Confirm to ask the group's moderators to delete \"{selected.Value.Name}\"."
                : selected.Value.IsLink
                ? $"Press Confirm to take \"{selected.Value.Name}\" out of your inventory. {Inventory.OwnerLabel(selected.Value)} keeps it."
                : selected.Value.IsFolder
                ? $"Press Confirm to delete \"{selected.Value.Name}\" and everything in it."
                : $"Press Confirm to delete \"{selected.Value.Name}\".", DashTheme.Warning);
            RebuildBar();
            return;
        }
        _confirmDeleteLeft = 0f;
        RunAction(_source.Delete(selected.Value));
    }

    private World? FocusedWorld => Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;

    private bool CanSpawnHere(World world, out string? refusal)
    {
        refusal = null;
        // Bringing your own item in is the Spawn domain, same question the in-world dispensers ask.
        // The world's own mode ceiling is inside that answer, so an event world refuses here too.
        if (!world.AllowsItemSpawning
            || world.DataModelPermissions?.AllowsDomain(world.LocalUser, DataModelPermissionDomain.Spawn) == false)
        {
            refusal = "This world does not let you spawn items.";
            return false;
        }
        return true;
    }

    private float3 SpawnPosition(World world)
    {
        var userRoot = world.LocalUser?.Root;
        return userRoot?.HeadSlot != null
            ? userRoot.HeadPosition + userRoot.HeadRotation * (float3.Backward * 1.0f)
            : new float3(0f, 1f, 0f);
    }

    // Fetch first, on a task; load on the world thread once the file is here. The readout comes up in the
    // world the moment Spawn is pressed and stands in front of the item once it is there, counting its
    // meshes in, so a spawn is seen happen rather than read off a line on the dash. -xlinka
    private void SpawnItem(InventoryEntry entry) => FetchWithReadout(entry, SpawnLocale.Spawning, (world, path, readout) =>
    {
        if (!CanSpawnHere(world, out var refusal))
        {
            Refuse(refusal!, readout);
            return;
        }
        LoadFetched(world, path, entry.Name, readout, spawned => spawned.GlobalPosition = SpawnPosition(world), spawned =>
        {
            InspectorUndo.Record(world, SlotExistenceUndoBatch.Created(world, new[] { spawned }, UndoLocale.SpawnItem));
            ModelImportIndicator.Follow(readout, spawned, SpawnLocale.InFront, SpawnLocale.Spawned);
            SetStatus($"\"{entry.Name}\" is in front of you.", DashTheme.TextDim);
        });
    });

    // The item is read, decrypted and parsed on a task, and only the load itself runs on the world thread: the
    // reading was 60 ms of a 2700-slot avatar's spawn frame. A big one then goes in over several frames
    // (Inventory.LoadItemStaged); placed says where it stands as soon as it has a place, done hears it whole.
    // -xlinka
    private void LoadFetched(World world, string path, string name, object? readout, Action<Slot>? placed, Action<Slot> done)
    {
        Await(Task.Run(() => Inventory.ReadItem(path)), tree =>
        {
            var spawned = tree != null && !world.IsDestroyed
                ? Inventory.LoadItemStaged(world, tree, name, placed, loaded =>
                {
                    if (loaded == null)
                        Refuse($"Couldn't load \"{name}\".", readout);
                    else
                        done(loaded);
                })
                : null;
            if (spawned == null)
                Refuse($"Couldn't load \"{name}\".", readout);
        }, why => ModelImportIndicator.Fail(LocaleText.Plain(why), readout));
    }

    // Once it is on, the avatar's own arrival takes over in the world, so the readout only covers the fetch.
    private void EquipItem(InventoryEntry entry) => FetchWithReadout(entry, SpawnLocale.PuttingOn, (world, path, readout) =>
    {
        var manager = world.LocalUser?.Root?.GetRegisteredComponent<Lumora.Core.Components.Avatar.AvatarEquipManager>();
        if (manager == null)
        {
            Refuse("There is nothing to wear it on here.", readout);
            return;
        }
        // Wearing is not spawning: where props may not come in, the avatar still does, loaded straight onto the user.
        if (!CanSpawnHere(world, out _))
        {
            Await(Task.Run(() => Inventory.ReadItem(path)), tree =>
            {
                var loading = tree != null && !world.IsDestroyed && !manager.IsDestroyed
                    ? manager.LoadToWear(tree, entry.Name, loaded =>
                    {
                        if (loaded != null && !manager.IsDestroyed && manager.EquipAvatar(loaded, replaceCurrent: true))
                        {
                            ModelImportIndicator.Hide(readout, SpawnLocale.Wearing, SpawnLocale.PutOn);
                            SetStatus($"Wearing \"{entry.Name}\".", DashTheme.TextDim);
                            return;
                        }
                        if (loaded != null && !loaded.IsDestroyed)
                            loaded.Destroy();
                        Refuse($"\"{entry.Name}\" would not go on.", readout);
                    })
                    : null;
                if (loading == null)
                    Refuse($"Couldn't load \"{entry.Name}\".", readout);
            }, why => ModelImportIndicator.Fail(LocaleText.Plain(why), readout));
            return;
        }
        LoadFetched(world, path, entry.Name, readout, spawned => spawned.GlobalPosition = SpawnPosition(world), spawned =>
        {
            spawned.GlobalPosition = SpawnPosition(world);
            if (!manager.IsDestroyed && manager.EquipAvatar(spawned, replaceCurrent: true))
            {
                ModelImportIndicator.Hide(readout, SpawnLocale.Wearing, SpawnLocale.PutOn);
                SetStatus($"Wearing \"{entry.Name}\".", DashTheme.TextDim);
            }
            else
            {
                InspectorUndo.Record(world, SlotExistenceUndoBatch.Created(world, new[] { spawned }, UndoLocale.SpawnItem));
                ModelImportIndicator.Follow(readout, spawned, SpawnLocale.WouldNotEquip, SpawnLocale.Spawned);
                SetStatus($"\"{entry.Name}\" is in front of you, but it would not equip.", DashTheme.TextDim);
            }
        });
    });

    private void Refuse(string why, object? readout)
    {
        SetStatus(why, DashTheme.Warning);
        ModelImportIndicator.Fail(LocaleText.Plain(why), readout);
    }

    private void OpenWorld(InventoryEntry entry) => FetchThen(entry, (_, path) =>
    {
        var manager = Lumora.Core.Engine.Current?.WorldManager;
        if (manager == null)
        {
            SetStatus("The world manager isn't up yet.", DashTheme.Warning);
            return;
        }
        var world = manager.OpenSavedWorld(path, entry.Name, fromCloudRecord: true);
        // Save changes in it writes over this item rather than making another, wherever the item lives, when
        // this account may change things there. Someone else's world opened from a share or a link is theirs:
        // Save changes makes a new one in the open folder instead. -xlinka
        if (world != null && !entry.IsLink && _access.CanChange())
        {
            world.SavedItemId = entry.Id;
            world.SavedItemAccount = Lumora.Core.InventorySync.Account;
            world.SavedItemName = entry.Name;
            world.SavedItemFolder = entry.FolderId;
            world.SavedItemOwner = entry.OwnerId;
        }
        SetStatus(world != null ? $"Opened \"{entry.Name}\"." : $"Couldn't open \"{entry.Name}\".", world != null ? DashTheme.TextDim : DashTheme.Warning);
    });

    // The orb points at the fetched file on this machine, so it opens here and refuses anywhere else.
    private void SpawnWorldOrb(InventoryEntry entry) => FetchThen(entry, (world, path) =>
    {
        bool queued = WorldOrb.SpawnForSavedWorld(world, path, out var reason);
        SetStatus(queued ? $"Orb for \"{entry.Name}\" is in front of you." : $"Couldn't place that orb: {reason ?? "refused"}.",
            queued ? DashTheme.TextDim : DashTheme.Warning);
    });

    private void FetchThen(InventoryEntry entry, Action<World, string> then)
        => Fetch(entry, default, (world, path, _) => then(world, path));

    private void FetchWithReadout(InventoryEntry entry, LocaleText readout, Action<World, string, object?> then)
        => Fetch(entry, readout, then);

    // With a readout title the in-world readout comes up as the fetch starts and goes red with the reason
    // when it does not land, rather than hanging at "Fetching".
    private void Fetch(InventoryEntry entry, LocaleText readoutTitle, Action<World, string, object?> then)
    {
        if (_busy)
            return;
        var world = FocusedWorld;
        if (world == null)
        {
            SetStatus("The world manager isn't up yet.", DashTheme.Warning);
            return;
        }
        _busy = true;
        SetStatus($"Fetching \"{entry.Name}\"…", DashTheme.TextDim);
        object? readout = null;
        if (!readoutTitle.IsEmpty)
        {
            readout = ModelImportIndicator.Show(world, null, readoutTitle, entry.Name);
            ModelImportIndicator.Report(new ImportProgress(ImportStage.Fetching, 0.1f), readout);
        }
        Await(_source.FetchToCache(entry), fetched =>
        {
            _busy = false;
            if (fetched.path == null)
            {
                Refuse(fetched.problem ?? "The download did not go through.", readout);
                return;
            }
            var target = FocusedWorld ?? world;
            if (target.IsDestroyed)
            {
                Refuse("That world is gone.", readout);
                return;
            }
            // Somewhere else by the time it landed: the readout follows the item there.
            if (readout != null && !ReferenceEquals(target, world))
            {
                ModelImportIndicator.Dismiss(readout);
                readout = ModelImportIndicator.Show(target, null, readoutTitle, entry.Name);
            }
            then(target, fetched.path, readout);
        }, why => ModelImportIndicator.Fail(LocaleText.Plain(why), readout));
    }

    // Everything in either hand that may be copied out, saved into the folder being looked at, or the top of
    // your own inventory when this folder is not yours to save into.
    private void SaveHeld()
    {
        if (_busy || RefusedForEmail())
            return;
        var world = FocusedWorld;
        var root = world?.LocalUser?.Root;
        if (world == null || root == null)
        {
            SetStatus("The world manager isn't up yet.", DashTheme.Warning);
            return;
        }
        var slots = new List<Slot>();
        foreach (var grabber in root.Slot.GetComponentsInChildren<Lumora.Core.Components.Interaction.Grabber>())
        {
            foreach (var grabbable in grabber.GrabbedObjects)
            {
                var slot = (grabbable as Component)?.Slot;
                if (slot != null && !slot.IsDestroyed)
                    slots.Add(slot);
            }
        }
        if (slots.Count == 0)
        {
            SetStatus("Nothing in your hands. Grab something first.", DashTheme.TextDim);
            return;
        }
        var uploads = new List<Task<InventoryResult>>();
        int refused = 0;
        string? lastRefusal = null;
        var place = SaveHere(out var notice);
        foreach (var slot in slots)
        {
            if (Lumora.Core.Components.Interaction.GrabSaveBlock.BlocksInventorySave(slot))
            {
                refused++;
                lastRefusal = "marked not saveable";
                continue;
            }
            if (!Components.ItemProtection.AllowsSaveCopy(slot, world.LocalUser, out var reason))
            {
                refused++;
                lastRefusal = reason ?? "not allowed";
                continue;
            }
            uploads.Add(_source.SaveItem(slot, slot.Name, place));
        }
        if (uploads.Count == 0)
        {
            SetStatus($"Nothing could be saved: {lastRefusal}.", DashTheme.Warning);
            return;
        }
        _busy = true;
        SetStatus(uploads.Count == 1 ? "Saving 1 item…" : $"Saving {uploads.Count} items…", DashTheme.TextDim);
        int refusedCount = refused;
        string? refusalText = lastRefusal;
        Await(Task.WhenAll(uploads), results =>
        {
            _busy = false;
            int saved = 0;
            string? failure = null;
            foreach (var r in results)
            {
                if (r.Ok) saved++; else failure = r.Message;
            }
            string line = saved == 1 ? "Saved 1 item here." : $"Saved {saved} items here.";
            if (notice != null)
                line = saved == 1 ? notice : saved > 1 ? $"{notice.TrimEnd('.')}: {saved} items." : $"Saved {saved} items to your inventory.";
            if (failure != null)
                line += $" One failed: {failure}";
            if (refusedCount > 0)
                line += refusedCount == 1 ? $" One was refused: {refusalText}." : $" {refusedCount} were refused: {refusalText}.";
            _notice = line;
            _noticeColor = saved == 0 || notice != null ? DashTheme.Warning : DashTheme.TextDim;
            Reload();
        });
    }

    // The avatar being worn, saved as it is. The socket it hangs on lives outside the avatar's own
    // tree, so the upload carries no link to it and the copy comes back unworn.
    private void SaveAvatar()
    {
        if (_busy || RefusedForEmail())
            return;
        var world = FocusedWorld;
        var manager = world?.LocalUser?.Root?.GetRegisteredComponent<Lumora.Core.Components.Avatar.AvatarEquipManager>();
        var avatar = manager?.CurrentAvatar.Target;
        if (world == null || avatar == null || avatar.IsDestroyed)
        {
            SetStatus("You are not wearing an avatar.", DashTheme.Warning);
            return;
        }
        if (!Components.ItemProtection.AllowsSaveCopy(avatar, world.LocalUser, out var reason))
        {
            SetStatus($"That avatar can't be saved: {reason ?? "not allowed"}.", DashTheme.Warning);
            return;
        }
        _busy = true;
        SetStatus("Saving your avatar…", DashTheme.TextDim);
        var place = SaveHere(out var notice);
        Await(_source.SaveItem(avatar, avatar.Name, place), result =>
        {
            _busy = false;
            _notice = notice != null && result.Ok ? Inventory.WentToOwn(notice, result.Message) : result.Message;
            _noticeColor = result.Ok && notice == null ? DashTheme.TextDim : DashTheme.Warning;
            Reload();
        });
    }

    // DIALOG: one text field, used for a new folder's name and for renaming.

    private void BuildDialog(Slot root)
    {
        _dialogRoot = root.AddSlot("Dialog");
        BrowserParts.Fill(_dialogRoot.AttachComponent<RectTransform>());
        _dialogRoot.AttachComponent<IgnoreLayout>();
        _dialogRoot.ActiveSelf.Value = false;

        var backdrop = _dialogRoot.AddSlot("Backdrop");
        BrowserParts.Fill(backdrop.AttachComponent<RectTransform>());
        // The cards are chunks of their own and drew straight through the dialog; overlay bands put the
        // scrim and the panel above every one of them. -xlinka
        backdrop.AttachComponent<GraphicChunkRoot>().OverlayLevel = 1;
        // Button before Image: a button attached after an image takes over its tint as a hover ramp, and the
        // whole window lit up grey under the cursor. -xlinka
        backdrop.AttachComponent<Button>().Clicked += (_, _) => CloseDialog();
        backdrop.AttachComponent<Image>().Tint.Value = DashTheme.ModalScrim;

        var panel = _dialogRoot.AddSlot("Panel");
        var panelRect = panel.AttachComponent<RectTransform>();
        panelRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        panelRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        panelRect.OffsetMin.Value = new float2(-220f, -92f);
        panelRect.OffsetMax.Value = new float2(220f, 92f);
        panel.AttachComponent<GraphicChunkRoot>().OverlayLevel = 2;
        BrowserParts.Panel(panel, DashTheme.Panel, DashTheme.RadiusCard, DashTheme.Outline, 1.5f);
        panel.AttachComponent<Button>();

        var title = panel.AddSlot("Title");
        BrowserParts.PinTop(title.AttachComponent<RectTransform>(), 16f, 26f, 20f);
        _dialogTitle = _parts.Label(title, string.Empty, DashTheme.FontHeading, DashTheme.Text, BrowserParts.Weight.Bold);
        _dialogTitle.HorizontalAlignment.Value = TextHorizontalAlignment.Left;

        var well = panel.AddSlot("Well");
        BrowserParts.PinTop(well.AttachComponent<RectTransform>(), 54f, DashTheme.ControlHeight, 20f);
        BrowserParts.Panel(well, DashTheme.Field, DashTheme.RadiusControl, DashTheme.Accent, 1.5f);
        well.AttachComponent<Button>().Clicked += (_, _) => _focus = Field.Dialog;
        var wellText = well.AddSlot("Text");
        var wellTextRect = wellText.AttachComponent<RectTransform>();
        BrowserParts.Fill(wellTextRect);
        wellTextRect.OffsetMin.Value = new float2(12f, 0f);
        wellTextRect.OffsetMax.Value = new float2(-12f, 0f);
        _dialogText = _parts.Label(wellText, string.Empty, DashTheme.FontBody, DashTheme.Text, BrowserParts.Weight.Regular);
        _dialogText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;

        var problem = panel.AddSlot("Problem");
        BrowserParts.PinTop(problem.AttachComponent<RectTransform>(), 96f, 20f, 20f);
        _dialogError = _parts.Label(problem, string.Empty, DashTheme.FontSmall, DashTheme.Warning, BrowserParts.Weight.Regular);
        _dialogError.HorizontalAlignment.Value = TextHorizontalAlignment.Left;

        var buttons = panel.AddSlot("Buttons");
        BrowserParts.PinBottom(buttons.AttachComponent<RectTransform>(), 14f, DashTheme.ControlHeight, 20f);
        var buttonRow = buttons.AttachComponent<HorizontalLayout>();
        buttonRow.Spacing.Value = 8f;
        buttonRow.ForceExpandWidth.Value = false;
        buttonRow.ForceExpandHeight.Value = true;
        var fill = buttons.AddSlot("Fill");
        fill.AttachComponent<RectTransform>();
        fill.AttachComponent<LayoutElement>().FlexibleWidth.Value = 1f;
        AddInlineButton(buttons, "Cancel", TabFill, 84f, CloseDialog);
        AddInlineButton(buttons, "OK", AccentColor, 84f, ConfirmDialog);
    }

    private void OpenDialog(DialogKind kind)
    {
        if (_dialogRoot == null || !_source.SignedIn)
            return;
        if (_carried.HasValue)
            CancelCarry();
        var selected = SelectedEntry();
        if (kind == DialogKind.Rename && !selected.HasValue)
            return;
        if (kind == DialogKind.NewFolder && !CanEditHere)
        {
            SetStatus(EditRefusal("no folder can go in it"), DashTheme.Warning);
            return;
        }
        if (kind == DialogKind.Rename && !selected!.Value.IsKeptLink && !CanEditHere)
        {
            SetStatus(EditRefusal("nothing here can be renamed"), DashTheme.Warning);
            return;
        }
        CloseShare();
        _mergeOffer = null;
        _dialog = kind;
        _dialogBuffer = kind == DialogKind.Rename ? selected!.Value.Name : string.Empty;
        _dialogProblem = null;
        _focus = Field.Dialog;
        BrowserParts.SetText(_dialogTitle, kind switch
        {
            DialogKind.Rename => $"Rename \"{BrowserParts.Truncate(selected!.Value.Name, 24)}\"",
            DialogKind.AddLink => $"Paste a shared link into {BrowserParts.Truncate(LinkHome.Name, 18)}",
            _ => $"New folder in {BrowserParts.Truncate(FolderName, 22)}",
        });
        UpdateDialog();
        _dialogRoot.ActiveSelf.Value = true;
        MarkDirty();
    }

    private void CloseDialog()
    {
        if (_dialogRoot == null || _dialog == DialogKind.None)
            return;
        _dialog = DialogKind.None;
        _focus = Field.None;
        _dialogRoot.ActiveSelf.Value = false;
        MarkDirty();
    }

    private void UpdateDialog()
    {
        bool empty = _dialogBuffer.Length == 0;
        string shown = _dialog == DialogKind.AddLink && _dialogBuffer.Length > 44 ? "…" + _dialogBuffer[^43..] : _dialogBuffer;
        BrowserParts.SetText(_dialogText, empty ? (_dialog == DialogKind.AddLink ? Inventory.LinkPrefix + "…" : "Name") : shown);
        if (_dialogText != null)
            BrowserParts.SetColor(_dialogText.Color, empty ? DashTheme.TextDim : DashTheme.Text);
        BrowserParts.SetText(_dialogError, _dialogProblem ?? string.Empty);
        MarkDirty();
    }

    private void ConfirmDialog()
    {
        if (_dialog == DialogKind.None || _busy)
            return;
        string name = _dialogBuffer.Trim();
        if (_dialog != DialogKind.AddLink && !Inventory.ValidName(name, out var error))
        {
            _dialogProblem = error;
            UpdateDialog();
            return;
        }
        Task<InventoryResult> task;
        if (_dialog == DialogKind.NewFolder)
        {
            task = _source.CreateFolder(Here, name);
        }
        else if (_dialog == DialogKind.AddLink)
        {
            task = _source.AddLink(name, LinkHome.FolderId);
        }
        else
        {
            var selected = SelectedEntry();
            if (!selected.HasValue)
            {
                CloseDialog();
                return;
            }
            task = _source.Rename(selected.Value, name);
        }
        _busy = true;
        BrowserParts.SetText(_dialogError, "Working…");
        Await(task, result =>
        {
            _busy = false;
            if (!result.Ok)
            {
                _dialogProblem = result.Message;
                UpdateDialog();
                return;
            }
            CloseDialog();
            _notice = result.Message;
            _noticeColor = DashTheme.TextDim;
            _selectedId = null;
            Reload();
        });
    }

    // KEYS: the search well and the dialog's field share one route; whichever has focus takes them.

    public bool HasFieldFocus => _focus == Field.Search || (_focus == Field.Dialog && _dialog != DialogKind.None);
    public string? CopyText => _focus == Field.Search ? _search : HasFieldFocus ? _dialogBuffer : null;

    public bool ConsumeChar(char c)
    {
        if (_focus == Field.Dialog && _dialog != DialogKind.None)
        {
            if (char.IsControl(c) || _dialogBuffer.Length >= (_dialog == DialogKind.AddLink ? 200 : 64))
                return true;
            _dialogBuffer += c;
            _dialogProblem = null;
            UpdateDialog();
            return true;
        }
        if (_focus == Field.Search)
        {
            if (char.IsControl(c) || _search.Length >= 48)
                return true;
            _search += c;
            _selectedId = null;
            _page = 0;
            Reload();
            return true;
        }
        return false;
    }

    public bool ConsumeBackspace()
    {
        if (_focus == Field.Dialog && _dialog != DialogKind.None)
        {
            if (_dialogBuffer.Length > 0)
            {
                _dialogBuffer = Lumora.Core.Input.TextElements.RemoveLast(_dialogBuffer);
                UpdateDialog();
            }
            return true;
        }
        if (_focus == Field.Search)
        {
            if (_search.Length > 0)
            {
                _search = Lumora.Core.Input.TextElements.RemoveLast(_search);
                Reload();
            }
            return true;
        }
        return false;
    }

    public bool ConsumeEnter()
    {
        if (_dialog != DialogKind.None)
        {
            ConfirmDialog();
            return true;
        }
        if (_focus == Field.Search)
        {
            _focus = Field.None;
            RebuildBar();
            return true;
        }
        if (_mergeOffer != null)
        {
            ConfirmMerge();
            return true;
        }
        return false;
    }

    public bool ConsumeEscape()
    {
        if (_dialog != DialogKind.None)
        {
            CloseDialog();
            return true;
        }
        if (_shareOpen)
        {
            CloseShare();
            return true;
        }
        if (_requestsOpen)
        {
            CloseRequests();
            return true;
        }
        if (_mergeOffer != null)
        {
            CancelMerge();
            return true;
        }
        if (_carried.HasValue || _pickForMove)
        {
            _pickForMove = false;
            CancelCarry();
            RebuildBar();
            return true;
        }
        if (_focus == Field.Search || _search.Length > 0)
        {
            _focus = Field.None;
            _search = string.Empty;
            Reload();
            return true;
        }
        if (_selectedId != null)
        {
            _selectedId = null;
            RepaintCards();
            RebuildBar();
            return true;
        }
        return false;
    }
}

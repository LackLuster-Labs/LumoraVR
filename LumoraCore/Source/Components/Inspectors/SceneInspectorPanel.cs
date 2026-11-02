// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Reflection;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Session;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// receives inspector row-button actions routed through InspectorButtonRelay
public interface IInspectorActionHandler
{
    void HandleInspectorAction(string argument);
}

// handlers that also want the pressing user's interaction context - destructive actions use
// it to aim a confirmation at THAT user's radial menu before running
public interface IInspectorActionContextHandler
{
    void HandleInspectorAction(string argument, UIInteractionContext context);
}

// two panes: left is the slot hierarchy, right is the selected slot's members + one section per
// component. Tree is INCREMENTAL - one row object per visible slot, each owning its own child
// container - so expand/collapse/rename/reorder/live add-remove touch only the rows involved.
// Authority builds the replicated UI; selection/expansion synced so all peers agree. Local-only
// slots and [HideInInspector] components never appear.
[ComponentCategory("Utility/Inspectors")]
public partial class SceneInspectorPanel : Component, IInspectorActionHandler, IInspectorActionContextHandler
{
    public readonly SyncRef<Slot> Root;
    public readonly SyncRef<Slot> Selected;
    public readonly SyncFieldList<ulong> ExpandedSlots;
    public readonly SyncFieldList<ulong> ExpandedComponents;

    private readonly SyncRef<Slot> _hierarchyContent;
    private readonly SyncRef<Slot> _componentsContent;
    private readonly SyncRef<Text> _selectedTitle;
    private readonly SyncRef<Text> _rootTitle;
    private readonly SyncRef<ComponentSelectorPanel> _selector;

    // The header actions that have no meaning on the world root. Held so the selection pass can take
    // them out of the toolbar entirely rather than leaving buttons that look live and do nothing.
    private readonly SyncRef<Slot> _insertParentButton;
    private readonly SyncRef<Slot> _duplicateButton;
    private readonly SyncRef<Slot> _destroyAssetsButton;
    private readonly SyncRef<Slot> _destroyButton;
    private readonly SyncRef<Slot> _focusButton;

    private bool _hierarchyDirty = true;
    private bool _componentsDirty = true;

    // Visible-row cap, spent as an EXPANSION budget: the expansion that would cross it stops and
    // leaves a "+N more" marker in THAT container, instead of truncating the tree somewhere the user
    // never looked.
    private const int RowBudget = 250;
    private const float RowHeight = InspectorUI.RowHeight;
    private const float IndentPerDepth = 18f;
    // Deep trees and long names. The indent tightens after FullIndentDepth levels and stops at
    // MaxIndent, so a row twelve deep keeps room for its name instead of squeezing it to a letter a line that
    // stacked over the rows below and left them unclickable. Names stay on one line, cut with an ellipsis,
    // and a name past NameDisplayLimit characters is shortened for the tree only. -xlinka
    private const int FullIndentDepth = 6;
    private const float DeepIndentPerDepth = 7f;
    private const float MaxIndent = 170f;
    private const int NameDisplayLimit = 64;

    private static float IndentFor(int depth)
        => MathF.Min(MathF.Min(depth, FullIndentDepth) * IndentPerDepth
            + MathF.Max(0, depth - FullIndentDepth) * DeepIndentPerDepth, MaxIndent);

    private static string TreeName(string? name)
    {
        string value = name ?? "Slot";
        return value.Length <= NameDisplayLimit ? value : value.Substring(0, NameDisplayLimit - 1) + "\u2026";
    }
    // Gap between rows. The scroll list, the node wrapper and every child container all use it, so a
    // nested row sits at exactly the pitch the flat list had.
    private const float TreeSpacing = 2f;

    // THE tree. One HierarchyRow per visible slot, keyed by slot RefID, owning that slot's row UI,
    // its child container and its live subscriptions. Every tree event resolves through this map to
    // ONE row and touches only that row's container, so expand, collapse, rename, reorder and live
    // add/remove never walk the tree, let alone rebuild it. Rows are their own graphics chunks, so an
    // untouched row keeps its baked mesh. -xlinka
    private readonly Dictionary<ulong, HierarchyRow> _rows = new();
    private int _visibleRows;
    private ulong _highlightedId;

    // Rows whose child set moved, drained once per OnChanges: a burst (an imported object dropping
    // fifty slots at once) costs one pass over the affected containers instead of fifty.
    private readonly HashSet<ulong> _pendingChildSync = new();
    private readonly List<HierarchyRow> _syncScratch = new();
    private bool _expansionDirty;
    private bool _traceDirty;

    // Live rename of the pane titles. Rows carry their own name watch and drop it with the row.
    private Sync<string>? _rootNameField;
    private Action<string>? _rootNameHandler;
    private Sync<string>? _titleNameField;
    private Action<string>? _titleNameHandler;

    private sealed class HierarchyRow
    {
        public Slot Target = null!;
        public ulong TargetId;
        public int Depth;
        public HierarchyRow? Parent;
        public readonly List<HierarchyRow> Children = new();

        // Node wraps the row and, while expanded, the container its children live in.
        public Slot NodeSlot = null!;
        public Slot RowSlot = null!;
        public Slot? ChildContainer;
        public Slot? OverflowRow;
        public int OverflowCount;

        public Text? Label;
        public Text? ExpanderLabel;
        public Text? BranchLabel;
        public Image? Background;
        public color BaseColor;
        public bool EffectivePersistent;
        public bool Expanded;

        public Action<Slot, Slot>? ChildAdded;
        public Action<Slot, Slot>? ChildRemoved;
        public Action<Slot>? OrderChanged;
        public Action<Slot>? ActiveChanged;
        public Action<Slot>? PersistentChanged;
        public Action<string>? NameChanged;
    }

    public SceneInspectorPanel()
    {
        Root = new SyncRef<Slot>(this);
        Selected = new SyncRef<Slot>(this);
        ExpandedSlots = new SyncFieldList<ulong>(this);
        ExpandedComponents = new SyncFieldList<ulong>(this);
        // What the panel shows and where it is in it: anyone pointing at it moves through it, whoever has it in hand.
        Root.MarkUiState();
        Selected.MarkUiState();
        ExpandedSlots.MarkUiState();
        ExpandedComponents.MarkUiState();
        ActiveTab.MarkUiState();
        HierarchyWidth.MarkUiState();
        _hierarchyContent = new SyncRef<Slot>(this);
        _componentsContent = new SyncRef<Slot>(this);
        _selectedTitle = new SyncRef<Text>(this);
        _rootTitle = new SyncRef<Text>(this);
        _selector = new SyncRef<ComponentSelectorPanel>(this);
        _insertParentButton = new SyncRef<Slot>(this);
        _duplicateButton = new SyncRef<Slot>(this);
        _destroyAssetsButton = new SyncRef<Slot>(this);
        _destroyButton = new SyncRef<Slot>(this);
        _focusButton = new SyncRef<Slot>(this);
    }

    // A slot under an ImmutableComponent is not editable content, so a panel aimed at one would be a
    // window onto rows that all refuse. Log it rather than opening an empty-feeling panel: the person
    // pressed something and nothing happened, and this is the only place that can say why.
    private static bool RefuseProtected(Slot? rootTarget, string what)
    {
        // An account's seal keeps its object shut to everyone but its owner, the host included.
        if (Security.ItemSeals.RefusesHere(rootTarget, Security.SealRule.Inspect, inside: true))
        {
            Logging.Logger.Log($"SceneInspectorPanel: refusing to {what} on '{rootTarget!.SlotName.Value}' - sealed");
            return true;
        }
        if (!ImmutableComponent.IsProtected(rootTarget))
            return false;
        Logging.Logger.Log($"SceneInspectorPanel: refusing to {what} on '{rootTarget!.SlotName.Value}' - protected by ImmutableComponent");
        return true;
    }

    // Whose window this is, for the seal gate: the user it is pinned to, else the one who opened it. The authority
    // builds every panel's rows, so the question cannot be put to whoever is at this machine.
    private User? Viewer
        => Slot?.GetComponent<PanelWorkspace>()?.PinnedUser.Target ?? Security.ItemSeals.BringerOf(Slot);

    private bool SealedFromViewer(Slot? target, out string? reason)
    {
        reason = null;
        return target != null && Security.ItemSeals.JudgeInside(target, Viewer?.AccountId.Value, Security.SealRule.Inspect, out reason, out _)
            != Security.SealVerdict.Allowed;
    }

    // Rows are SCENE content. Local-only slots are render plumbing and never appear; a protected
    // subtree never appears either, and skipping it at the parent means none of its descendants are
    // ever reached, let alone walked.
    private static bool IsHiddenFromTree(Slot slot)
        => slot.IsLocalElement || ImmutableComponent.IsProtected(slot) || UndoGraveyard.IsGraveyard(slot);

    // Social and Event worlds are view spaces: the authored world is frozen for everyone, host
    // included, and the tooling stays off with no switch to bring it back. Every inspector opens
    // through the entry points below, so this one check is the whole lock. -xlinka
    private static bool RefuseLocked(World? world)
        => world == null || world.IsDestroyed || !world.AllowsWorldEditing;

    public static SceneInspectorPanel? Spawn(World world, Slot rootTarget) => Spawn(world, rootTarget, null);

    // opened hears the panel once it stands: straight away, or after the first fetch of the user's favourite
    // inspector, when the return is null. See SceneInspectorFavourite.
    public static SceneInspectorPanel? Spawn(World world, Slot rootTarget, Action<SceneInspectorPanel>? opened)
    {
        if (RefuseLocked(world) || RefuseProtected(rootTarget, "spawn"))
            return null;
        return Fill(PanelSlotAhead(world), rootTarget, opened);
    }

    // The built-in whatever anyone's favourite is: a panel that is part of a world's own content, set up by its
    // template, rather than one somebody opened.
    public static SceneInspectorPanel? SpawnBuiltIn(World world, Slot rootTarget)
    {
        if (RefuseLocked(world) || RefuseProtected(rootTarget, "spawn"))
            return null;
        return BuiltIn(PanelSlotAhead(world), rootTarget);
    }

    private static Slot PanelSlotAhead(World world)
    {
        var head = world.LocalUser?.Root?.HeadSlot;
        var panelSlot = world.RootSlot.AddSlot("Inspector");
        panelSlot.Persistent.Value = false;
        panelSlot.Tag.Value = "Developer";

        if (head != null)
        {
            float3 forward = head.GlobalRotation * float3.Backward; // view forward is -Z
            forward.y = 0f;
            forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
            // Drop the panel centre below eye level so the HEADER sits near the gaze line and the body falls into the
            // natural downward view, instead of centring on the eyes (which makes you look UP to read the title). -xlinka
            panelSlot.GlobalPosition = head.GlobalPosition + forward * 1.0f + float3.Down * (240f * InspectorUI.PanelScale);
            FaceHead(panelSlot, head);
        }
        panelSlot.LocalScale.Value = float3.One * InspectorUI.PanelScale;
        return panelSlot;
    }

    // Yaw the panel so its readable +Z front points AT the head. Mirrors FaceLocalUser: the vector runs
    // FROM the panel TO the head, so a panel placed in front of the user turns to face back at them. The
    // old spawn yawed off the head's forward (pointing away), which showed the panel's back. -xlinka
    private static void FaceHead(Slot panelSlot, Slot head)
    {
        var toViewer = head.GlobalPosition - panelSlot.GlobalPosition;
        toViewer.y = 0f;
        if (toViewer.LengthSquared <= 1e-6f)
            return;
        panelSlot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toViewer.x, toViewer.z));
    }

    // Open-from-a-card entry point. Spawn() drops every panel on the same head-relative spot, so opening
    // the same thing twice stacked two identical panels on top of each other: they z-fight (rows fading in
    // and out), the tree lists "Inspector" once per copy, and the extras read as phantom objects because you
    // only ever see the front one. A tab already rooted at the slot brings its saved view to the front; a new
    // root becomes a tab in the panel nearest the card when one is within reach, else a panel of its own.
    // -xlinka
    //
    // mineOnly keeps to panels this machine minted. A tab writes the panel's tabs, root, selection and
    // expansion lists, and a builder may add to another user's panel but not clear its lists, so a tab
    // opened there stops part way with that user's view already switched. A panel pinned to someone else, or
    // in their hand, never takes a tab at all. -xlinka
    public static SceneInspectorPanel? OpenOrFocus(World world, Slot rootTarget, float3 near, float3? spawnAt = null,
        bool mineOnly = false, Action<SceneInspectorPanel>? opened = null)
    {
        if (RefuseLocked(world) || RefuseProtected(rootTarget, "open"))
            return null;

        SceneInspectorPanel? nearest = null;
        float nearestDist = TabReach * TabReach;
        foreach (var panel in world.RootSlot.GetComponentsInChildren<SceneInspectorPanel>())
        {
            if (panel.IsDestroyed || panel.Slot == null || panel.Slot.IsDestroyed)
                continue;
            if (PinnedToSomeoneElse(world, panel) || (mineOnly && !MintedHere(world, panel.Slot)))
                continue;
            if (panel.HasTabFor(rootTarget))
            {
                panel.OpenTab(rootTarget);
                return Opened(panel, opened);
            }
            float d = (panel.Slot.GlobalPosition - near).LengthSquared;
            if (d < nearestDist)
            {
                nearestDist = d;
                nearest = panel;
            }
        }
        if (nearest != null)
        {
            nearest.OpenTab(rootTarget);
            return Opened(nearest, opened);
        }
        return spawnAt is { } at ? SpawnAt(world, rootTarget, at, opened) : Spawn(world, rootTarget, opened);
    }

    // Spawn() on a spot the caller picked, still turned to face the user. The panel goes there whenever it
    // stands, so a favourite that comes after its fetch lands on the same spot.
    public static SceneInspectorPanel? SpawnAt(World world, Slot root, float3 position, Action<SceneInspectorPanel>? opened = null)
        => Spawn(world, root, panel =>
        {
            panel.Slot.GlobalPosition = position;
            var head = world.LocalUser?.Root?.HeadSlot;
            if (head != null)
                FaceHead(panel.Slot, head);
            opened?.Invoke(panel);
        });

    // A guest mints in its own allocation byte, the host in the world's.
    private static bool MintedHere(World world, Slot slot)
    {
        if (world.IsAuthority)
            return slot.ReferenceID.IsAuthorityID;
        var local = world.LocalUser;
        return local != null && RefIDConstants.IsValidUserByte(local.AllocationID.Value)
            && slot.ReferenceID.GetUserByte() == local.AllocationID.Value;
    }

    // Pinned to somebody else, or in their hand: a tab there would be this machine changing their things.
    private static bool PinnedToSomeoneElse(World world, SceneInspectorPanel panel)
        => panel.Slot.GetComponent<PanelWorkspace>()?.PinnedUser.Target is { } user && !ReferenceEquals(user, world.LocalUser)
           || panel.Slot.ActiveUser is { } holder && !ReferenceEquals(holder, world.LocalUser);

    // Metres. A card dropped further than this from every panel opens a panel of its own rather than a
    // tab somewhere across the world.
    private const float TabReach = 2.5f;

    private bool HasTabFor(Slot root)
    {
        if (ReferenceEquals(Root.Target, root))
            return true;
        foreach (var tab in Tabs)
        {
            if (tab is { IsDestroyed: false } && ReferenceEquals(tab.Root.Target, root))
                return true;
        }
        return false;
    }

    // A panel's width and a gap, in the origin's own units, so a resized panel's neighbour still clears it.
    public static SceneInspectorPanel? SpawnAdjacent(SceneInspectorPanel origin, Slot rootTarget)
        => SpawnBeside(origin, rootTarget, new float3(1440f * origin.Slot.GlobalScale.x, 0f, 0f));

    public override void OnAttach()
    {
        base.OnAttach();

        var theme = Slot.GetOrAttachComponent<UITheme>();

        var shell = Slot.GetOrAttachComponent<PanelShell>();
        shell.Title.Value = "Inspector";
        // Tall, narrow portrait tool: longer so more rows show at once, thinner so it
        // doesn't hog the view. Content still scrolls on overflow (bounded viewport), so height isn't the lever. -xlinka
        shell.Size.Value = new float2(1000f, 1360f);
        theme.ApplyTo(shell);
        Slot.GetOrAttachComponent<PanelWorkspace>();

        shell.RebuildContent(BuildLayout);
        _hierarchyDirty = true;
        _componentsDirty = true;
    }

    private World? _subscribedWorld;

    public override void OnStart()
    {
        base.OnStart();
        // Live tree: react to slots spawned/removed in the world so the hierarchy stays current without a
        // manual reselect. Rows carry their own child watches, so these two are the backstop for add
        // paths that don't fire them; both feed the same per-row queue, which coalesces a burst (a whole
        // imported object, dozens of slots at once) into one reconcile pass next OnChanges. -xlinka
        _subscribedWorld = World;
        if (_subscribedWorld != null)
        {
            _subscribedWorld.OnSlotAdded += OnWorldSlotAdded;
            _subscribedWorld.OnSlotRemoved += OnWorldSlotRemoved;
        }
        // ExpandedSlots stays the synced source of truth, so watching the LIST catches a toggle from
        // any peer as well as our own button, and the reconcile touches only the rows that disagree.
        ExpandedSlots.OnChanged += OnExpandedSlotsChanged;
        // Same for sections: a press sets the rebuild flag only on the presser's machine, and the
        // authority is the one that rebuilds.
        ExpandedComponents.OnChanged += OnExpandedComponentsChanged;
        SessionEditConsent.Changed += OnEditConsentChanged;

        // Taken out of an inventory as an object of its own, not opened as somebody's favourite: it starts on
        // the world root. Only the machine that loaded it knows, and that one says so for everybody.
        if (_fromSave && World != null)
            TakeLoaded(Root.Target ?? World.RootSlot);
        AdoptLocalParts();
    }

    private void OnExpandedSlotsChanged(SyncFieldList<ulong> list)
    {
        _expansionDirty = true;
        MarkChangeDirty();
    }

    private void OnExpandedComponentsChanged(SyncFieldList<ulong> list)
    {
        _componentsDirty = true;
        MarkChangeDirty();
    }

    private void OnWorldSlotAdded(Slot slot)
    {
        // CRITICAL: ignore UI chrome. Building a row spawns slots (Node/Row/Button/Text); each fires this
        // event, and every inspector/panel is UI too - so a second inspector opened from a reference card
        // makes the two panels feed each other rows forever (hard lockup). We only care about SCENE slots,
        // so skip anything under a Canvas (all Helio UI roots at one), plus local render plumbing and
        // anything outside the displayed root. -xlinka
        if (slot == null || IsUiChrome(slot) || IsHiddenFromTree(slot) || !IsUnderDisplayedRoot(slot))
            return;
        // Backstop for any add path that doesn't fire the parent's own child event: queue the
        // PARENT's row. A slot under a collapsed (or absent) row resolves to no row and costs nothing.
        QueueChildSync(slot.Parent?.ReferenceID.RawValue ?? 0);
    }

    private void OnWorldSlotRemoved(Slot slot)
    {
        // Same filters as OnWorldSlotAdded: local render plumbing and slots outside the displayed root
        // never appear in the tree, so their destruction must not re-tessellate it (interaction flows
        // create/destroy local slots constantly while a panel is open).
        if (slot == null || IsUiChrome(slot) || IsHiddenFromTree(slot) || !IsUnderDisplayedRoot(slot))
            return;

        // The displayed ROOT died: that tab is a husk pointing at nothing - close it, and the panel with
        // it when it was the last one. Target reads through RawTarget because a destroyed ref's Target
        // already reads null here. Deferred: this fires mid slot-removal and closing inline would re-enter
        // the event.
        var root = Root.RawTarget;
        if (root != null && (ReferenceEquals(root, slot) || root.IsDestroyed))
        {
            if (World?.IsAuthority == true)
                World.RunSynchronously(CloseActiveTabOrPanel);
            return;
        }

        // A destroyed selection would otherwise leave a dead detail pane pointing at nothing; fall back
        // to the displayed root.
        var selected = Selected.Target;
        if (selected != null && (selected == slot || selected.IsDescendantOf(slot)))
        {
            Selected.Target = Root.Target;
            _componentsDirty = true;
        }
        // Backstop, same as the add side. The slot is off its parent's child list by now, so the
        // parent's reconcile drops that one row; a slot with no row queues nothing.
        var parentId = slot.Parent?.ReferenceID.RawValue ?? 0;
        if (parentId == 0 && _rows.TryGetValue(slot.ReferenceID.RawValue, out var orphan))
            parentId = orphan.Parent?.TargetId ?? 0;
        QueueChildSync(parentId);
    }

    // LIVE detail pane: watch the selected slot's component list so add/remove from ANY source (another
    // user, code, a tool) rebuilds the pane - not just this panel's own buttons. The events fire on the
    // WATCHED scene slot only, so this can't re-enter from the panel building its own UI rows.
    private Slot? _componentWatchSlot;

    private void UpdateComponentSubscription()
    {
        var selected = Selected.Target;
        if (ReferenceEquals(selected, _componentWatchSlot))
            return;
        if (_componentWatchSlot != null)
        {
            _componentWatchSlot.OnComponentAdded -= OnSelectedComponentsChanged;
            _componentWatchSlot.OnComponentRemoved -= OnSelectedComponentsChanged;
            _componentWatchSlot.ParentChanged -= OnSelectedMoved;
            _componentWatchSlot.ActiveChanged -= OnSelectedMoved;
        }
        _componentWatchSlot = selected;
        if (selected != null)
        {
            selected.OnComponentAdded += OnSelectedComponentsChanged;
            selected.OnComponentRemoved += OnSelectedComponentsChanged;
            selected.ParentChanged += OnSelectedMoved;
            selected.ActiveChanged += OnSelectedMoved;
        }
    }

    private void OnSelectedComponentsChanged(Slot slot, Component component)
    {
        _componentsDirty = true;
        MarkChangeDirty();
    }

    // An undoable delete parks the slot, or one above it, in the graveyard instead of destroying it, so
    // OnWorldSlotRemoved never hears; the selection hears the move. -xlinka
    private void OnSelectedMoved(Slot slot)
    {
        if (UndoGraveyard.Holds(slot))
            MarkChangeDirty();
    }

    // Any Helio UI panel (this inspector, another inspector, a material panel, a reference card, the
    // dashboard, a menu) roots at a Canvas; scene content does not. A slot under a Canvas is chrome, not
    // scene structure, so it must never drive a tree rebuild. -xlinka
    private static bool IsUiChrome(Slot slot)
    {
        for (var s = slot; s != null; s = s.Parent)
        {
            if (s.GetComponent<Canvas>() != null || s.GetComponent<PanelShell>() != null)
                return true;
        }
        return false;
    }

    private bool IsUnderDisplayedRoot(Slot slot)
    {
        var root = Root.Target;
        if (root == null)
            return false;
        for (var s = slot; s != null; s = s.Parent)
        {
            if (s == root)
                return true;
        }
        return false;
    }

    private void BuildLayout(UIBuilder ui)
    {
        var page = ui.Current;
        InspectorUI.ApplyTheme(ui, Slot);

        var vLayout = page.AttachComponent<VerticalLayout>();
        vLayout.Spacing.Value = 6f;
        vLayout.PaddingLeft.Value = 6f;
        vLayout.PaddingRight.Value = 6f;
        vLayout.PaddingTop.Value = 6f;
        vLayout.PaddingBottom.Value = 6f;
        vLayout.ForceExpandWidth.Value = true;
        vLayout.ForceExpandHeight.Value = false;

        BuildHeaderBar();
        BuildSelectionRow(page);

        // Destructive actions sit at the far end, away from the ones you press all day.
        InspectorUI.FixedRow(page, "Actions", ChromeRowHeight, out var actionsUi, Slot);
        actionsUi.FontSize(ChromeFontSize);
        InspectorUI.RelayButton(actionsUi, this, "addchild", "+ Child", 74f);
        _insertParentButton.Target = InspectorUI.RelayButton(actionsUi, this, "insertparent", "+ Parent", 84f).Slot;
        _duplicateButton.Target = InspectorUI.RelayButton(actionsUi, this, "duplicate", "Duplicate", 88f).Slot;
        actionsUi.PushStyle().FlexibleWidth(1f);
        actionsUi.Empty("Spacer");
        actionsUi.PopStyle();
        _focusButton.Target = InspectorUI.RelayButton(actionsUi, this, "focus", "Focus", 68f).Slot;
        actionsUi.PushStyle().TextColor(InspectorUI.DangerColor);
        _destroyAssetsButton.Target = InspectorUI.RelayButton(actionsUi, this, "destroyassets", "Destroy Keep Assets", 160f).Slot;
        _destroyButton.Target = InspectorUI.RelayButton(actionsUi, this, "destroy", "Destroy", 76f).Slot;
        actionsUi.PopStyle();

        var bodyRow = page.AddSlot("Body");
        bodyRow.AttachComponent<RectTransform>();
        var bodyLE = bodyRow.AttachComponent<LayoutElement>();
        bodyLE.FlexibleHeight.Value = 1f;
        bodyLE.MinHeight.Value = 400f;
        var bodyLayout = bodyRow.AttachComponent<HorizontalLayout>();
        bodyLayout.Spacing.Value = 4f;
        bodyLayout.ForceExpandHeight.Value = true;
        bodyLayout.ForceExpandWidth.Value = false;

        var leftCol = bodyRow.AddSlot("Hierarchy");
        leftCol.AttachComponent<RectTransform>();
        var leftLE = leftCol.AttachComponent<LayoutElement>();
        leftLE.MinWidth.Value = MinHierarchyWidth;
        leftLE.PreferredWidth.Value = HierarchyWidth.Value;
        leftLE.FlexibleWidth.Value = 0f;
        _hierarchyColumn.Target = leftLE;
        var leftImage = leftCol.AttachComponent<Image>();
        leftImage.Tint.Value = InspectorUI.PaneColor;
        leftImage.CornerRadius.Value = DashTheme.RadiusCard;
        leftCol.AttachComponent<GraphicChunkRoot>();
        var leftLayout = leftCol.AttachComponent<VerticalLayout>();
        leftLayout.Spacing.Value = 2f;
        leftLayout.PaddingLeft.Value = 4f;
        leftLayout.PaddingRight.Value = 4f;
        leftLayout.PaddingTop.Value = 4f;
        leftLayout.PaddingBottom.Value = 4f;
        leftLayout.ForceExpandWidth.Value = true;
        leftLayout.ForceExpandHeight.Value = false;

        InspectorUI.FixedRow(leftCol, "RootRow", 30f, out var rootUi, Slot);
        _rootTitle.Target = ClippedLabel(rootUi, "Root", 1f, ChromeFontSize, InspectorUI.MutedColor, TextHorizontalAlignment.Left);
        rootUi.PushStyle().FontSize(ChromeFontSize);
        InspectorUI.RelayButton(rootUi, this, "rootup", "↑ Up", 56f);
        rootUi.PopStyle();

        _hierarchyContent.Target = BuildScrollList(leftCol, "Tree", TreeSpacing, tree: true);

        BuildDivider(bodyRow);

        var rightCol = bodyRow.AddSlot("Detail");
        rightCol.AttachComponent<RectTransform>();
        var rightLE = rightCol.AttachComponent<LayoutElement>();
        rightLE.FlexibleWidth.Value = 1f;
        rightLE.MinWidth.Value = MinDetailWidth;
        var rightImage = rightCol.AttachComponent<Image>();
        rightImage.Tint.Value = InspectorUI.PaneColor;
        rightImage.CornerRadius.Value = DashTheme.RadiusCard;
        rightCol.AttachComponent<GraphicChunkRoot>();
        var rightLayout = rightCol.AttachComponent<VerticalLayout>();
        rightLayout.Spacing.Value = 3f;
        rightLayout.PaddingLeft.Value = 4f;
        rightLayout.PaddingRight.Value = 4f;
        rightLayout.PaddingTop.Value = 4f;
        rightLayout.PaddingBottom.Value = 4f;
        rightLayout.ForceExpandWidth.Value = true;
        rightLayout.ForceExpandHeight.Value = false;

        _componentsContent.Target = BuildScrollList(rightCol, "Sections", 3f, tree: false);

        // Right padding keeps the button clear of the resize corner.
        var footer = InspectorUI.FixedRow(page, "Footer", ChromeRowHeight + 4f, out var footerUi, Slot);
        footer.GetComponent<HorizontalLayout>()!.PaddingRight.Value = 36f;
        footerUi.PushStyle();
        footerUi.FlexibleWidth(1f);
        InspectorUI.RelayButton(footerUi, this, "attach", "Attach Component...", 0f);
        footerUi.PopStyle();
        BuildResizeHandle();
    }

    private Slot BuildScrollList(Slot column, string name, float spacing, bool tree)
    {
        var host = column.AddSlot(name);
        host.AttachComponent<RectTransform>();
        var hostLE = host.AttachComponent<LayoutElement>();
        hostLE.FlexibleHeight.Value = 1f;
        hostLE.MinHeight.Value = 200f;

        var scrollUi = new UIBuilder(host);
        InspectorUI.ApplyTheme(scrollUi, Slot);
        var scroll = scrollUi.ScrollRect(out var content, null, color.Transparent);
        // Every row here selects, folds or writes the inspected object.
        scroll.RowsActOnRelease.Value = true;
        var viewport = scroll.Slot.GetComponent<RectTransform>()!;
        InspectorUI.FillParent(viewport);
        viewport.OffsetMax.Value = new float2(-(ScrollColumn + 2f), 0);
        BuildScrollControls(host, scroll, content, tree);

        var contentLayout = content.Slot.AttachComponent<VerticalLayout>();
        contentLayout.Spacing.Value = spacing;
        contentLayout.ForceExpandWidth.Value = true;
        contentLayout.ForceExpandHeight.Value = false;
        return content.Slot;
    }

    // The authority keeps every panel's rows, the ones in somebody's hand or pinned to them too. Whatever it
    // writes for that stays inside this panel; see DataModelPermissions.EnterUiUpkeep.
    private Lumora.Warden.PermissionEngine.UpkeepScope? Upkeep() => World?.DataModelPermissions?.EnterUiUpkeep(this);

    public override void OnChanges()
    {
        base.OnChanges();
        if (World?.IsAuthority != true)
            return;
        using var keep = Upkeep();

        // Parked by an undoable delete is gone the same as destroyed: the tab closes, the selection falls back.
        if (Root.Target is { } parkedRoot && UndoGraveyard.Holds(parkedRoot))
        {
            World.RunSynchronously(CloseActiveTabOrPanel);
            return;
        }
        if (Selected.Target is { } parked && UndoGraveyard.Holds(parked))
            Selected.Target = Root.Target;

        bool tabChanged = ActiveTab.GetWasChangedAndClear();
        bool selectionChanged = Selected.GetWasChangedAndClear();
        if (tabChanged)
            _hierarchyDirty = _componentsDirty = true;
        if (selectionChanged)
        {
            _componentsDirty = true;
            UpdateSelectionGizmo();
            UpdateComponentSubscription();
            // An open selector attaches to what is selected now, not to what was selected when it opened.
            if (_selector.Target is { IsDestroyed: false } selector)
                SetRef(selector.TargetSlot, Selected.Target);
        }
        if (Root.GetWasChangedAndClear())
        {
            _hierarchyDirty = true;
            UpdateHeaderActions(Selected.Target);
        }

        // A DIFFERENT tree (root change, "Up") is the only full rebuild left. Everything else below
        // reconciles the rows that actually moved.
        if (_hierarchyDirty)
        {
            _hierarchyDirty = false;
            RebuildHierarchy();
        }
        // A selection made outside the tree (a reference card, a gizmo, another tool) can point at a
        // slot whose row isn't built. Expanding its ancestor chain builds exactly those rows.
        // Restoring a tab preserves branches the user collapsed, even around its selected slot.
        // A new selection still opens its ancestor chain. Check the synced tab on the authority too.
        bool restoredSelection = tabChanged && ReferenceEquals(Selected.Target, ActiveTab.Target?.Selected.Target);
        if (selectionChanged && !restoredSelection)
            EnsureSelectionVisible();
        if (_expansionDirty)
        {
            _expansionDirty = false;
            SyncExpansion();
        }
        DrainChildSync();
        if (selectionChanged)
            UpdateSelectionHighlight();       // the tree just retints two rows, no rebuild
        if (_traceDirty)
        {
            _traceDirty = false;
            TraceHierarchyShape(Root.Target, 0);
        }
        if (_componentsDirty)
        {
            _componentsDirty = false;
            RebuildComponents();
        }
        RefreshWorkspace();
        LeaveRowsUnsaved();
    }

    // Full rebuild: only for a tree rooted somewhere else, where nothing on screen can be reused.
    private void RebuildHierarchy()
    {
        var container = _hierarchyContent.Target;
        var root = Root.Target;
        if (container == null || container.IsDestroyed)
            return;

        var rootTitle = _rootTitle.Target;
        if (rootTitle != null && !rootTitle.IsDestroyed)
            rootTitle.Content.Value = $"Root: {root?.SlotName.Value ?? "<none>"}";

        // Rows left over from the previous pass would stack on top of the new ones; counted before the
        // clear so the trace below can tell "clear failed" apart from "the tree really has that shape".
        int staleRows = container.ChildCount + container.LocalChildCount;
        ClearRows();
        container.DestroyChildren();
        _highlightedId = Selected.Target?.ReferenceID.RawValue ?? 0;
        ClearRootNameSubscription();
        if (root == null || root.IsDestroyed || ImmutableComponent.IsProtected(root))
            return;

        _rootNameField = root.SlotName;
        _rootNameHandler = value =>
        {
            using var keep = Upkeep();
            var title = _rootTitle.Target;
            if (title != null && !title.IsDestroyed)
                title.Content.Value = $"Root: {value}";
        };
        _rootNameField.OnChanged += _rootNameHandler;

        BuildRow(container, root, 0, null, AncestorsPersistent(root), -1);
        InvalidateContentHeight();
        TraceHierarchyShape(root, staleRows);
    }

    // The tree is drawn from ONE slot, so a doubled row can only come from four places: the displayed
    // root has a parent that also renders, it appears twice in a parent's child list, it is its own
    // child, or the previous pass's rows survived the clear. This names which one, once per CHANGE of
    // shape (not per frame), so a bad tree identifies itself in the log instead of needing a repro.
    // Cheap: it runs once per reconcile pass, which is once per structural edit, and a repeat of the
    // same shape doesn't log at all. -xlinka
    private string _lastHierarchyTrace = "";

    private void TraceHierarchyShape(Slot? root, int staleRows)
    {
        if (!Lumora.Core.Logging.Logger.EnableDebug || root == null || root.IsDestroyed)
            return;
        var sb = new System.Text.StringBuilder();
        sb.Append("SceneInspector.Hierarchy: root='").Append(root.SlotName.Value)
          .Append("' id=").Append(root.ReferenceID)
          .Append(" local=").Append(root.IsLocalElement)
          .Append(" isWorldRoot=").Append(root.IsRootSlot)
          // Row count now comes from the row map, which is the tree: the content slot holds ONE
          // top-level node and the rest hang off it.
          .Append(" rows=").Append(_rows.Count)
          .Append(" staleBeforeClear=").Append(staleRows);

        sb.Append(" parents=");
        if (root.Parent == null)
        {
            sb.Append("<none>");
        }
        else
        {
            // Each step also says WHICH list the step below is filed in - a slot that is in neither
            // (MISSING) or in both is exactly the corruption a doubled row would need.
            int guard = 0;
            var child = root;
            for (var parent = root.Parent; parent != null && guard++ < 16; parent = parent.Parent)
            {
                sb.Append('\'').Append(parent.SlotName.Value).Append("'#").Append(parent.ReferenceID)
                  .Append(Contains(parent.Children, child) ? "[children]"
                        : Contains(parent.LocalChildren, child) ? "[localChildren]" : "[MISSING]")
                  .Append('/');
                child = parent;
            }
        }

        int duplicates = 0;
        bool selfChild = false;
        var children = root.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (ReferenceEquals(children[i], root))
                selfChild = true;
            for (int j = i + 1; j < children.Count; j++)
            {
                if (ReferenceEquals(children[i], children[j]))
                    duplicates++;
            }
        }
        sb.Append(" dupChildren=").Append(duplicates).Append(" selfChild=").Append(selfChild);

        sb.Append(" children=[");
        for (int i = 0; i < children.Count && i < 24; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(children[i].SlotName.Value).Append('#').Append(children[i].ReferenceID);
        }
        if (children.Count > 24) sb.Append(", ...");
        sb.Append(']');

        var trace = sb.ToString();
        if (trace == _lastHierarchyTrace)
            return;
        _lastHierarchyTrace = trace;
        Lumora.Core.Logging.Logger.Debug(trace);
    }

    private static bool Contains(System.Collections.Generic.IReadOnlyList<Slot> list, Slot slot)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], slot))
                return true;
        }
        return false;
    }

    private static bool AncestorsPersistent(Slot slot)
    {
        for (var parent = slot.Parent; parent != null; parent = parent.Parent)
        {
            if (!parent.Persistent.Value)
                return false;
        }
        return true;
    }

    private void ClearRootNameSubscription()
    {
        if (_rootNameField != null && _rootNameHandler != null)
            _rootNameField.OnChanged -= _rootNameHandler;
        _rootNameField = null;
        _rootNameHandler = null;
    }

    private void ClearTitleSubscription()
    {
        if (_titleNameField != null && _titleNameHandler != null)
            _titleNameField.OnChanged -= _titleNameHandler;
        _titleNameField = null;
        _titleNameHandler = null;
    }

    public override void OnDestroy()
    {
        SessionEditConsent.Changed -= OnEditConsentChanged;
        if (_subscribedWorld != null)
        {
            _subscribedWorld.OnSlotAdded -= OnWorldSlotAdded;
            _subscribedWorld.OnSlotRemoved -= OnWorldSlotRemoved;
            _subscribedWorld = null;
        }
        ExpandedSlots.OnChanged -= OnExpandedSlotsChanged;
        ExpandedComponents.OnChanged -= OnExpandedComponentsChanged;
        ClearRows();
        ClearRootNameSubscription();
        ClearTitleSubscription();
        ClearSelectionGizmo();
        if (_componentWatchSlot != null)
        {
            _componentWatchSlot.OnComponentAdded -= OnSelectedComponentsChanged;
            _componentWatchSlot.OnComponentRemoved -= OnSelectedComponentsChanged;
            _componentWatchSlot.ParentChanged -= OnSelectedMoved;
            _componentWatchSlot.ActiveChanged -= OnSelectedMoved;
            _componentWatchSlot = null;
        }
        base.OnDestroy();
    }

    // Selecting in the tree shows a transform gizmo on the slot in-world, so selection is spatial,
    // not just a highlighted row. Only a gizmo THIS panel spawned is destroyed on deselect - if the
    // user already had one on that slot (dev tool), selection borrows it and leaves it alone.
    private Slot? _gizmoTarget;
    private bool _gizmoOwned;

    private void UpdateSelectionGizmo()
    {
        var selected = Selected.Target;
        if (ReferenceEquals(selected, _gizmoTarget))
            return;

        ClearSelectionGizmo();

        if (selected == null || selected.IsDestroyed || selected.IsRootSlot)
            return;
        bool existed = Gizmos.GizmoHelper.HasGizmo(selected);
        var gizmo = Gizmos.GizmoHelper.SpawnGizmoFor(selected);
        if (gizmo == null)
            return;
        gizmo.LinkedInspector.Target = this;
        _gizmoTarget = selected;
        _gizmoOwned = !existed;
    }

    private void ClearSelectionGizmo()
    {
        if (_gizmoTarget != null && !_gizmoTarget.IsDestroyed && _gizmoOwned)
            Gizmos.GizmoHelper.DestroyGizmo(_gizmoTarget);
        _gizmoTarget = null;
        _gizmoOwned = false;
    }

    // Retint only the previously-highlighted and newly-selected rows. Selecting a slot must NOT
    // rebuild the tree.
    private void UpdateSelectionHighlight()
    {
        ulong newId = Selected.Target?.ReferenceID.RawValue ?? 0;
        if (newId == _highlightedId)
            return;
        if (_rows.TryGetValue(_highlightedId, out var previous))
        {
            if (previous.Background != null && !previous.Background.IsDestroyed)
                previous.Background.Tint.Value = color.Transparent;
            if (previous.Label != null && !previous.Label.IsDestroyed)
                previous.Label.Color.Value = previous.BaseColor;
        }
        if (_rows.TryGetValue(newId, out var current))
        {
            if (current.Background != null && !current.Background.IsDestroyed)
                current.Background.Tint.Value = InspectorUI.SelectionColor;
            if (current.Label != null && !current.Label.IsDestroyed)
                current.Label.Color.Value = InspectorUI.TextColor;
        }
        _highlightedId = newId;
    }

    // ---- incremental tree ------------------------------------------------------------------------

    private bool IsLiveRow(HierarchyRow row)
        => row.NodeSlot != null && !row.NodeSlot.IsDestroyed
           && _rows.TryGetValue(row.TargetId, out var mapped) && ReferenceEquals(mapped, row);

    private void QueueChildSync(ulong rowId)
    {
        if (rowId == 0 || !_rows.ContainsKey(rowId))
            return;
        if (_pendingChildSync.Add(rowId))
            MarkChangeDirty();
    }

    // One pass per OnChanges over the rows whose child set moved. Removals run across ALL of them
    // before any additions: a reparent is a remove on one container plus an add on another, and
    // pruning first is what guarantees a slot never owns two rows at once.
    private void DrainChildSync()
    {
        if (_pendingChildSync.Count == 0)
            return;

        _syncScratch.Clear();
        foreach (var id in _pendingChildSync)
        {
            if (_rows.TryGetValue(id, out var row))
                _syncScratch.Add(row);
        }
        _pendingChildSync.Clear();

        for (int i = 0; i < _syncScratch.Count; i++)
        {
            if (IsLiveRow(_syncScratch[i]))
                PruneRowChildren(_syncScratch[i]);
        }
        for (int i = 0; i < _syncScratch.Count; i++)
        {
            var row = _syncScratch[i];
            if (!IsLiveRow(row))
                continue;
            BuildRowChildren(row);
            UpdateExpanderGlyph(row);
        }
        _syncScratch.Clear();
        _traceDirty = true;
        InvalidateContentHeight();
    }

    // ExpandedSlots is the synced source of truth, so a toggle from any peer lands here: compare each
    // built row against the list and expand or collapse only the ones that disagree.
    private void SyncExpansion()
    {
        if (_rows.Count == 0)
            return;

        _syncScratch.Clear();
        foreach (var row in _rows.Values)
            _syncScratch.Add(row);
        for (int i = 0; i < _syncScratch.Count; i++)
        {
            var row = _syncScratch[i];
            if (!IsLiveRow(row))
                continue;
            bool wanted = row.Depth == 0 || IsExpanded(ExpandedSlots, row.TargetId);
            if (wanted == row.Expanded)
                continue;
            if (wanted)
                ExpandRow(row);
            else
                CollapseRow(row);
            _traceDirty = true;
        }
        _syncScratch.Clear();
        InvalidateContentHeight();
    }

    private void ExpandRow(HierarchyRow row)
    {
        if (row.Expanded)
            return;
        row.Expanded = true;
        PruneRowChildren(row);
        BuildRowChildren(row);
        UpdateExpanderGlyph(row);
    }

    private void CollapseRow(HierarchyRow row)
    {
        if (!row.Expanded)
            return;
        row.Expanded = false;
        DropChildContainer(row);
        UpdateExpanderGlyph(row);
    }

    // Collapse is ONE destroy: the child container takes the whole subtree's rows with it, and no row
    // outside this node is touched.
    private void DropChildContainer(HierarchyRow row)
    {
        for (int i = 0; i < row.Children.Count; i++)
            UnregisterSubtree(row.Children[i]);
        row.Children.Clear();
        var container = row.ChildContainer;
        row.ChildContainer = null;
        row.OverflowRow = null;
        row.OverflowCount = 0;
        if (container != null && !container.IsDestroyed)
            container.Destroy();
    }

    // Built when the row has children to show, destroyed the moment it doesn't. A node with only its
    // row in it adds no layout spacing of its own, which is what keeps the nested tree at the exact
    // row pitch the flat list had. -xlinka
    private static Slot CreateChildContainer(Slot node)
    {
        var container = node.AddSlot("Children");
        container.AttachComponent<RectTransform>();
        var layout = container.AttachComponent<VerticalLayout>();
        layout.Spacing.Value = TreeSpacing;
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = false;
        return container;
    }

    // Drop the rows whose slot is gone, went local, or moved to another parent. Runs before any
    // additions anywhere, so a reparented slot's old row is dead before its new one is built.
    private void PruneRowChildren(HierarchyRow row)
    {
        if (!row.Expanded || row.Target == null || row.Target.IsDestroyed)
            return;
        for (int i = row.Children.Count - 1; i >= 0; i--)
        {
            var child = row.Children[i];
            var target = child.Target;
            if (target != null && !target.IsDestroyed && !IsHiddenFromTree(target)
                && ReferenceEquals(target.Parent, row.Target))
                continue;
            row.Children.RemoveAt(i);
            DestroyRow(child);
        }
    }

    private void BuildRowChildren(HierarchyRow row)
    {
        var target = row.Target;
        if (!row.Expanded || target == null || target.IsDestroyed)
            return;
        if (!HasVisibleChildren(target) || SealedFromViewer(target, out _))
        {
            // Expanded over nothing: hold no container at all, or the node layout's spacing opens a
            // gap under the row that the flat list never had.
            DropChildContainer(row);
            return;
        }
        if (row.ChildContainer == null || row.ChildContainer.IsDestroyed)
            row.ChildContainer = CreateChildContainer(row.NodeSlot);
        var container = row.ChildContainer;

        // Rows follow scene order, which the child list already is on every machine.
        var ordered = new List<Slot>();
        foreach (var child in target.Children)
        {
            if (!IsHiddenFromTree(child))
                ordered.Add(child);
        }

        var existing = new Dictionary<ulong, HierarchyRow>(row.Children.Count);
        for (int i = 0; i < row.Children.Count; i++)
            existing[row.Children[i].TargetId] = row.Children[i];

        var placed = new List<HierarchyRow>(ordered.Count);
        int refused = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            ulong childId = ordered[i].ReferenceID.RawValue;
            if (existing.TryGetValue(childId, out var childRow))
            {
                // An untouched child keeps its row, its whole subtree and its baked chunk; only its
                // position in the container's child list can change.
                if (childRow.NodeSlot.SiblingIndex != placed.Count)
                    container.MoveChildToIndex(childRow.NodeSlot, placed.Count);
                placed.Add(childRow);
                continue;
            }
            // Budget gates NEW rows only. Rows already on screen stay and keep their place, so a full
            // tree doesn't shuffle when some other branch expands.
            if (_visibleRows >= RowBudget)
            {
                refused++;
                continue;
            }
            var built = BuildRow(container, ordered[i], row.Depth + 1, row,
                row.EffectivePersistent, placed.Count);
            if (built != null)
                placed.Add(built);
        }

        // Anything left over is a row this container no longer owns; drop it rather than strand a
        // slot with two rows or a node slot with no row behind it.
        for (int i = 0; i < placed.Count; i++)
            existing.Remove(placed[i].TargetId);
        foreach (var orphan in existing.Values)
            DestroyRow(orphan);

        row.Children.Clear();
        row.Children.AddRange(placed);

        UpdateOverflowRow(row, refused);
    }

    // The marker the expansion budget leaves behind, so a subtree that didn't fully draw says so
    // where it was cut instead of going quietly short.
    private void UpdateOverflowRow(HierarchyRow row, int hidden)
    {
        var container = row.ChildContainer;
        if (row.OverflowCount == hidden)
        {
            if (hidden > 0 && row.OverflowRow != null && !row.OverflowRow.IsDestroyed
                && container != null && !container.IsDestroyed)
                container.MoveChildToIndex(row.OverflowRow, row.Children.Count);
            return;
        }
        if (row.OverflowRow != null && !row.OverflowRow.IsDestroyed)
            row.OverflowRow.Destroy();
        row.OverflowRow = null;
        row.OverflowCount = hidden;
        if (hidden <= 0 || container == null || container.IsDestroyed)
            return;

        var marker = InspectorUI.FixedRow(container, "More", RowHeight, out var markerUi, Slot);
        marker.GetComponent<HorizontalLayout>()!.PaddingLeft.Value = 4f + IndentFor(row.Depth + 1);
        markerUi.PushStyle();
        markerUi.FlexibleWidth(1f);
        var text = markerUi.Text($"+{hidden} more", InspectorUI.FontSize, InspectorUI.MutedColor);
        InspectorUI.FillParent(text.RectTransform!);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        markerUi.PopStyle();
        row.OverflowRow = marker;
        // A new slot sorts by its offset, and rows moved into place carry theirs, so it is put last.
        container.MoveChildToIndex(marker, row.Children.Count);
    }

    private HierarchyRow? BuildRow(Slot container, Slot target, int depth, HierarchyRow? parent,
        bool ancestorsPersistent, int insertIndex)
    {
        if (target == null || target.IsDestroyed)
            return null;

        // Node wraps the row and, while expanded, the container its children live in. That nesting is
        // what makes collapse one destroy and leaves every sibling's row slot - and its baked chunk -
        // untouched; the flat look survives because the indent is paid as row padding per depth.
        var node = container.AddSlot("Node");
        node.AttachComponent<RectTransform>();
        var nodeLayout = node.AttachComponent<VerticalLayout>();
        nodeLayout.Spacing.Value = TreeSpacing;
        nodeLayout.ForceExpandWidth.Value = true;
        nodeLayout.ForceExpandHeight.Value = false;
        if (insertIndex >= 0)
            container.MoveChildToIndex(node, insertIndex);

        var row = new HierarchyRow
        {
            Target = target,
            TargetId = target.ReferenceID.RawValue,
            Depth = depth,
            Parent = parent,
            NodeSlot = node,
        };

        // A slot owns exactly one row. An entry already here means an add beat the old container's
        // prune; drop the old row rather than leave a duplicate in the tree.
        if (_rows.TryGetValue(row.TargetId, out var stale))
        {
            stale.Parent?.Children.Remove(stale);
            DestroyRow(stale);
        }
        _rows[row.TargetId] = row;
        _visibleRows++;

        BuildRowContent(row, ancestorsPersistent);

        if (depth == 0 || IsExpanded(ExpandedSlots, row.TargetId))
            ExpandRow(row);
        else
            UpdateExpanderGlyph(row);
        return row;
    }

    private void BuildRowContent(HierarchyRow row, bool ancestorsPersistent)
    {
        var target = row.Target;
        var rowSlot = InspectorUI.FixedRow(row.NodeSlot, "Row", RowHeight, out var ui, Slot);
        row.RowSlot = rowSlot;
        var rowBackground = rowSlot.AttachComponent<Image>();
        rowBackground.Tint.Value = color.Transparent;
        rowBackground.CornerRadius.Value = DashTheme.RadiusChip;
        row.Background = rowBackground;
        rowSlot.GetComponent<HorizontalLayout>()!.PaddingLeft.Value = 4f + IndentFor(row.Depth);

        // The ≡ cell at the end of the row pulls a card for the slot.
        var rowSource = rowSlot.AttachComponent<ReferenceProxySource>();
        rowSource.Target.Target = target;

        // Releasing a held slot card over the row reparents that slot under this one (undoable).
        var reparent = rowSlot.AttachComponent<SlotReparentReceiver>();
        reparent.NewParent.Target = target;

        // Flat text rows: transparent expander/name buttons, the selection backing is the only fill.
        ui.PushStyle();
        ui.BackgroundColor(color.Transparent);

        // The expander cell is ALWAYS a button and goes blank when the slot has no children, so a
        // slot gaining or losing its first child only retexts one glyph. Swapping the cell for a
        // spacer would relayout the row and re-mesh its chunk on every add. -xlinka
        ui.PushStyle();
        ui.TextColor(InspectorUI.MutedColor);
        var expander = InspectorUI.RelayButton(ui, this, $"expand:{row.TargetId}", "", 28f);
        row.ExpanderLabel = expander.Slot.GetComponentInChildren<Text>();
        ui.PopStyle();

        ui.PushStyle();
        ui.FlexibleWidth(1f);
        // The name takes what the row has left and no more, and its ellipsis cuts it there. Sized from its own
        // text, a long name pushed the row's buttons over. One unit, since zero reads as unset.
        ui.MinWidth(1f);
        ui.PreferredWidth(1f);
        var nameButton = InspectorUI.RelayButton(ui, this, $"select:{row.TargetId}",
            TreeName(target.SlotName.Value), 0f);
        var nameText = nameButton.Slot.GetComponentInChildren<Text>();
        if (nameText != null)
        {
            nameText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            // User content: slot names render their inline style tags.
            nameText.RichText.Value = true;
            nameText.WordWrap.Value = false;
            nameText.Ellipsis.Value = true;
            // Whatever runs past the name's own cell is trimmed there, so it can never draw over the row's buttons.
            nameText.ClipToRect.Value = true;
        }
        row.Label = nameText;
        ui.PopStyle();
        ui.PopStyle();

        // Unfolds the whole branch. Blank on a childless row, like the expander, for the same reason.
        ui.PushStyle();
        ui.BackgroundColor(color.Transparent);
        ui.TextColor(InspectorUI.MutedColor);
        var branch = InspectorUI.RelayButton(ui, this, $"branch:{row.TargetId}", "", 24f);
        row.BranchLabel = branch.Slot.GetComponentInChildren<Text>();
        ui.PopStyle();
        rowSource.GrabArea.Target = nameButton.Slot;

        ui.PushStyle();
        ui.MinWidth(28f);
        ui.PreferredWidth(28f);
        ui.FlexibleWidth(0f);
        var editorHost = ui.Next("Active");
        editorHost.AttachComponent<HorizontalLayout>();
        var editorUi = new UIBuilder(editorHost);
        InspectorUI.ApplyTheme(editorUi, Slot);
        editorHost.AttachComponent<BooleanMemberEditor>().Setup(target.ActiveSelf, "", editorUi);
        ui.PopStyle();

        ApplyRowTint(row, ancestorsPersistent);
        SubscribeRow(row);
        LockControls(rowSlot, PersonLockTip(target));
    }

    // Persistence tints the label (bright orange = this slot opted out, muted orange = an ancestor
    // did), then effective inactivity fades whatever color that produced. Selection paints over both
    // and the cached base color restores them on deselect.
    private void ApplyRowTint(HierarchyRow row, bool ancestorsPersistent)
    {
        var target = row.Target;
        bool selfPersistent = target.Persistent.Value;
        row.EffectivePersistent = ancestorsPersistent && selfPersistent;
        color baseColor = row.EffectivePersistent
            ? InspectorUI.TextColor
            : selfPersistent ? InspectorUI.NonPersistentInheritedColor : InspectorUI.NonPersistentColor;
        // Inactive reads as dimmer solid text. Fading the alpha left the outline at full strength around a
        // fill that had all but gone, a hollow 1px trace that was hard to read. -xlinka
        if (!target.IsActive)
            baseColor = color.Lerp(baseColor, DashTheme.Panel, target.ActiveSelf.Value ? 0.35f : 0.5f) with { a = 1f };
        row.BaseColor = baseColor;

        bool selected = row.TargetId == (Selected.Target?.ReferenceID.RawValue ?? 0);
        if (row.Label != null && !row.Label.IsDestroyed)
            row.Label.Color.Value = selected ? InspectorUI.TextColor : baseColor;
        if (row.Background != null && !row.Background.IsDestroyed)
            row.Background.Tint.Value = selected ? InspectorUI.SelectionColor : color.Transparent;
    }

    private void RefreshRowTint(HierarchyRow row)
    {
        if (!IsLiveRow(row))
            return;
        ApplyRowTint(row, row.Parent?.EffectivePersistent ?? AncestorsPersistent(row.Target));
    }

    // Persistence is inherited, so a flip retints this row and every BUILT row under it. Rows under a
    // collapsed branch don't exist and pick the new tint up when they're built.
    private void RefreshRowTintTree(HierarchyRow row)
    {
        RefreshRowTint(row);
        for (int i = 0; i < row.Children.Count; i++)
            RefreshRowTintTree(row.Children[i]);
    }

    private static void UpdateExpanderGlyph(HierarchyRow row)
    {
        var label = row.ExpanderLabel;
        if (label == null || label.IsDestroyed || row.Target == null || row.Target.IsDestroyed)
            return;
        bool hasChildren = HasVisibleChildren(row.Target);
        string glyph = hasChildren ? (row.Expanded ? "v" : ">") : "";
        if (label.Content.Value != glyph)
            label.Content.Value = glyph;
        var branch = row.BranchLabel;
        string branchGlyph = hasChildren ? "↘" : "";
        if (branch != null && !branch.IsDestroyed && branch.Content.Value != branchGlyph)
            branch.Content.Value = branchGlyph;
    }

    // Every row watches its OWN slot: child add/remove queues this row's container, an OrderOffset
    // change queues the PARENT's (a slot's offset decides where it sits among its siblings), and
    // name, active and persistent changes retext or retint in place. All of it dies with the row, so
    // a collapsed branch holds no subscriptions at all. -xlinka
    private void SubscribeRow(HierarchyRow row)
    {
        var target = row.Target;
        row.ChildAdded = (_, _) => QueueChildSync(row.TargetId);
        row.ChildRemoved = (_, _) => QueueChildSync(row.TargetId);
        row.OrderChanged = _ => QueueChildSync(row.Parent?.TargetId ?? 0);
        row.ActiveChanged = _ =>
        {
            using var keep = Upkeep();
            RefreshRowTint(row);
        };
        row.PersistentChanged = _ =>
        {
            using var keep = Upkeep();
            RefreshRowTintTree(row);
        };
        row.NameChanged = value =>
        {
            using var keep = Upkeep();
            if (row.Label != null && !row.Label.IsDestroyed)
                row.Label.Content.Value = TreeName(value);
        };

        target.OnChildAdded += row.ChildAdded;
        target.OnChildRemoved += row.ChildRemoved;
        target.OrderOffsetChanged += row.OrderChanged;
        target.ActiveChanged += row.ActiveChanged;
        target.PersistentChanged += row.PersistentChanged;
        target.SlotName.OnChanged += row.NameChanged;
    }

    private static void UnsubscribeRow(HierarchyRow row)
    {
        var target = row.Target;
        if (target != null)
        {
            if (row.ChildAdded != null) target.OnChildAdded -= row.ChildAdded;
            if (row.ChildRemoved != null) target.OnChildRemoved -= row.ChildRemoved;
            if (row.OrderChanged != null) target.OrderOffsetChanged -= row.OrderChanged;
            if (row.ActiveChanged != null) target.ActiveChanged -= row.ActiveChanged;
            if (row.PersistentChanged != null) target.PersistentChanged -= row.PersistentChanged;
            if (row.NameChanged != null && target.SlotName != null)
                target.SlotName.OnChanged -= row.NameChanged;
        }
        row.ChildAdded = null;
        row.ChildRemoved = null;
        row.OrderChanged = null;
        row.ActiveChanged = null;
        row.PersistentChanged = null;
        row.NameChanged = null;
    }

    private void DestroyRow(HierarchyRow row)
    {
        var node = row.NodeSlot;
        UnregisterSubtree(row);
        if (node != null && !node.IsDestroyed)
            node.Destroy();
    }

    // Forget a row and everything under it: the slot destroy that follows takes the UI, this takes
    // the subscriptions and the budget back.
    private void UnregisterSubtree(HierarchyRow row)
    {
        for (int i = 0; i < row.Children.Count; i++)
            UnregisterSubtree(row.Children[i]);
        row.Children.Clear();
        UnsubscribeRow(row);
        if (_rows.TryGetValue(row.TargetId, out var mapped) && ReferenceEquals(mapped, row))
        {
            _rows.Remove(row.TargetId);
            _visibleRows--;
        }
        row.ChildContainer = null;
        row.OverflowRow = null;
        row.OverflowCount = 0;
    }

    private void ClearRows()
    {
        foreach (var row in _rows.Values)
        {
            UnsubscribeRow(row);
            row.Children.Clear();
        }
        _rows.Clear();
        _visibleRows = 0;
        _pendingChildSync.Clear();
        _syncScratch.Clear();
    }

    // A selection from outside the tree can sit under collapsed ancestors. Expand the chain between
    // the displayed root and the selection - those rows and nothing else.
    private void EnsureSelectionVisible()
    {
        var selected = Selected.Target;
        var root = Root.Target;
        if (selected == null || selected.IsDestroyed || root == null || root.IsDestroyed)
            return;
        if (_rows.ContainsKey(selected.ReferenceID.RawValue))
            return;

        bool underRoot = false;
        for (var s = selected; s != null; s = s.Parent)
        {
            if (ReferenceEquals(s, root))
            {
                underRoot = true;
                break;
            }
        }
        if (!underRoot)
            return;

        for (var s = selected.Parent; s != null; s = s.Parent)
        {
            ulong id = s.ReferenceID.RawValue;
            if (!IsExpanded(ExpandedSlots, id))
                ExpandedSlots.Add(id);
            if (ReferenceEquals(s, root))
                break;
        }
    }

    // The sizer latches on the row COUNT of its DIRECT children, and the tree hands it a single
    // top-level node - so a collapse (shorter, same count) would never re-pin. Poke it whenever the
    // shape changes. It re-measures on its next update and the ScrollRect holds its normalized
    // position, so the view doesn't jump. -xlinka
    private void InvalidateContentHeight()
    {
        var container = _hierarchyContent.Target;
        if (container == null || container.IsDestroyed)
            return;
        container.GetComponent<ScrollContentSizer>()?.Invalidate();
    }

    // Local-only children are render plumbing (canvas chunks, material caches); they never show, and
    // neither does a protected subtree.
    private static bool HasVisibleChildren(Slot slot)
    {
        foreach (var child in slot.Children)
        {
            if (!IsHiddenFromTree(child))
                return true;
        }
        return false;
    }

    // Right pane: the slot's own member rows and action rows first, then one section per component.
    private void RebuildComponents()
    {
        var container = _componentsContent.Target;
        var selected = Selected.Target;
        if (container == null || container.IsDestroyed)
            return;

        var title = _selectedTitle.Target;
        if (title != null && !title.IsDestroyed)
            title.Content.Value = selected == null ? "<i>nothing selected</i>" : selected.SlotName.Value ?? "Slot";

        UpdateHeaderActions(selected);

        ClearTitleSubscription();
        if (selected != null && !selected.IsDestroyed)
        {
            _titleNameField = selected.SlotName;
            _titleNameHandler = value =>
            {
                using var keep = Upkeep();
                var titleText = _selectedTitle.Target;
                if (titleText != null && !titleText.IsDestroyed)
                    titleText.Content.Value = value ?? "Slot";
            };
            _titleNameField.OnChanged += _titleNameHandler;
        }

        container.DestroyChildren();
        if (selected == null || selected.IsDestroyed)
            return;

        if (SealedFromViewer(selected, out var sealedReason))
        {
            InspectorUI.FixedRow(container, "Sealed", InspectorUI.RowHeight * 2f, out var sealedUi, Slot);
            sealedUi.PushStyle();
            sealedUi.FlexibleWidth(1f);
            var owner = Security.ItemSeals.SealOf(selected)?.Owner;
            var note = sealedUi.Text(owner != null
                    ? Security.ProtectionLocale.InspectorHidden(Security.ItemSeals.NameOf(World, owner)).Resolve()
                    : sealedReason ?? string.Empty,
                InspectorUI.FontSize, InspectorUI.MutedColor);
            InspectorUI.FillParent(note.RectTransform!);
            note.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            note.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            sealedUi.PopStyle();
            return;
        }

        BuildConsentRows(container, selected);
        BuildSlotSection(container, selected);

        _builtOrder = OrderSignature(selected);
        foreach (var component in InspectedComponents(selected))
            BuildComponentSection(container, component);
        ApplyPersonLock();
    }

    // Part of somebody else: what the viewer may do about it, and while nothing is asked yet, the way to ask. The
    // fields below stay as they are and the gate refuses what they would write, so this line is what says why.
    // -xlinka
    private void BuildConsentRows(Slot container, Slot selected)
    {
        var viewer = Viewer;
        var person = SessionEditConsent.PersonOf(selected, viewer);
        if (person == null || viewer == null)
            return;

        string name = SessionPermissionsPanel.DisplayName(person);
        var state = SessionEditConsent.StateOf(viewer, person);
        long left = SessionEditConsent.SecondsLeft(viewer, person);
        bool rootItself = ReferenceEquals(selected, person.Root?.Slot);
        LocaleText line;
        color tint = InspectorUI.MutedColor;
        if (state == EditAskState.Granted)
        {
            line = rootItself ? EditConsentLocale.InspectorRoot(name) : EditConsentLocale.InspectorGranted(name);
            tint = rootItself ? InspectorUI.MutedColor : DashTheme.Positive;
        }
        else
        {
            line = state switch
            {
                EditAskState.Pending => EditConsentLocale.InspectorPending(name),
                EditAskState.Denied => EditConsentLocale.InspectorDenied(name, EditConsentLocale.Clock(left)),
                EditAskState.Waiting => EditConsentLocale.InspectorWaiting(name, EditConsentLocale.Clock(left)),
                _ => EditConsentLocale.InspectorLocked(name),
            };
            if (state == EditAskState.Denied)
                tint = DashTheme.Warning;
        }

        InspectorUI.FixedRow(container, "Consent", InspectorUI.RowHeight, out var noteUi, Slot);
        noteUi.PushStyle();
        noteUi.FlexibleWidth(1f);
        var note = noteUi.Text(line.Resolve(), InspectorUI.FontSize, tint);
        InspectorUI.FillParent(note.RectTransform!);
        note.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        note.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        noteUi.PopStyle();

        if (state == EditAskState.None)
        {
            InspectorUI.FixedRow(container, "ConsentAsk", InspectorUI.RowHeight, out var askUi, Slot);
            InspectorUI.RelayButton(askUi, this, "askedit", EditConsentLocale.InspectorAsk(name).Resolve(), 0f);
        }
        // A no or a wait runs out with nothing on either record moving: look again then.
        if (left > 0)
            World?.RunInSeconds(left + 1, () =>
            {
                if (IsDestroyed || !ReferenceEquals(Selected.Target, selected))
                    return;
                _componentsDirty = true;
                RunApplyChangesSafe();
            });
    }

    // READ-ONLY FOR SOMEBODY ELSE'S THINGS
    //
    // Without the person's yes every control that would change what is shown goes grey and dead, with the reason as
    // its tip: the member rows and their buttons, the component headers' Dup, x and gizmo, the slot actions, + Child,
    // + Parent, Duplicate, Destroy, Attach Component, a row's active box. Looking stays live: folding sections and
    // rows, selecting, Focus, Up, Jump To and the ask itself. With the yes the same controls come back, and they go
    // again when it is taken back. Only what this pass turned off is ever turned back on. -xlinka
    private readonly HashSet<InteractionElement> _personLocked = new();
    // The words on a locked control, faded, and the colour each had: the stock disabled fill is a light grey that
    // reads louder on this dark panel than the live control did.
    private readonly Dictionary<Text, color> _fadedText = new();

    private static readonly string[] SelectionActions = { "addchild", "insertparent", "duplicate", "destroyassets", "destroy", "attach" };

    // The tip for every control that would change slot for the viewer, or null when nothing stands in the way.
    private string? PersonLockTip(Slot? slot)
    {
        var viewer = Viewer;
        var person = SessionEditConsent.PersonOf(slot, viewer);
        if (person == null || viewer == null || !SessionEditConsent.Refuses(slot, viewer))
            return null;
        return EditConsentLocale.ReadOnlyTip(SessionPermissionsPanel.DisplayName(person)).Resolve();
    }

    private void ApplyPersonLock()
    {
        if (World?.IsAuthority != true)
            return;

        // What was turned off and is no longer anybody else's comes back first.
        foreach (var element in new List<InteractionElement>(_personLocked))
        {
            if (element.IsDestroyed || element.Slot == null)
                _personLocked.Remove(element);
        }
        foreach (var text in new List<Text>(_fadedText.Keys))
        {
            if (text.IsDestroyed)
                _fadedText.Remove(text);
        }

        var selected = Selected.Target;
        string? tip = PersonLockTip(selected);
        if (_componentsContent.Target is { IsDestroyed: false } pane)
            LockControls(pane, tip);
        foreach (var relay in Slot.GetComponentsInChildren<InspectorButtonRelay>())
        {
            if (Array.IndexOf(SelectionActions, relay.Argument.Value ?? "") >= 0 && relay.Slot.GetComponent<InteractionElement>() is { } button)
                SetLocked(button, tip);
        }
        foreach (var row in _rows.Values)
        {
            if (row.RowSlot is { IsDestroyed: false } rowSlot && row.Target is { IsDestroyed: false } target)
                LockControls(rowSlot, PersonLockTip(target));
        }
    }

    private void LockControls(Slot root, string? tip)
    {
        if (World?.IsAuthority != true)
            return;
        foreach (var element in root.GetComponentsInChildren<InteractionElement>())
        {
            if (IsViewOnly(element))
                continue;
            SetLocked(element, tip);
        }
    }

    private void SetLocked(InteractionElement element, string? tip)
    {
        if (tip != null)
        {
            if (!element.Interactable.Value && !_personLocked.Contains(element))
                return;
            if (element.Interactable.Value)
                element.Interactable.Value = false;
            if (element.Tip.Value != tip)
                element.Tip.Value = tip;
            if (_personLocked.Add(element))
                Fade(element, true);
        }
        else if (_personLocked.Remove(element))
        {
            element.Interactable.Value = true;
            element.Tip.Value = string.Empty;
            Fade(element, false);
        }
    }

    private void Fade(InteractionElement element, bool faded)
    {
        var slot = element.Slot;
        foreach (var driver in slot.GetComponentsInChildren<ColorDriver>())
        {
            if (driver.Interaction.Target is { } owner && !ReferenceEquals(owner, element))
                continue;
            var normal = driver.NormalColor.Value;
            var dim = color.Lerp(normal, InspectorUI.PaneColor, 0.6f) with { a = normal.a };
            if (faded && !driver.DisabledColor.IsDriven && driver.DisabledColor.Value != dim)
                driver.DisabledColor.Value = dim;
        }
        foreach (var text in slot.GetComponentsInChildren<Text>())
        {
            if (text.Color.IsDriven)
                continue;
            if (faded && !_fadedText.ContainsKey(text))
            {
                _fadedText[text] = text.Color.Value;
                text.Color.Value = color.Lerp(text.Color.Value, InspectorUI.PaneColor, 0.55f) with { a = text.Color.Value.a };
            }
            else if (!faded && _fadedText.Remove(text, out var was))
            {
                text.Color.Value = was;
            }
        }
    }

    // Controls that only look: folding, selecting, moving the view, the person line and its ask.
    private static bool IsViewOnly(InteractionElement element)
    {
        var slot = element.Slot;
        for (var at = slot; at != null; at = at.Parent)
        {
            if (at.Name.Value is "Consent" or "ConsentAsk")
                return true;
            if (at.GetComponent<SceneInspectorPanel>() != null)
                break;
        }
        if (slot?.GetComponent<InspectorButtonRelay>() is not { } relay)
            return false;
        var argument = relay.Argument.Value ?? "";
        return argument is "expandall" or "collapseall" or "jumpto" or "focus" or "rootup" or "askedit"
            || argument.StartsWith("comp:", StringComparison.Ordinal)
            || argument.StartsWith("expand:", StringComparison.Ordinal)
            || argument.StartsWith("select:", StringComparison.Ordinal)
            || argument.StartsWith("branch:", StringComparison.Ordinal);
    }

    // Either side of an ask moved in this world: the person line is rebuilt when it is about them or the viewer.
    private void OnEditConsentChanged(World world, User who)
    {
        if (IsDestroyed || !ReferenceEquals(world, World) || World?.IsAuthority != true)
            return;
        var viewer = Viewer;
        var person = SessionEditConsent.PersonOf(Selected.Target, viewer);
        if (person == null || (!ReferenceEquals(who, person) && !ReferenceEquals(who, viewer)))
            return;
        _componentsDirty = true;
        RunApplyChangesSafe();
    }

    // The components that get a section, in the order they show.
    private IEnumerable<Component> InspectedComponents(Slot slot)
    {
        foreach (var component in OrderedComponents(slot))
        {
            if (component == null || component.IsDestroyed)
                continue;
            // Class-level opt-out: pure render plumbing renders no section at all.
            if (component.GetType().GetCustomAttribute<HideInInspectorAttribute>() != null)
                continue;
            yield return component;
        }
    }

    private bool HasInspectedComponents(Slot slot)
    {
        foreach (var _ in InspectedComponents(slot))
            return true;
        return false;
    }

    // Every section the slot shows, opened or closed in one go. Other slots keep theirs.
    private void SetComponentsExpanded(Slot slot, bool expanded)
    {
        foreach (var component in InspectedComponents(slot))
        {
            ulong id = component.ReferenceID.RawValue;
            if (IsExpanded(ExpandedComponents, id) != expanded)
                ToggleExpanded(ExpandedComponents, id);
        }
    }

    // + Parent, Dup, Destroy and Destroy Keep Assets are gone for the world root, not greyed: the root
    // is not a thing any of them can be done to, and an offer that always refuses reads as a bug. The
    // relay handlers refuse it as well, so a peer driving the synced Selected field at a button that is
    // hidden on their own screen gets nothing. + Child and Attach Component stay: building ON the root
    // is ordinary work. Focus is gone while the selection already is the hierarchy's root. -xlinka
    private void UpdateHeaderActions(Slot? selected)
    {
        bool offer = selected != null && !selected.IsDestroyed && !selected.IsRootSlot;
        SetActionVisible(_insertParentButton.Target, offer);
        SetActionVisible(_duplicateButton.Target, offer);
        SetActionVisible(_destroyAssetsButton.Target, offer);
        SetActionVisible(_destroyButton.Target, offer);
        SetActionVisible(_focusButton.Target, selected != null && !selected.IsDestroyed && !ReferenceEquals(selected, Root.Target));
    }

    private static void SetActionVisible(Slot? button, bool visible)
    {
        if (button == null || button.IsDestroyed)
            return;
        if (button.ActiveSelf.Value != visible)
            button.ActiveSelf.Value = visible;
    }

    // Editable rows for the slot itself (Name, Parent, Tag, Active, Persistent, Position, Rotation,
    // Scale, OrderOffset) with the axis legend heading the transform rows, then the transform and
    // parenting actions.
    private void BuildSlotSection(Slot container, Slot selected)
    {
        WorkerInspectorBuilder.BuildMemberRows(selected, container, Slot, name =>
        {
            if (name == "Position")
                BuildAxisLegend(container);
        });

        InspectorUI.FixedRow(container, "ResetRow", 34f, out var resetUi, Slot);
        InspectorUI.MemberLead(resetUi, "Reset:");
        InspectorUI.Columns(resetUi, "Buttons");
        InspectorUI.RelayButton(resetUi, this, "resetpos", "Position", 0f);
        InspectorUI.RelayButton(resetUi, this, "resetrot", "Rotation", 0f);
        InspectorUI.RelayButton(resetUi, this, "resetscale", "Scale", 0f);
        resetUi.NestOut();

        InspectorUI.FixedRow(container, "ResetAllRow", InspectorUI.RowHeight, out var resetAllUi, Slot);
        InspectorUI.RelayButton(resetAllUi, this, "resetall", "Reset All", 0f);

        InspectorUI.FixedRow(container, "PivotRow", 34f, out var pivotUi, Slot);
        pivotUi.PushStyle();
        pivotUi.FlexibleWidth(1f);
        InspectorUI.RelayButton(pivotUi, this, "pivot", "Create Pivot At Center", 0f);
        pivotUi.PopStyle();

        InspectorUI.FixedRow(container, "FocusRow", 34f, out var focusUi, Slot);
        InspectorUI.RelayButton(focusUi, this, "jumpto", "Jump To", 0f);
        InspectorUI.RelayButton(focusUi, this, "bringto", "Bring To", 0f);

        InspectorUI.FixedRow(container, "ParentRow", 34f, out var parentUi, Slot);
        InspectorUI.MemberLead(parentUi, "Parent under:");
        InspectorUI.Columns(parentUi, "Buttons");
        InspectorUI.RelayButton(parentUi, this, "parentuser", "Local User Space", 0f);
        InspectorUI.RelayButton(parentUi, this, "parentroot", "World Root", 0f);
        parentUi.NestOut();

        // Last, right above the sections it opens and closes.
        if (!HasInspectedComponents(selected))
            return;
        InspectorUI.FixedRow(container, "ComponentsRow", 34f, out var componentsUi, Slot);
        InspectorUI.MemberLead(componentsUi, "Components:");
        InspectorUI.Columns(componentsUi, "Buttons");
        InspectorUI.RelayButton(componentsUi, this, "expandall", "Expand All", 0f);
        InspectorUI.RelayButton(componentsUi, this, "collapseall", "Collapse All", 0f);
        componentsUi.NestOut();
    }

    // X red, Y green, Z blue, each centred over its column of the vector rows below it: built from the same
    // lead and columns as a member row, so the letters stay over the fields at any panel width.
    private void BuildAxisLegend(Slot container)
    {
        InspectorUI.FixedRow(container, "AxisLegend", 24f, out var ui, Slot);
        InspectorUI.MemberLead(ui);
        InspectorUI.Columns(ui, "Axes");
        AddAxisLabel(ui, "X", InspectorUI.AxisXColor);
        AddAxisLabel(ui, "Y", InspectorUI.AxisYColor);
        AddAxisLabel(ui, "Z", InspectorUI.AxisZColor);
        ui.NestOut();
    }

    private static void AddAxisLabel(UIBuilder ui, string label, color tint)
    {
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var text = ui.Text(label, InspectorUI.FontSize, tint);
        InspectorUI.FillParent(text.RectTransform!);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        ui.PopStyle();
    }

    // Component section: header row (expander, type name, Enabled toggle, duplicate, destroy),
    // then the member rows when expanded.
    private void BuildComponentSection(Slot container, Component component)
    {
        ulong componentId = component.ReferenceID.RawValue;
        bool expanded = IsExpanded(ExpandedComponents, componentId);

        var header = InspectorUI.FixedRow(container, component.GetType().Name, InspectorUI.RowHeight + 4f, out var ui, Slot);
        var headerImage = header.AttachComponent<Image>();
        headerImage.Tint.Value = InspectorUI.HeaderColor;
        headerImage.CornerRadius.Value = DashTheme.RadiusControl;

        // Only the handle at the left pulls the component's card, so a squeeze while pressing anything
        // else on the header never hands you one.
        var headerSource = header.AttachComponent<ReferenceProxySource>();
        headerSource.Target.Target = component;
        BuildComponentHandle(ui, component, headerSource);

        ui.PushStyle();
        ui.BackgroundColor(color.Transparent);
        ui.TextColor(InspectorUI.MutedColor);
        SetChipColors(InspectorUI.RelayButton(ui, this, $"comp:{componentId}", expanded ? "v" : ">", 26f), color.Transparent);
        ui.PopStyle();

        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var nameText = ui.Text(SyncMemberEditorBuilder.NiceTypeName(component.GetType()),
            InspectorUI.FontSize + 1f, InspectorUI.TextColor);
        InspectorUI.FillParent(nameText.RectTransform!);
        nameText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        nameText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        ui.PopStyle();

        if (component.TryGetField("Enabled") is IField enabledField)
        {
            ui.PushStyle();
            ui.MinWidth(28f);
            ui.PreferredWidth(28f);
            ui.FlexibleWidth(0f);
            var editorHost = ui.Next("Enabled");
            editorHost.AttachComponent<HorizontalLayout>();
            var editorUi = new UIBuilder(editorHost);
            InspectorUI.ApplyTheme(editorUi, Slot);
            editorHost.AttachComponent<BooleanMemberEditor>().Setup(enabledField, "", editorUi);
            ui.PopStyle();
        }

        // Gizmo toggle, only for components something is actually registered to draw. Tinted while the
        // gizmo is up, so the header says whether pressing it puts one up or takes one down - a button
        // that looks identical in both states is the fastest way to end up with fifteen wireframes on
        // one object. -xlinka
        if (Gizmos.GizmoHelper.CanGizmo(component))
        {
            bool gizmoUp = Gizmos.GizmoHelper.HasComponentGizmo(component);
            ui.PushStyle();
            ui.TextColor(gizmoUp ? InspectorUI.CyanColor : InspectorUI.MutedColor);
            InspectorUI.RelayButton(ui, this, $"gizmo:{componentId}", "Giz", 40f);
            ui.PopStyle();
        }

        // The world's config components get neither button. Dup would mint a second copy of a
        // single-instance component and X would try to take the world's settings off the root; both are
        // refused at the datamodel, so the header does not offer them in the first place. -xlinka
        if (!WorldConfigLock.IsConfigComponent(component))
        {
            InspectorUI.RelayButton(ui, this, $"dupcomp:{componentId}", "Dup", 46f);

            ui.PushStyle();
            ui.TextColor(InspectorUI.DangerColor).FontSize(InspectorUI.FontSize + 3f);
            InspectorUI.RelayButton(ui, this, $"remove:{componentId}", "×", 30f);
            ui.PopStyle();
        }

        if (!expanded)
            return;

        BuildComponentNote(container, component);

        if (component is ICustomInspector custom)
        {
            var customHost = container.AddSlot("Custom");
            customHost.AttachComponent<RectTransform>();
            var hostLE = customHost.AttachComponent<LayoutElement>();
            hostLE.MinHeight.Value = 60f;
            var customUi = new UIBuilder(customHost);
            InspectorUI.ApplyTheme(customUi, Slot);
            try { custom.BuildInspectorUI(customUi); }
            catch { /* logged by the component's own guards; leave the host empty */ }
        }
        else
        {
            WorkerInspectorBuilder.BuildMemberRows(component, container, Slot);
        }

        // Parameterless action methods reflect into clickable rows below the members.
        WorkerInspectorBuilder.BuildMethodRows(component, container, Slot);

        // Optional appended body (mesh statistics and the like), AFTER the reflected rows.
        if (component is ICustomInspectorUI extra)
        {
            var bodyHost = container.AddSlot("CustomBody");
            bodyHost.AttachComponent<RectTransform>();
            var bodyLayout = bodyHost.AttachComponent<VerticalLayout>();
            bodyLayout.Spacing.Value = 2f;
            bodyLayout.ForceExpandWidth.Value = true;
            bodyLayout.ForceExpandHeight.Value = false;
            var bodyUi = new UIBuilder(bodyHost);
            InspectorUI.ApplyTheme(bodyUi, Slot);
            try { extra.BuildInspectorBody(bodyUi); }
            catch { /* a broken body must not take the section down; leave the host empty */ }
        }
    }

    internal static bool IsExpanded(SyncFieldList<ulong> list, ulong id)
    {
        foreach (var value in list)
        {
            if (value == id)
                return true;
        }
        return false;
    }

    private static void ToggleExpanded(SyncFieldList<ulong> list, ulong id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == id)
            {
                list.RemoveAt(i);
                return;
            }
        }
        list.Add(id);
    }

    // destructive actions detour through a confirmation on the pressing user's radial menu;
    // everything else falls straight through to the plain handler, with the presser remembered for the
    // actions that are about THEM (pin to me, jump to my root)
    public void HandleInspectorAction(string argument, UIInteractionContext context)
    {
        if (TryConfirmDestructiveAction(argument, context))
            return;
        _actionActor = context.Actor;
        try
        {
            HandleInspectorAction(argument);
        }
        finally
        {
            _actionActor = null;
        }
    }

    private static readonly float[] DestroyConfirmFill = { 0.48f, 0.15f, 0.18f, 0.92f };

    private bool TryConfirmDestructiveAction(string argument, UIInteractionContext context)
    {
        string? title = null;
        if (argument == "destroy")
            title = $"Destroy {Selected.Target?.SlotName.Value ?? "slot"}?";
        else if (argument == "destroyassets")
            title = $"Destroy {Selected.Target?.SlotName.Value ?? "slot"}, keep assets?";
        else if (argument.StartsWith("remove:", StringComparison.Ordinal) && ulong.TryParse(argument[7..], out ulong id))
        {
            var component = World?.ReferenceController?.GetObjectOrNull(new RefID(id)) as Component;
            title = $"Destroy {component?.GetType().Name ?? "component"}?";
        }
        if (title == null)
            return false;

        var menu = context.Actor?.Root?.Slot?.GetComponentInChildren<UI.ContextMenuSystem>();
        if (menu == null || menu.IsShown)
            return false; // no menu to ask with - act immediately so the button never goes dead

        var ctx = MemberActionsRelay.BuildMenuContext(this, context.Actor);
        string confirmed = argument;
        menu.OpenConfirm(title, "Destroy", DestroyConfirmFill,
            () => { if (!IsDestroyed) HandleInspectorAction(confirmed); }, ctx);
        return true;
    }

    public void HandleInspectorAction(string argument)
    {
        try
        {
            HandleInspectorActionCore(argument);
        }
        catch (UnauthorizedAccessException ex)
        {
            // The gate already logged the denial once with the actor and target. Letting it escape turned a
            // refused click into an "Error in change application" for the whole panel.
            Lumora.Core.Logging.Logger.Warn($"Inspector: '{argument}' refused: {ex.Message}");
        }
    }

    private void HandleInspectorActionCore(string argument)
    {
        if (string.IsNullOrEmpty(argument))
            return;

        if (HandleWorkspaceAction(argument, _actionActor ?? World?.LocalUser))
            return;

        if (argument.StartsWith("expand:", StringComparison.Ordinal) && ulong.TryParse(argument[7..], out ulong slotId))
        {
            // The expander cell exists on every row (blank when childless) so gaining a first child
            // only retexts a glyph - so refuse the toggle rather than park a dead id in the list.
            if (World?.ReferenceController?.GetObjectOrNull(new RefID(slotId)) is Slot expandTarget
                && !HasVisibleChildren(expandTarget))
                return;
            // The list's own change event drives the reconcile, which expands or collapses THAT row.
            ToggleExpanded(ExpandedSlots, slotId);
            RunApplyChangesSafe();
            return;
        }
        if (argument.StartsWith("select:", StringComparison.Ordinal) && ulong.TryParse(argument[7..], out ulong selId))
        {
            // Just set the synced field. OnChanges catches the change and retints two rows +
            // rebuilds the detail pane - NO hierarchy rebuild.
            if (World?.ReferenceController?.GetObjectOrNull(new RefID(selId)) is Slot slot)
                Selected.Target = slot;
            RunApplyChangesSafe();
            return;
        }
        if (argument.StartsWith("comp:", StringComparison.Ordinal) && ulong.TryParse(argument[5..], out ulong compId))
        {
            ToggleExpanded(ExpandedComponents, compId);
            _componentsDirty = true;
            RunApplyChangesSafe();
            return;
        }
        if (argument.StartsWith("remove:", StringComparison.Ordinal) && ulong.TryParse(argument[7..], out ulong remId))
        {
            // Serialize-then-destroy so the X is undoable (the batch re-attaches + loads the state back).
            if (World?.ReferenceController?.GetObjectOrNull(new RefID(remId)) is Component component
                && !WorldConfigLock.IsConfigComponent(component))
                ComponentUndo.RecordDestroy(this, component);
            _componentsDirty = true;
            RunApplyChangesSafe();
            return;
        }
        if (argument.StartsWith("gizmo:", StringComparison.Ordinal) && ulong.TryParse(argument[6..], out ulong gizId))
        {
            // Explicit only: selecting a slot never spawns these, so the header button is the one way
            // in and the one way back out.
            if (World?.ReferenceController?.GetObjectOrNull(new RefID(gizId)) is Component gizmoTarget)
                Gizmos.GizmoHelper.ToggleComponentGizmo(gizmoTarget);
            // Rebuild the sections so the button re-tints to whatever the toggle just left behind.
            _componentsDirty = true;
            RunApplyChangesSafe();
            return;
        }
        if (argument.StartsWith("dupcomp:", StringComparison.Ordinal) && ulong.TryParse(argument[8..], out ulong dupId))
        {
            if (World?.ReferenceController?.GetObjectOrNull(new RefID(dupId)) is Component source
                && source.Slot != null && !source.Slot.IsDestroyed
                && !WorldConfigLock.IsConfigComponent(source))
            {
                var clone = source.Slot.AttachComponent(source.GetType());
                clone.CopyValues(source);
                ComponentUndo.RecordAttach(this, clone);
            }
            _componentsDirty = true;
            RunApplyChangesSafe();
            return;
        }

        switch (argument)
        {
            case "askedit":
            {
                // On the presser's own machine, asked of the person the selection is part of. Nothing else moves;
                // the line changes once the ask is on the presser's record.
                var world = World;
                var person = SessionEditConsent.PersonOf(Selected.Target, world?.LocalUser);
                if (world != null && person != null)
                    SessionEditConsent.Ask(world, person);
                return;
            }
            case "rootup":
            {
                var root = Root.Target;
                if (root == null || root.IsRootSlot || root.Parent == null) return;
                Root.Target = root.Parent;
                break;
            }
            case "focus":
            {
                // The selected slot becomes this tab's hierarchy root, the way a new tab would open on it,
                // without the tab. ↑ Up walks back out.
                var selected = Selected.Target;
                if (selected == null || selected.IsDestroyed || ReferenceEquals(selected, Root.Target)
                    || RefuseProtected(selected, "focus"))
                    return;
                Root.Target = selected;
                break;
            }
            case "addchild":
            {
                var selected = Selected.Target;
                if (selected == null) return;
                var child = selected.AddSlot("Slot");
                RecordUndo(SlotExistenceUndoBatch.Created(World, new[] { child }, UndoLocale.AddChild));
                if (!IsExpanded(ExpandedSlots, selected.ReferenceID.RawValue))
                    ExpandedSlots.Add(selected.ReferenceID.RawValue);
                Selected.Target = child;
                break;
            }
            case "insertparent":
            {
                InsertParentAboveSelected();
                break;
            }
            case "pivot":
            {
                CreatePivotAboveSelected();
                break;
            }
            case "duplicate":
            {
                var selected = Selected.Target;
                if (selected == null || selected == Root.Target || selected.IsRootSlot) return;
                if (Security.ItemSeals.RefusesHere(selected, Security.SealRule.SaveCopy)) return;
                var copy = selected.Duplicate();
                if (copy == null) return;
                RecordUndo(SlotExistenceUndoBatch.Created(World, new[] { copy }, UndoLocale.Duplicate));
                Selected.Target = copy;
                break;
            }
            case "destroy":
            {
                var selected = Selected.Target;
                if (selected == null || selected == Root.Target || selected.IsRootSlot) return;
                var parent = selected.Parent;
                // Parked in the graveyard instead of destroyed, so the step reverses cleanly.
                // Only park when a history exists to evict it later; otherwise destroy for real.
                var manager = FindUndoManager();
                var batch = manager != null ? SlotExistenceUndoBatch.Destroy(World, new[] { selected }) : null;
                if (batch != null)
                    manager!.Record(batch);
                else
                    selected.Destroy();
                Selected.Target = parent;
                break;
            }
            case "destroyassets":
            {
                var selected = Selected.Target;
                if (selected == null || selected == Root.Target || selected.IsRootSlot) return;
                var parent = selected.Parent;
                // The recording variant parks emptied slots and serializes stripped components so the
                // step reverses; it declines when there is nowhere to park, and then the plain strip
                // runs instead so the button is never dead.
                var manager = FindUndoManager();
                var batch = manager != null ? PreserveAssetsUndoBatch.Perform(selected) : null;
                if (batch != null)
                    manager!.Record(batch);
                else
                    selected.DestroyPreservingAssets();
                Selected.Target = parent;
                break;
            }
            case "expandall":
            case "collapseall":
            {
                var selected = Selected.Target;
                if (selected == null || selected.IsDestroyed) return;
                SetComponentsExpanded(selected, argument == "expandall");
                _componentsDirty = true;
                RunApplyChangesSafe();
                return;
            }
            case "resetpos":
            {
                // Editors watch the field and refresh in place; no rebuild needed.
                var selected = Selected.Target;
                if (selected == null || selected.IsRootSlot) return;
                var undo = SlotTransformUndoBatch.Begin(selected, UndoLocale.ResetPosition);
                selected.LocalPosition.Value = float3.Zero;
                RecordUndo(undo?.Commit());
                return;
            }
            case "resetrot":
            {
                var selected = Selected.Target;
                if (selected == null || selected.IsRootSlot) return;
                var undo = SlotTransformUndoBatch.Begin(selected, UndoLocale.ResetRotation);
                selected.LocalRotation.Value = floatQ.Identity;
                RecordUndo(undo?.Commit());
                return;
            }
            case "resetscale":
            {
                var selected = Selected.Target;
                if (selected == null || selected.IsRootSlot) return;
                var undo = SlotTransformUndoBatch.Begin(selected, UndoLocale.ResetScale);
                selected.LocalScale.Value = float3.One;
                RecordUndo(undo?.Commit());
                return;
            }
            case "resetall":
            {
                var selected = Selected.Target;
                if (selected == null || selected.IsRootSlot) return;
                var undo = SlotTransformUndoBatch.Begin(selected, UndoLocale.ResetTransform);
                selected.LocalPosition.Value = float3.Zero;
                selected.LocalRotation.Value = floatQ.Identity;
                selected.LocalScale.Value = float3.One;
                RecordUndo(undo?.Commit());
                return;
            }
            case "jumpto":
            {
                JumpToSelected();
                return;
            }
            case "bringto":
            {
                var selected = Selected.Target;
                var head = World?.LocalUser?.Root?.HeadSlot;
                if (selected == null || selected.IsRootSlot || head == null) return;
                var undo = SlotTransformUndoBatch.Begin(selected, UndoLocale.BringTo);
                var forward = head.GlobalRotation * float3.Backward; // view forward is -Z
                selected.GlobalPosition = head.GlobalPosition + forward * 0.6f;
                RecordUndo(undo?.Commit());
                return;
            }
            case "parentuser":
            {
                ReparentSelected(World?.LocalUser?.Root?.Slot, "Parent Under Local User Space");
                break;
            }
            case "parentroot":
            {
                ReparentSelected(World?.RootSlot, "Parent Under World Root");
                break;
            }
            case "attach":
            {
                var selected = Selected.Target;
                if (selected == null) return;
                var existing = _selector.Target;
                if (existing != null && !existing.IsDestroyed)
                    existing.TargetSlot.Target = selected;
                else
                    _selector.Target = ComponentSelectorPanel.Spawn(this, selected, _selectorCategory);
                return;
            }
        }
        // No hierarchy flag: every structural edit above (add child, duplicate, destroy, reparent,
        // insert parent, pivot) reaches the tree as a child add/remove on the affected row. A new
        // ROOT is the exception and rides Root's own changed flag.
        _componentsDirty = true;
        RunApplyChangesSafe();
    }

    // Keep the object where it stands in the world while changing who owns it.
    private void ReparentSelected(Slot? newParent, LocaleText description)
    {
        var selected = Selected.Target;
        if (selected == null || newParent == null || selected.IsRootSlot)
            return;
        if (newParent == selected || newParent.IsDescendantOf(selected))
            return;
        var undo = SlotTransformUndoBatch.Begin(selected, description);
        selected.SetParent(newParent, preserveGlobalTransform: true);
        RecordUndo(undo?.Commit());
    }

    // Wrap the selected slot in a fresh parent that sits at its exact pose; the slot's local
    // transform collapses to identity underneath it.
    private void InsertParentAboveSelected()
    {
        var target = Selected.Target;
        if (target == null || target.IsRootSlot || target.Parent == null)
            return;

        var wrapper = target.Parent.AddSlot(target.SlotName.Value + " - Parent");
        wrapper.LocalPosition.Value = target.LocalPosition.Value;
        wrapper.LocalRotation.Value = target.LocalRotation.Value;
        wrapper.LocalScale.Value = target.LocalScale.Value;

        using (BeginUndoBatch(UndoLocale.InsertParent))
        {
            RecordUndo(SlotExistenceUndoBatch.Created(World, new[] { wrapper }, UndoLocale.InsertParent));
            var move = SlotTransformUndoBatch.Begin(target, UndoLocale.InsertParent);
            target.SetParent(wrapper);
            target.LocalPosition.Value = float3.Zero;
            target.LocalRotation.Value = floatQ.Identity;
            target.LocalScale.Value = float3.One;
            RecordUndo(move?.Commit());
        }

        if (Root.Target == target)
            Root.Target = wrapper;
        Selected.Target = wrapper;
    }

    // New parent at the center of the subtree's visual bounds, keeping the object where it is,
    // so grab/scale/rotate operate around the middle instead of an arbitrary authored origin.
    private void CreatePivotAboveSelected()
    {
        var target = Selected.Target;
        if (target == null || target.IsRootSlot || target.Parent == null)
            return;
        if (!SlotBoundsHelper.TryComputeWorldBounds(target, out var bounds))
            return;
        var center = bounds.Center;
        if (float3.DistanceSquared(center, target.GlobalPosition) < 1e-10f)
            return;

        var wrapper = target.Parent.AddSlot(target.SlotName.Value + " - Pivot");
        wrapper.GlobalPosition = center;
        wrapper.GlobalRotation = target.GlobalRotation;

        using (BeginUndoBatch(UndoLocale.CreatePivot))
        {
            RecordUndo(SlotExistenceUndoBatch.Created(World, new[] { wrapper }, UndoLocale.CreatePivot));
            var move = SlotTransformUndoBatch.Begin(target, UndoLocale.CreatePivot);
            target.SetParent(wrapper, preserveGlobalTransform: true);
            RecordUndo(move?.Commit());
        }

        if (Root.Target == target)
            Root.Target = wrapper;
        Selected.Target = wrapper;
    }

    // Teleport the local user to stand a step back from the slot, approaching from where they
    // already are. Goes through the character controller so physics state stays coherent.
    private void JumpToSelected()
    {
        var selected = Selected.Target;
        var userRoot = World?.LocalUser?.Root;
        if (selected == null || userRoot == null)
            return;
        var character = userRoot.GetRegisteredComponent<CharacterController>();
        if (character == null)
            return;

        var away = userRoot.Slot.GlobalPosition - selected.GlobalPosition;
        away.y = 0f;
        away = away.LengthSquared > 1e-6f ? away.Normalized : float3.Backward;
        character.Teleport(selected.GlobalPosition + away * 1.5f);
    }

    private UndoManager? FindUndoManager()
        => World?.LocalUser?.Root?.Slot?.GetComponentInChildren<UndoManager>();

    // Everything recorded inside the returned scope collapses into one history entry. Without a
    // manager the scope is inert and the records fall on the floor exactly as they already did.
    private UndoBatchScope BeginUndoBatch(LocaleText description)
    {
        var manager = FindUndoManager();
        return manager != null ? manager.BeginBatch(description) : default;
    }

    private void RecordUndo(IUndoBatch? batch)
    {
        if (batch != null)
            FindUndoManager()?.Record(batch);
    }

    // selector calls this after attaching so the component list refreshes
    public void MarkListsDirty()
    {
        // Attaching a component can't change the slot tree; any slots it spawns arrive as child
        // events on the row that owns them.
        _componentsDirty = true;
        RunApplyChangesSafe();
    }

    // The category a selector closed on after its attach, so the next one opens there rather than at the
    // top of the tree. A plain field: the press that closes a selector and the one that opens the next both
    // run on the presser's machine.
    private string _selectorCategory = "";

    internal void RememberSelectorCategory(string? path) => _selectorCategory = path ?? "";

    private void RunApplyChangesSafe()
    {
        // The rebuild itself runs in OnChanges on the authority; a synced member changed in every
        // action path above, which marks the component dirty on all peers including the authority.
        MarkChangeDirty();
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Interaction;

// Builds scripts in the world.
//
// The node browser has every node by folder and by search. Picking one readies it on the tool, and a
// double press on empty space puts it at the tool's tip (VR) or half a metre out along the pointer
// (desktop), in a group of its own that nothing shows of but its nodes. The browser stays open for the
// next one.
//
// On a card: press a port and drag onto another to wire them, or off into space to let go of an input's
// wire; press the header and drag to move the node. Wiring nodes of two groups puts the groups together,
// whichever tool placed them. Secondary while dragging a wire makes the node that wire wants where it
// ends, wired in: a Constant, a Display, a Call or a Pulse. Grab a node's body to carry it; let go, it stays
// where it was let go, in its graph. Secondary on empty space opens the browser on desktop, and in VR opens the
// menu, which has the browser on its ring. A hand holding something leaves secondary to the menu.
//
// Secondary held on a node for half a second selects every node wired to it in its graph, or deselects them
// all when that node was selected; let go sooner, it readies the node's type. A selection adds up across sets
// and graphs, and every machine sees it. A hand grabbing a selected node takes the whole selection along in
// one piece. The menu clears it, deletes it, or packs the scripts it is in where they stand; with a slot card
// in the hand, every group of it standing on its own packs into that slot.
//
// In VR a dragged wire's end is pulled onto the nearest port it fits within a few centimetres, through
// ScriptPortSnap, in its own graph or any other it could join. Two ports whose types differ offer the
// conversion nodes that fit between them. Pressing on a graph away from any card and swiping across wires
// cuts every wire the swipe crossed, in every graph it went over, when it lets go.
//
// The beam finds a script's wires along their curves, and the wire it is on is drawn brighter. A press there
// puts a relay on the wire at that point, the wire running through it, and the press carries on as a press on
// the relay's middle: it moves with the pointer until let go, and the relay and where it went are one undo
// step. Wires drag from the relay like any port, so one value can branch off to more inputs.
//
// A field card in the menu's hand, gripped off an inspector row, offers a node for it put down by the tool,
// already pointed at it: a Drive that holds it, a Write that sets it on a pulse, a Read, or the field itself as
// a Reference to wire anywhere. The row's "..." menu offers the same while the tool is equipped.
//
// Writers show where they write: a line from the port naming the target to it, in the target's own colour,
// for the node the pointer is on and the nodes selected, or for every writer of every open script with "Show
// writes" on.
//
// Shader graphs are built the same way from shader nodes. The browser switches between the two lists; a
// material orb's graph opens from its menu; a shader node placed on empty space starts a loose group that
// joins a material's graph when it is wired into it.
//
// A slot card in the menu's hand, gripped off an inspector row, stands for that slot. The menu unpacks
// every script under it at once, or packs them all back; the nodes standing on their own worked on last
// pack into it as its script; a slot with no graph under it takes a new script.
//
// What the tool changes is the shared graph, gated like any other edit. When an edit is refused or two
// ports do not join, the reason floats by the laser for a moment. -xlinka
[ComponentCategory("Interaction/Tools")]
public sealed class ScriptNodeTool : EquippableTool
{
    private const float NoticeSeconds = 2.5f;
    private const double DoublePressSeconds = 0.5;
    private const float TipReach = 0.075f;
    private const float GraphReach = 2.5f;

    // How far out along the pointer a node is put on desktop, in metres at the user's own size.
    private const float PlaceDistance = 0.5f;

    // How close a wire end has to come to a port it fits before it is pulled on, in metres.
    public const float SnapReach = 0.035f;

    // How far a press on the graph has to move before it is a swipe that cuts, in metres.
    public const float CutStartDistance = 0.02f;

    // How far outside its cards a graph still takes a swipe, in metres.
    private const float GraphMargin = 0.15f;

    // How long secondary is held on a node before it selects or deselects the node's set, in seconds.
    public const double SelectHoldSeconds = 0.5;

    // What this tool has selected, a wired set at a time. Synced, so every machine tints the cards.
    public readonly SyncRefList<ScriptGraphNode> Selected = new();

    private ScriptGraphNode? _holding;
    private HashSet<ScriptGraphNode>? _holdSet;
    private double _holdStart;
    private bool _holdSelects;
    private bool _holdDone;

    private ScriptCardHandle? _wireFrom;
    private ScriptCardHandle? _pointFrom;
    private ScriptGraphNode? _moving;
    private float3 _moveStart;
    private float3 _moveNormal;
    private float3 _grabOffset;
    private ScriptGraphSpace? _lastSpace;
    private string? _readyType;
    private string? _readySignature;
    private GraphKind _readyKind;
    private double _lastEmptyPress = double.NegativeInfinity;
    private ScriptNodeBrowser? _browser;
    private Slot? _previewSlot;
    private ScriptWireMesh? _preview;
    private Slot? _noticeSlot;
    private TextRenderer? _notice;
    private double _noticeUntil;
    private Slot? _readySlot;
    private TextRenderer? _readyLabel;

    private ScriptGraphSpace? _cutSpace;
    private float3 _cutFrom;
    private float3 _cutTo;
    private bool _cutArmed;
    private bool _cutting;
    private readonly Dictionary<ScriptGraphSpace, float3> _cutStarts = new();
    private readonly List<ScriptWireKey> _cutWires = new();
    private readonly List<ScriptWireKey> _crossing = new();
    private readonly List<ScriptGraphSpace> _spaces = new();
    private readonly HashSet<ScriptGraphSpace> _marked = new();
    private Slot? _cutSlot;
    private ScriptWireMesh? _cutLine;

    private ScriptPortSnap? _snap;
    private Slot? _snapMarkSlot;
    private ScriptPulseMesh? _snapMark;

    // A relay the press still held made: its step is written when the press lets go, where the relay landed.
    private ScriptGraphNode? _newRelay;
    private List<IUndoBatch>? _newRelayWires;

    private bool _showWrites;
    private ScriptWriteMarks? _writeMarks;
    private readonly List<ScriptGraphNode> _writeFocus = new();

    // How near the graph worked on last a node made from a field has to be put down to go into it, in metres.
    private const float FieldNodeReach = 0.75f;

    public static readonly Localization.LocaleText ShowWritesLabel = Localization.LocaleText.Keyed("Loom.Tool.ShowWrites", "Show writes");
    public static readonly Localization.LocaleText DrivenAlready =
        Localization.LocaleText.Keyed("Loom.Tool.DrivenAlready", "another drive holds that field: this one takes it when that one lets go");
    public static readonly Localization.LocaleText WriteDriven =
        Localization.LocaleText.Keyed("Loom.Tool.WriteDriven", "a drive holds that field: writes to it are refused until it lets go");

    // Every node tool of a world, so a wire can ask whether a node tool drives a beam.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World, List<ScriptNodeTool>> InWorld = new();

    protected override colorHDR VisualTint => new(0.86f, 0.52f, 0.25f, 1f);

    public string? ReadyType => _readyType;

    // Whether the readied node is a script node or a shader node: the two lists share names.
    public GraphKind ReadyKind => _readyKind;

    // The graph worked on last, while it is out: one joined into another is parked.
    public ScriptGraphSpace? CurrentSpace => _lastSpace is { IsDestroyed: false, IsShown: true } space ? space : null;

    // The port a dragged wire's end is pulled onto right now, if any.
    public (ScriptGraphNode Node, ScriptCardHandleKind Kind, string Port)? SnappedPort => _snap?.Snapped;

    // While a swipe is cutting: the wires it crosses so far.
    public IReadOnlyList<ScriptWireKey> CutWires => _cutWires;

    public bool IsCutting => _cutting;

    public override void OnInit()
    {
        base.OnInit();
        EquipName.Value = "Script Node Tool";
    }

    public override void OnAwake()
    {
        base.OnAwake();
        if (World != null)
        {
            ScriptNodeSelection.For(World).Watch(Selected);
            var tools = InWorld.GetValue(World, static _ => new List<ScriptNodeTool>());
            lock (tools)
                tools.Add(this);
        }
    }

    // Whether a beam is an equipped node tool's: a script's wires answer that beam and no other.
    public static bool Drives(InteractionLaser? laser)
    {
        if (laser?.World is not { } world || !InWorld.TryGetValue(world, out var tools))
            return false;
        lock (tools)
        {
            foreach (var tool in tools)
            {
                if (!tool.IsDestroyed && ReferenceEquals(tool.ActiveTool?.Laser, laser))
                    return true;
            }
        }
        return false;
    }

    // A shared browser always arms the tool held by the person using it.
    public static ScriptNodeTool? ForLocalUser(World world)
    {
        if (!InWorld.TryGetValue(world, out var tools)) return null;
        lock (tools)
            foreach (var tool in tools)
                if (!tool.IsDestroyed && tool.IsEquipped && tool.Slot.ActiveUserRoot?.ActiveUser == world.LocalUser)
                    return tool;
        return null;
    }

    protected override void BuildVisualExtras(Slot visual)
    {
        EnsureBead(visual, "Bead", float3.Up * 0.05f, 0.011f, VisualTint);
    }

    public override bool OnPrimaryPress()
    {
        if (EditingBlocked)
            return false;

        var handle = HandleUnderLaser();
        if (handle?.Node is { IsDestroyed: false } node)
        {
            _lastSpace = node.Space ?? _lastSpace;
            if (handle.Kind == ScriptCardHandleKind.Material)
            {
                _pointFrom = handle;
                return true;
            }
            if (handle.Kind == ScriptCardHandleKind.Header)
            {
                _moving = node;
                BeginMove(node);
                return true;
            }
            _wireFrom = handle;
            BeginSnap(handle);
            return true;
        }

        if (WireUnderLaser() is { } wire)
        {
            AddRelay(wire.Space, wire.Hit);
            return true;
        }

        // A node's body is for the hand to grab, and a panel's presses are the panel's; neither is empty
        // space.
        if (NodeUnderLaser() != null || ActiveTool?.Laser?.CurrentPointerTarget != null)
        {
            _lastEmptyPress = double.NegativeInfinity;
            return false;
        }

        // On a graph away from its cards: a swipe from here cuts, a press let go where it began is a tap.
        if (SpaceUnderRay(out var cutSpace, out var cutFrom) && ActiveTool?.Laser is { } laser)
        {
            _cutSpace = cutSpace;
            _cutFrom = _cutTo = cutFrom;
            BeginCut(cutSpace, cutFrom, laser);
            _cutArmed = true;
            _cutting = false;
            return true;
        }

        return _readyType != null && TapEmpty();
    }

    // The wire the beam is on and where, as the wire took the beam's last look.
    private (ScriptGraphSpace Space, ScriptWireHit Hit)? WireUnderLaser()
    {
        var laser = ActiveTool?.Laser;
        return laser?.CurrentPointerTarget is ScriptWireTarget { Space: { IsDestroyed: false } space } wires
            && wires.HoveredBy(laser.PointerId) is { } hit
            ? (space, hit)
            : null;
    }

    // The press that makes a relay goes on as a press on its middle, so the relay rides the pointer until it lets go.
    private void AddRelay(ScriptGraphSpace space, ScriptWireHit hit)
    {
        SettleRelay();
        ScriptGraphNode? relay = null;
        List<IUndoBatch>? wires = null;
        string? error = null;
        if (!Guarded(() => relay = ScriptGraphEditing.MakeRelay(hit.Key, hit.Point, out error, out wires)))
        {
            Notice("you may not change that graph");
            return;
        }
        if (relay == null || wires == null)
        {
            Notice(error ?? "no relay was made");
            return;
        }
        _lastSpace = space;
        _newRelay = relay;
        _newRelayWires = wires;
        _moving = relay;
        BeginMove(relay);
    }

    // The relay made by the last press, into the history where it stands.
    private void SettleRelay()
    {
        var relay = _newRelay;
        var wires = _newRelayWires;
        _newRelay = null;
        _newRelayWires = null;
        if (ReferenceEquals(_moving, relay))
            _moving = null;
        if (relay != null && wires != null)
            ScriptGraphEditing.RecordRelay(relay, wires);
    }

    // The double press on empty space that puts the readied node there.
    private bool TapEmpty()
    {
        double now = World?.Time.TotalTime ?? 0.0;
        if (now - _lastEmptyPress <= DoublePressSeconds)
        {
            _lastEmptyPress = double.NegativeInfinity;
            PlaceReady(PlacementPoint());
            return true;
        }
        _lastEmptyPress = now;
        return false;
    }

    public override bool OnPrimaryHold()
    {
        if (_pointFrom != null)
        {
            ShowPoint();
            return true;
        }
        if (_moving is { IsDestroyed: false } moving)
        {
            if (RayOnPlane(moving.Space, _moveStart, _moveNormal, out var local))
            {
                var target = local - _grabOffset;
                if (moving.Slot.LocalPosition.Value != target)
                    Guarded(() => moving.Slot.LocalPosition.Value = target);
            }
            return true;
        }
        if (_wireFrom != null)
        {
            ShowPreview();
            return true;
        }
        if (_cutArmed)
        {
            UpdateCut();
            return true;
        }
        return false;
    }

    public override bool OnPrimaryRelease()
    {
        if (_pointFrom != null)
        {
            var pointed = _pointFrom;
            _pointFrom = null;
            HidePreview();
            PaintPointed(pointed);
            return true;
        }
        if (_moving != null)
        {
            var moving = _moving;
            _moving = null;
            if (ReferenceEquals(moving, _newRelay))
            {
                SettleRelay();
                return true;
            }
            if (!moving.IsDestroyed && moving.Slot.LocalPosition.Value != _moveStart)
                RecordUndo(new FieldEditUndoBatch(moving.Slot.LocalPosition, _moveStart, moving.Slot.LocalPosition.Value, ScriptGraphEditing.ValueStep));
            return true;
        }

        if (_cutArmed)
            return FinishCut();

        var from = _wireFrom;
        _wireFrom = null;
        var snapped = _snap?.SnappedHandle();
        EndSnap();
        HidePreview();
        if (from == null)
            return false;

        var to = snapped ?? HandleUnderLaser();
        // A Write Value's or a call's pointer takes the node it is dropped on by its header as well as by a port.
        if (to != null && (to.IsPort || from.Kind == ScriptCardHandleKind.Variable) && !ReferenceEquals(to, from))
            Join(from, to);
        else if (to == null)
            ScriptGraphEditing.Unwire(from);
        return true;
    }

    // Two ports joined as they are, or with the input's node changed to the overload that takes the output.
    // Only when neither works are the conversions that fit between them offered on the menu.
    private void Join(ScriptCardHandle from, ScriptCardHandle to)
    {
        var conversions = ScriptGraphEditing.ConversionsFor(from, to);
        if (conversions.Count > 0 && OfferConversions(from, to, conversions))
            return;
        var node = from.Node;
        if (!ScriptGraphEditing.Connect(from, to, out var error))
            Notice(error ?? "those do not join");
        else if (node?.Space is { } joined)
            _lastSpace = joined;
    }

    private bool OfferConversions(ScriptCardHandle from, ScriptCardHandle to, List<ScriptConversion> conversions)
    {
        var world = World;
        var menu = world?.LocalUser?.Root?.Slot?.GetComponentInChildren<ContextMenuSystem>();
        if (menu == null || menu.IsShown)
            return false;

        // The ends as they are now: the cards may be rebuilt before anything is picked.
        var output = from.Kind == ScriptCardHandleKind.DataOutput ? from : to;
        var input = ReferenceEquals(output, from) ? to : from;
        var producer = output.Node!;
        var consumer = input.Node!;
        string outputPort = output.Port;
        string inputPort = input.Port;

        var page = new ContextMenuPage("Convert to join");
        foreach (var conversion in conversions)
        {
            var picked = conversion;
            page.AddItem(new ContextMenuItem
            {
                Label = picked.Title,
                FillColor = ArmFill,
                OnPressed = _ =>
                {
                    menu.Close();
                    ScriptGraphNode? made = null;
                    string? error = null;
                    if (!Guarded(() => made = ScriptGraphEditing.ConnectThrough(producer, outputPort, consumer, inputPort, picked, out error))
                        || made == null || error != null)
                        Notice(error ?? "the conversion was not made");
                    else if (made.Space is { } joined)
                        _lastSpace = joined;
                },
            });
        }
        page.AddItem(new ContextMenuItem
        {
            Label = "Cancel",
            FillColor = ItemFill,
            OnPressed = _ => menu.Close(),
        });
        var laser = ActiveTool?.Laser;
        menu.OpenPage(page, new ContextMenuContext { Pointer = laser?.Slot ?? ActiveTool?.Slot, Side = ActiveTool?.Side.Value });
        return true;
    }

    public override bool OnSecondaryPress()
    {
        // A hand holding something leaves secondary to the menu, which has what can be done with what it holds.
        if (ActiveTool is HandTool { IsHoldingObjects: true })
            return false;

        // Secondary with a wire in hand makes the node that wire wants where the wire ends, wired in.
        if (_wireFrom is { Node: { IsDestroyed: false } dragged } from)
        {
            _wireFrom = null;
            EndSnap();
            HidePreview();
            if (dragged.Space is { } space)
            {
                var local = space.Slot.GlobalPointToLocal(PlacementPoint());
                ScriptGraphNode? made = null;
                string? error = null;
                if (!Guarded(() => made = ScriptGraphEditing.AddHelper(from, local, out error)) || made == null)
                    Notice(error ?? "nothing was made");
            }
            return true;
        }

        // On a node the press is a hold: long enough, it selects or deselects the node's set; let go sooner, it
        // readies the node's type.
        var node = HandleUnderLaser()?.Node ?? NodeUnderLaser();
        if (node is { IsDestroyed: false })
        {
            BeginHold(node);
            return true;
        }
        if (Engine.Current?.InputInterface?.IsVRActive == true)
            return false;
        ToggleBrowser();
        return true;
    }

    public override bool OnSecondaryHold()
    {
        if (_holding == null)
            return false;
        // Something taken into the hand meanwhile ends the hold; nothing comes of it.
        if (_holding.IsDestroyed || ActiveTool is HandTool { IsHoldingObjects: true })
        {
            EndHold();
            return true;
        }
        if (_holdDone)
            return true;
        double held = (World?.Time.TotalTime ?? 0.0) - _holdStart;
        if (held >= SelectHoldSeconds)
        {
            _holdDone = true;
            ToggleSet(_holding);
            ShowHold(null, 0f);
        }
        else
        {
            ShowHold(_holdSet, (float)(held / SelectHoldSeconds));
        }
        return true;
    }

    public override bool OnSecondaryRelease()
    {
        var node = _holding;
        if (node == null)
            return false;
        bool done = _holdDone;
        // A hold the frames never caught up with still counts by the clock.
        if (!done && !node.IsDestroyed && (World?.Time.TotalTime ?? 0.0) - _holdStart >= SelectHoldSeconds)
        {
            ToggleSet(node);
            done = true;
        }
        EndHold();
        if (!done && !node.IsDestroyed)
            Ready(node.NodeType.Value ?? string.Empty, node.Signature.Value, node.Space?.Kind.Value ?? GraphKind.Script);
        return true;
    }

    public override void OnDequipped()
    {
        SettleRelay();
        _wireFrom = null;
        _pointFrom = null;
        _moving = null;
        _writeMarks?.Hide();
        EndSnap();
        HidePreview();
        CancelCut();
        ShowReady(false);
        EndHold();
        ClearSelection();
        base.OnDequipped();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        double now = World?.Time.TotalTime ?? 0.0;
        if (_notice != null && now > _noticeUntil && _noticeSlot is { IsDestroyed: false } slot && slot.ActiveSelf.Value)
            slot.ActiveSelf.Value = false;
        if (_readyType != null && IsEquipped)
            ShowReady(true);
        UpdateWriteMarks();
    }

    // The writers whose targets show on this machine: every one with Show writes on, else the node the pointer
    // is on and the nodes selected. Only for whoever holds the tool.
    private void UpdateWriteMarks()
    {
        var world = World;
        bool mine = IsEquipped && world != null && Slot.ActiveUserRoot?.ActiveUser == world.LocalUser;
        _writeFocus.Clear();
        if (mine && !_showWrites)
        {
            if ((HandleUnderLaser()?.Node ?? NodeUnderLaser()) is { IsDestroyed: false } under && ScriptWriteTargets.Writes(under))
                _writeFocus.Add(under);
            foreach (var node in Selected)
            {
                if (node is { IsDestroyed: false } && ScriptWriteTargets.Writes(node) && !_writeFocus.Contains(node))
                    _writeFocus.Add(node);
            }
        }
        if (!mine || (!_showWrites && _writeFocus.Count == 0))
        {
            _writeMarks?.Hide();
            return;
        }
        if (_writeMarks == null || _writeMarks.IsDestroyed)
            _writeMarks = ScriptWriteMarks.Make(world!);
        _writeMarks.Show(_showWrites, _writeFocus);
    }

    public bool ShowWrites
    {
        get => _showWrites;
        set => _showWrites = value;
    }

    public ScriptWriteMarks? WriteMarks => _writeMarks;

    public override void OnDestroy()
    {
        SettleRelay();
        if (_writeMarks is { IsDestroyed: false } marks)
            marks.Slot.Destroy();
        EndSnap();
        EndHold();
        if (World != null && InWorld.TryGetValue(World, out var tools))
        {
            lock (tools)
                tools.Remove(this);
        }
        // The list goes with the tool, and its nodes stop wearing the tint everywhere.
        ScriptNodeSelection.Of(World)?.Forget(Selected);
        foreach (var slot in new[] { _previewSlot, _noticeSlot, _readySlot, _cutSlot, _snapMarkSlot })
        {
            if (slot is { IsDestroyed: false })
                slot.Destroy();
        }
        // Somebody else may be holding the browser by now; then it stays theirs to put away.
        if (_browser is { IsDestroyed: false } browser)
            Guarded(() => browser.Slot.Destroy());
        base.OnDestroy();
    }

    public override void PopulateToolActions(ContextMenuPage page, ContextMenuContext context)
    {
        PruneSelection();
        // A field card in the hand is what the menu was opened for: its nodes come first.
        if (HeldMember(context) is { } member)
            PopulateFieldActions(page, member);
        page.AddItem(new ContextMenuItem
        {
            Label = _browser is { IsDestroyed: false } ? "Close node browser" : "Node browser",
            FillColor = ActiveFill,
            OnPressed = _ => ToggleBrowser(),
        });
        page.AddItem(new ContextMenuItem
        {
            Label = World != null && LoomDiagnostic.Find(World) != null ? "Close diagnostic" : "Diagnostic",
            FillColor = ItemFill,
            OnPressed = _ =>
            {
                if (World != null)
                    LoomDiagnostic.Toggle(World);
            },
        });
        // Stays up, so the lines can be looked at with it on and off.
        page.AddItem(new ContextMenuItem
        {
            Label = ShowWritesLabel.Resolve(),
            FillColor = _showWrites ? ActiveFill : ItemFill,
            IsToggle = true,
            IsToggled = _showWrites,
            KeepsOpen = true,
            OnPressed = _ => _showWrites = !_showWrites,
            Refresh = item =>
            {
                if (item.IsToggled == _showWrites)
                    return false;
                item.IsToggled = _showWrites;
                item.FillColor = _showWrites ? ActiveFill : ItemFill;
                return true;
            },
        });
        if (_readyType != null)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "Stop placing " + _readyType,
                FillColor = ItemFill,
                OnPressed = _ => Ready(null, null),
            });
        }

        var under = HandleUnderLaser()?.Node ?? NodeUnderLaser();
        var space = under?.Space ?? CurrentSpace;
        if (Selected.Count > 0)
            PopulateSelectionActions(page);
        if (HeldSlot(context) is { } held)
            PopulateSlotCardActions(page, held, space);
        // Pointing at a selected node, the selection is what packs.
        bool onSelection = under != null && Selected.Contains(under);
        bool packOffered = !onSelection && space is { IsDestroyed: false, IsShown: true } && space.Kind.Value == GraphKind.Script;
        if (packOffered)
            AddPack(page, space!);
        // A material's graph closes back onto its orb; the material keeps what it made.
        if (space is { IsDestroyed: false } && space.ShaderSource.Target is { IsDestroyed: false } material)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "Close shader graph",
                FillColor = ItemFill,
                OnPressed = _ =>
                {
                    if (!ShaderGraphMaterials.SetOpen(material, false))
                        Notice("you may not close that graph");
                },
            });
        }
        if (under is { IsDestroyed: false } node)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "Delete node",
                FillColor = ClearFill,
                OnPressed = _ =>
                {
                    if (!ScriptGraphEditing.DeleteNode(node, out var error))
                        Notice(error ?? "not deleted");
                },
            });
        }
        else if (ResolveObjectRoot(ActiveTool?.Laser?.CurrentHitSlot) is { } target && !IsOffLimits(target))
        {
            if (ShaderGraphMaterials.FindOn(target) is { } shader && !shader.IsGraphOpen)
            {
                page.AddItem(new ContextMenuItem
                {
                    Label = "Edit shader graph",
                    FillColor = ArmFill,
                    OnPressed = _ =>
                    {
                        if (ShaderGraphMaterials.SetOpen(shader, true))
                            _lastSpace = shader.Graph.Target;
                        else
                            Notice("you may not open that graph");
                    },
                });
            }
            // An object's script packs when its graph is open, and unpacks when it is not.
            var host = target.GetComponent<ScriptHost>();
            if (host != null && ScriptGraphEditing.FindSpace(host) is { IsShown: true } open)
            {
                if (!packOffered || !ReferenceEquals(open, space))
                    AddPack(page, open);
            }
            else
            {
                page.AddItem(new ContextMenuItem
                {
                    Label = host != null ? "Unpack script" : "New script here",
                    FillColor = ArmFill,
                    OnPressed = _ =>
                    {
                        var opened = ScriptGraphEditing.EditScriptOn(target, out var error);
                        if (opened != null)
                            _lastSpace = opened;
                        else
                            Notice(error ?? "no script");
                    },
                });
            }
        }
    }

    // Named for the object it packs into, so a menu opened away from the graph says which one it means.
    private void AddPack(ContextMenuPage page, ScriptGraphSpace space)
    {
        var owner = space.Host.Target?.Slot;
        page.AddItem(new ContextMenuItem
        {
            Label = owner == null || ScriptGraphEditing.IsImplicit(space) ? "Pack script" : "Pack " + CardName(owner),
            FillColor = ItemFill,
            OnPressed = _ => Pack(space),
        });
    }

    // Readies a node type to place, or clears it with a null type.
    public void Ready(string? type, string? signature, GraphKind kind = GraphKind.Script)
    {
        _readyType = string.IsNullOrEmpty(type) ? null : type;
        _readySignature = string.IsNullOrEmpty(signature) ? null : signature;
        _readyKind = kind;
        _lastEmptyPress = double.NegativeInfinity;
        ShowReady(_readyType != null);
    }

    // The browser opens on the list for the graph worked on last, else for the node readied.
    public void ToggleBrowser()
    {
        if (_browser is { IsDestroyed: false } open)
        {
            // In somebody else's hand it is not this tool's to close; a new one opens instead.
            Guarded(() => open.Slot.Destroy());
            _browser = null;
            if (open.Slot.IsDestroyed)
                return;
        }
        if (World != null)
            _browser = ScriptNodeBrowser.Open(World, this, CurrentSpace?.Kind.Value ?? _readyKind);
    }

    // Puts the readied node at a point, in a group of its own there facing whoever placed it. Wiring it to a
    // node of any other group puts the two groups together.
    public ScriptGraphNode? PlaceReady(float3 point)
    {
        var world = World;
        if (world == null || _readyType == null)
            return null;
        if (EditingBlocked || SpawnBlocked)
        {
            Notice("building is not allowed here");
            return null;
        }

        var facing = world.LocalUser?.Root?.HeadPosition ?? point + float3.Backward;
        ScriptGraphNode? node = null;
        string? error = null;
        if (!Guarded(() => node = ScriptGraphEditing.PlaceNode(world, _readyType, _readySignature, point, facing, out error, _readyKind))
            || node == null)
        {
            Notice(error ?? "that node could not be added");
            return null;
        }
        _lastSpace = node.Space;
        return node;
    }

    // The tip in VR; on desktop half a metre out along the pointer, at the user's own size, whatever the
    // pointer is on. A node put where the beam lands ends up on the floor or in a wall metres away, and has
    // to be fished back out. -xlinka
    private float3 PlacementPoint()
    {
        if (Engine.Current?.InputInterface?.IsVRActive == true)
            return Tip + TipForward * TipReach;
        var laser = ActiveTool?.Laser;
        if (laser == null)
            return Tip;
        float scale = World?.LocalUser?.Root?.GlobalScale ?? 1f;
        return laser.RayOrigin + laser.RayDirection * (PlaceDistance * (scale > 0f ? scale : 1f));
    }

    private void Pack(ScriptGraphSpace space) => Report(ScriptGraphEditing.PackAsync(space));

    private void Report(System.Threading.Tasks.Task<string?> task)
    {
        task.ContinueWith(done =>
        {
            if (done.Result is string error)
                World?.RunSynchronously(() => Notice(error));
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    // The slot a card in the summoning hand stands for: a slot picked up from the inspector by its row.
    private Slot? HeldSlot(ContextMenuContext? context)
    {
        if (context?.Grabber is not { IsDestroyed: false } grabber)
            return null;
        foreach (var grabbed in grabber.GrabbedObjects)
        {
            var slot = (grabbed as Component)?.Slot;
            var card = slot?.GetComponent<ReferenceProxy>() ?? slot?.GetComponentInChildren<ReferenceProxy>();
            if (card is { IsDestroyed: false } && card.Target.Target is Slot { IsDestroyed: false } target && !IsOffLimits(target))
                return target;
        }
        return null;
    }

    // What a slot card offers: every script under the slot unpacked or packed back at once, the nodes
    // standing on their own worked on last packed into it, or a new script on it when it has none.
    private void PopulateSlotCardActions(ContextMenuPage page, Slot held, ScriptGraphSpace? space)
    {
        var graphs = ScriptGraphEditing.GraphsUnder(held);
        string name = CardName(held);
        if (graphs.AnyPacked)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "Unpack " + name,
                FillColor = ArmFill,
                OnPressed = _ =>
                {
                    var opened = ScriptGraphEditing.UnpackAll(held, out var error);
                    foreach (var graph in opened)
                    {
                        if (graph.Kind.Value == GraphKind.Script)
                        {
                            _lastSpace = graph;
                            break;
                        }
                    }
                    if (error != null)
                        Notice(error);
                },
            });
        }
        if (graphs.AnyOpen)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "Pack " + name,
                FillColor = ItemFill,
                OnPressed = _ => Report(ScriptGraphEditing.PackAllAsync(held)),
            });
        }
        // With nodes selected, every group of them standing on its own packs into the slot at once.
        var selected = SelectedLooseGroups(held);
        if (selected.Count > 0)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "Pack into " + name,
                FillColor = ArmFill,
                OnPressed = _ =>
                {
                    ClearSelection();
                    Report(ScriptGraphEditing.PackIntoAsync(selected, held));
                },
            });
        }
        else if (space is { IsDestroyed: false } loose && loose.Kind.Value == GraphKind.Script && ScriptGraphEditing.IsImplicit(loose)
            && loose.Slot?.Parent is { } group && !ReferenceEquals(held, group) && !held.IsDescendantOf(group))
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "Pack into " + name,
                FillColor = ArmFill,
                OnPressed = _ => Report(ScriptGraphEditing.PackIntoAsync(loose, held)),
            });
        }
        else if (!graphs.Any)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "New script on " + name,
                FillColor = ArmFill,
                OnPressed = _ =>
                {
                    var opened = ScriptGraphEditing.EditScriptOn(held, out var error);
                    if (opened != null)
                        _lastSpace = opened;
                    else
                        Notice(error ?? "no script");
                },
            });
        }
    }

    // FIELDS. The field a card in the summoning hand stands for: a value or reference field gripped off its
    // inspector row.
    private static IWorldElement? HeldMember(ContextMenuContext? context)
    {
        if (context?.Grabber is not { IsDestroyed: false } grabber)
            return null;
        foreach (var grabbed in grabber.GrabbedObjects)
        {
            var slot = (grabbed as Component)?.Slot;
            var card = slot?.GetComponent<ReferenceProxy>() ?? slot?.GetComponentInChildren<ReferenceProxy>();
            if (card is { IsDestroyed: false } && card.Target.Target is { IsDestroyed: false } target && ScriptFieldNodes.Offers(target).Count > 0)
                return target;
        }
        return null;
    }

    // Drive, Write, Read and Reference for a field, each making its node by the tool, pointed at the field.
    public void PopulateFieldActions(ContextMenuPage page, IWorldElement member, Action? picked = null)
    {
        foreach (var kind in ScriptFieldNodes.Offers(member))
        {
            var chosen = kind;
            page.AddItem(new ContextMenuItem
            {
                Label = ScriptFieldNodes.Label(chosen),
                FillColor = chosen == ScriptFieldNodeKind.Drive ? ArmFill : ActiveFill,
                OnPressed = _ =>
                {
                    picked?.Invoke();
                    SpawnFieldNode(member, chosen);
                },
            });
        }
    }

    // A node for a field put down where the tool puts nodes, into the graph worked on last when that is where it
    // lands, else into a group of its own that joins a graph once wired into it.
    public ScriptGraphNode? SpawnFieldNode(IWorldElement member, ScriptFieldNodeKind kind)
    {
        var world = World;
        if (world == null)
            return null;
        if (EditingBlocked || SpawnBlocked)
        {
            Notice("building is not allowed here");
            return null;
        }
        var point = PlacementPoint();
        var facing = world.LocalUser?.Root?.HeadPosition ?? point + float3.Backward;
        var into = SpaceNear(point);
        ScriptGraphNode? node = null;
        string? error = null;
        if (!Guarded(() => node = ScriptFieldNodes.Spawn(world, member, kind, point, facing, into, out error)) || node == null)
        {
            Notice(error ?? "that node could not be added");
            return null;
        }
        _lastSpace = node.Space;
        if (member is ILinkable { IsDriven: true })
        {
            if (kind == ScriptFieldNodeKind.Drive)
                Notice(DrivenAlready.Resolve());
            else if (kind == ScriptFieldNodeKind.Write)
                Notice(WriteDriven.Resolve());
        }
        return node;
    }

    // The script worked on last, when a point is close enough to its cards to be working on it.
    private ScriptGraphSpace? SpaceNear(float3 point)
    {
        if (CurrentSpace is not { Kind.Value: GraphKind.Script } space)
            return null;
        var local = space.Slot.GlobalPointToLocal(point);
        local.z = 0f;
        if ((space.Slot.LocalPointToGlobal(local) - point).Length > FieldNodeReach)
            return null;
        return NearCards(space, local, FieldNodeReach) ? space : null;
    }

    private static string CardName(Slot slot)
    {
        var name = slot.Name.Value?.Trim();
        if (string.IsNullOrEmpty(name))
            return "slot";
        return name.Length > 22 ? name[..21] + "..." : name;
    }

    // SELECTION. What the menu offers for it: letting go of it, deleting it, and packing the scripts it is in
    // where they stand. Each of those lets go of it too.
    private void PopulateSelectionActions(ContextMenuPage page)
    {
        page.AddItem(new ContextMenuItem
        {
            Label = "Clear selection",
            FillColor = ItemFill,
            OnPressed = _ => ClearSelection(),
        });
        page.AddItem(new ContextMenuItem
        {
            Label = "Delete selected",
            FillColor = ClearFill,
            OnPressed = _ =>
            {
                var nodes = SelectedNodes();
                ClearSelection();
                string? error = null;
                if (Guarded(() => ScriptGraphEditing.DeleteNodes(nodes, out error)) && error != null)
                    Notice(error);
            },
        });
        bool scripts = false;
        foreach (var node in SelectedNodes())
            scripts |= node.Space?.Kind.Value == GraphKind.Script;
        if (!scripts)
            return;
        page.AddItem(new ContextMenuItem
        {
            Label = "Pack selected",
            FillColor = ItemFill,
            OnPressed = _ =>
            {
                var nodes = SelectedNodes();
                ClearSelection();
                Report(ScriptGraphEditing.PackGraphsOf(nodes));
            },
        });
    }

    // The groups standing on their own that selected nodes are in, but for one the slot is part of.
    private List<ScriptGraphSpace> SelectedLooseGroups(Slot held)
    {
        var groups = new List<ScriptGraphSpace>();
        foreach (var node in SelectedNodes())
        {
            if (node.Space is { Kind.Value: GraphKind.Script } space && !groups.Contains(space) && ScriptGraphEditing.IsImplicit(space)
                && space.Slot?.Parent is { } group && !ReferenceEquals(held, group) && !held.IsDescendantOf(group))
                groups.Add(space);
        }
        return groups;
    }

    // The selection as it stands, once what is gone has been let go of.
    private List<ScriptGraphNode> SelectedNodes()
    {
        PruneSelection();
        var nodes = new List<ScriptGraphNode>();
        foreach (var node in Selected)
        {
            if (node != null)
                nodes.Add(node);
        }
        return nodes;
    }

    // Lets go of selected nodes that are gone: deleted, parked, or in a graph packed away or closed.
    private void PruneSelection()
    {
        for (int i = Selected.Count - 1; i >= 0; i--)
        {
            if (Selected[i] is { IsDestroyed: false } node && node.Space is { IsShown: true })
                continue;
            int at = i;
            Guarded(() => Selected.RemoveAt(at));
        }
    }

    public void ClearSelection()
    {
        if (!IsDestroyed && Selected.Count > 0)
            Guarded(() => Selected.Clear());
    }

    // A node's whole wired set into the selection, or out of it when that node was in already.
    private void ToggleSet(ScriptGraphNode node)
    {
        PruneSelection();
        var set = new HashSet<ScriptGraphNode>(ScriptGraphEditing.ConnectedSet(node));
        if (Selected.Contains(node))
        {
            Guarded(() =>
            {
                for (int i = Selected.Count - 1; i >= 0; i--)
                {
                    if (Selected[i] is { } each && set.Contains(each))
                        Selected.RemoveAt(i);
                }
            });
            return;
        }
        var had = new HashSet<ScriptGraphNode>(SelectedNodes());
        Guarded(() =>
        {
            foreach (var each in set)
            {
                if (had.Add(each))
                    Selected.Add(each);
            }
        });
    }

    private void BeginHold(ScriptGraphNode node)
    {
        EndHold();
        _holding = node;
        _holdStart = World?.Time.TotalTime ?? 0.0;
        _holdSelects = !Selected.Contains(node);
        _holdSet = new HashSet<ScriptGraphNode>(ScriptGraphEditing.ConnectedSet(node));
        _holdDone = false;
    }

    private void EndHold()
    {
        _holding = null;
        _holdSet = null;
        _holdDone = false;
        ShowHold(null, 0f);
    }

    // The cards of a held set ramping toward what the hold will make them, on this machine.
    private void ShowHold(HashSet<ScriptGraphNode>? set, float progress)
    {
        if (World != null)
            ScriptNodeSelection.Of(World)?.SetHold(this, set, _holdSelects, progress);
    }

    // A hand grabbing a selected node takes every other selected node too, each where it is, so the selection
    // moves as one piece.
    public override IEnumerable<IGrabbable>? GrabCompanions(Grabber grabber, IGrabbable grabbed)
    {
        if ((grabbed as Component)?.Slot?.GetComponent<ScriptGraphNode>() is not { } node || !Selected.Contains(node))
            return null;
        var companions = new List<IGrabbable>();
        foreach (var other in SelectedNodes())
        {
            if (!ReferenceEquals(other, node) && other.Slot.GetComponent<Grabbable>() is { IsDestroyed: false, IsGrabbed: false } grab
                && grab.Enabled.Value)
                companions.Add(grab);
        }
        return companions;
    }

    private ScriptCardHandle? HandleUnderLaser()
    {
        var laser = ActiveTool?.Laser;
        if (laser?.CurrentTarget is not Canvas canvas)
            return null;
        // The element the beam presses, not the topmost rect: a port's label sits over its hit area.
        return canvas.TryHitTest(laser.RayOrigin, laser.RayDirection, out var hit) && hit.Interactable is Component pressed
            ? ScriptCardHandle.Find(pressed.Slot)
            : null;
    }

    private ScriptGraphNode? NodeUnderLaser()
    {
        for (var slot = ActiveTool?.Laser?.CurrentHitSlot; slot != null; slot = slot.Parent)
        {
            if (slot.GetComponent<ScriptGraphNode>() is { IsDestroyed: false } node)
                return node;
        }
        return null;
    }

    // A header drag slides the node in the plane of its own card, through where it stands, so a node put in front
    // of its graph or turned keeps its depth and its turn.
    private void BeginMove(ScriptGraphNode node)
    {
        var slot = node.Slot;
        _moveStart = slot.LocalPosition.Value;
        _moveNormal = slot.LocalRotation.Value * float3.Forward;
        _grabOffset = RayOnPlane(node.Space, _moveStart, _moveNormal, out var local) ? local - _moveStart : float3.Zero;
    }

    // Where the laser crosses a node's card plane, in the graph's own space. A node held in a hand has no plane
    // in its graph, and the graph's own stands in.
    private bool RayOnCard(ScriptGraphSpace? space, ScriptGraphNode node, out float3 local)
    {
        var slot = node.Slot;
        if (space?.Slot != null && slot != null && ReferenceEquals(slot.Parent, space.Slot))
            return RayOnPlane(space, slot.LocalPosition.Value, slot.LocalRotation.Value * float3.Forward, out local);
        return RayOnGraph(space, out local);
    }

    // Where the laser crosses the plane through point facing normal, both in the graph's own space.
    private bool RayOnPlane(ScriptGraphSpace? space, float3 point, float3 normal, out float3 local)
    {
        local = default;
        var laser = ActiveTool?.Laser;
        if (laser == null || space?.Slot == null)
            return false;
        var origin = space.Slot.GlobalPointToLocal(laser.RayOrigin);
        var direction = space.Slot.GlobalDirectionToLocal(laser.RayDirection);
        float across = float3.Dot(direction, normal);
        if (MathF.Abs(across) < 1e-5f)
            return false;
        float t = float3.Dot(point - origin, normal) / across;
        if (t < 0f)
            return false;
        local = origin + direction * t;
        return true;
    }

    // Where the laser crosses a graph's plane, in the graph's own space.
    private bool RayOnGraph(ScriptGraphSpace? space, out float3 local)
    {
        local = default;
        var laser = ActiveTool?.Laser;
        return laser != null && OnPlane(space, laser.RayOrigin, laser.RayDirection, out local);
    }

    private static bool OnPlane(ScriptGraphSpace? space, float3 rayOrigin, float3 rayDirection, out float3 local)
    {
        local = default;
        if (space?.Slot == null)
            return false;
        var origin = space.Slot.GlobalPointToLocal(rayOrigin);
        var direction = space.Slot.GlobalDirectionToLocal(rayDirection);
        if (MathF.Abs(direction.z) < 1e-5f)
            return false;
        float t = -origin.z / direction.z;
        if (t < 0f)
            return false;
        local = origin + direction * t;
        local.z = 0f;
        return true;
    }

    private void ShowPreview()
    {
        var from = _wireFrom;
        var space = from?.Node?.Space;
        var view = space?.View;
        if (from?.Node == null || space == null || view == null || !view.TryPortPoint(from.Node, from.Kind, from.Port, out var portPoint))
            return;
        // Pulled onto a port in reach, else onto the port the pointer is on, which may be in another group
        // and off this graph's plane, else where the pointer meets the plane of the card it leaves.
        bool onGraph = RayOnCard(space, from.Node, out var end);
        if (onGraph && Snap(space, end) is { } pulled)
            end = pulled;
        else if (PortUnderLaser(from) is { } port)
            end = space.Slot.GlobalPointToLocal(port);
        else if (!onGraph)
            return;

        if (_previewSlot == null || _previewSlot.IsDestroyed || !ReferenceEquals(_previewSlot.Parent, space.Slot))
        {
            if (_previewSlot is { IsDestroyed: false })
                _previewSlot.Destroy();
            _previewSlot = space.Slot.AddLocalSlot("Wire preview");
            _preview = _previewSlot.AttachComponent<ScriptWireMesh>();
            ScriptGraphView.DrawPipe(_previewSlot, _preview);
        }
        _previewSlot.ActiveSelf.Value = true;

        var start = space.Slot.GlobalPointToLocal(portPoint);
        var tint = from.Kind is ScriptCardHandleKind.FlowInput or ScriptCardHandleKind.FlowOutput
            ? ScriptEditorStyle.Flow
            : from.Kind == ScriptCardHandleKind.Variable ? DashTheme.Warning : ScriptEditorStyle.Type(PortType(from));
        bool leavesRight = from.Kind is ScriptCardHandleKind.DataOutput or ScriptCardHandleKind.FlowOutput;
        _preview!.SetWires(new[] { leavesRight ? new ScriptWireCurve(start, end, tint, tint) : new ScriptWireCurve(end, start, tint, tint) });
    }

    // A drag from a Material Output's well: a line from the well to where the beam lands, and what it lands on
    // wears the graph's material when the press lets go, the surface under the beam as the material tool's ray
    // paints it.
    private void ShowPoint()
    {
        var from = _pointFrom;
        var space = from?.Node?.Space;
        var view = space?.View;
        if (from?.Node == null || space == null || view == null || !view.TryPortPoint(from.Node, from.Kind, from.Port, out var wellPoint))
            return;
        float3 end;
        if (TryGetAim(out var aim) && aim.HitSlot != null)
            end = aim.Point;
        else if (ActiveTool?.Laser is { } laser)
            end = laser.RayOrigin + laser.RayDirection * 1.5f;
        else
            return;
        if (_previewSlot == null || _previewSlot.IsDestroyed || !ReferenceEquals(_previewSlot.Parent, space.Slot))
        {
            if (_previewSlot is { IsDestroyed: false })
                _previewSlot.Destroy();
            _previewSlot = space.Slot.AddLocalSlot("Wire preview");
            _preview = _previewSlot.AttachComponent<ScriptWireMesh>();
            ScriptGraphView.DrawPipe(_previewSlot, _preview);
        }
        _previewSlot.ActiveSelf.Value = true;
        var tint = DashTheme.Accent;
        _preview!.SetWires(new[] { new ScriptWireCurve(space.Slot.GlobalPointToLocal(wellPoint), space.Slot.GlobalPointToLocal(end), tint, tint) });
    }

    private void PaintPointed(ScriptCardHandle from)
    {
        if (from.Node is not { IsDestroyed: false } node || !TryGetAim(out var aim) || aim.HitSlot is not { IsDestroyed: false } hit)
            return;
        // Let go back on a graph: no paint, nothing to say.
        if (hit.GetComponentInParents<ScriptGraphNode>() != null || hit.GetComponentInParents<ScriptGraphSpace>() != null)
            return;
        int painted = 0;
        Localization.LocaleText message = default;
        Guarded(() => painted = ShaderGraphOutput.ApplyAt(node, hit, aim.Point, out message));
        ShaderOutputWell.Say(node, message, painted == 0);
        Notice(message.Resolve());
    }

    private void HidePreview()
    {
        if (_previewSlot is { IsDestroyed: false })
            _previewSlot.ActiveSelf.Value = false;
    }

    // Where the port the pointer is on meets its wires, when it is a port of another node.
    private float3? PortUnderLaser(ScriptCardHandle from)
    {
        if (HandleUnderLaser() is not { IsPort: true, Node: { IsDestroyed: false } node } handle || ReferenceEquals(node, from.Node))
            return null;
        return node.Space?.View is { } view && view.TryPortPoint(node, handle.Kind, handle.Port, out var point) ? point : null;
    }

    // SNAPPING, in VR only: small ports are hard to land on with a hand at arm's length, and a desktop
    // pointer lands where it is put.
    private void BeginSnap(ScriptCardHandle from)
    {
        EndSnap();
        if (Engine.Current?.InputInterface?.IsVRActive == true)
            _snap = ScriptPortSnap.Begin(from, SnapReach);
    }

    // The point in the graph a loose end at end is pulled onto, or null when no port it fits is in reach. A
    // port of another group may sit off this graph's plane, and the end goes to it there.
    private float3? Snap(ScriptGraphSpace space, float3 end)
    {
        if (_snap?.Pull(space.Slot.LocalPointToGlobal(end)) is not { } port)
        {
            ShowSnapMark(space, null);
            return null;
        }
        var pulled = space.Slot.GlobalPointToLocal(port);
        ShowSnapMark(space, pulled);
        return pulled;
    }

    private void EndSnap()
    {
        _snap?.Dispose();
        _snap = null;
        if (_snapMarkSlot is { IsDestroyed: false } mark && mark.ActiveSelf.Value)
            mark.ActiveSelf.Value = false;
    }

    // A bright bead on the port the end is pulled onto.
    private void ShowSnapMark(ScriptGraphSpace space, float3? at)
    {
        if (at == null)
        {
            if (_snapMarkSlot is { IsDestroyed: false } hidden && hidden.ActiveSelf.Value)
                hidden.ActiveSelf.Value = false;
            return;
        }
        if (_snapMarkSlot == null || _snapMarkSlot.IsDestroyed || !ReferenceEquals(_snapMarkSlot.Parent, space.Slot))
        {
            if (_snapMarkSlot is { IsDestroyed: false } old)
                old.Destroy();
            _snapMarkSlot = space.Slot.AddLocalSlot("Snap mark");
            _snapMark = _snapMarkSlot.AttachComponent<ScriptPulseMesh>();
            ScriptGraphView.Draw(_snapMarkSlot, _snapMark);
        }
        _snapMarkSlot.ActiveSelf.Value = true;
        _snapMark!.SetBeads(new[] { (at.Value, ScriptWireMesh.Radius * 3.5f, ScriptEditorStyle.Snap) });
    }

    // CUTTING. A press on a graph away from its cards arms a cut; once the pointer has moved a little it is
    // a swipe, drawn as a red line, and every wire it crosses goes red with it, in that graph and in any
    // other the swipe goes over. Letting go cuts them all as one undo step; letting go before it moved is a
    // tap.
    private void UpdateCut()
    {
        var space = _cutSpace;
        if (space == null || space.IsDestroyed || !RayOnGraph(space, out var local))
            return;
        _cutTo = local;
        if (!_cutting)
        {
            float moved = (space.Slot.LocalPointToGlobal(local) - space.Slot.LocalPointToGlobal(_cutFrom)).Length;
            if (moved < CutStartDistance)
                return;
            _cutting = true;
            _lastEmptyPress = double.NegativeInfinity;
        }
        FindCut();
        ShowCutLine(space);
    }

    // Where the swipe began on every graph whose plane the press met within reach, in each graph's own space,
    // so each can judge the swipe on its own plane.
    private void BeginCut(ScriptGraphSpace space, float3 from, InteractionLaser laser)
    {
        _cutStarts.Clear();
        ScriptGraphSpace.GetAll(World, _spaces);
        foreach (var other in _spaces)
        {
            if (other.IsShown && RayOnGraph(other, out var start)
                && (other.Slot.LocalPointToGlobal(start) - laser.RayOrigin).Length <= GraphReach)
                _cutStarts[other] = start;
        }
        _cutStarts[space] = from;
    }

    // Every wire the swipe crosses so far, in every graph it began over: from where it began on that graph's
    // plane to where the pointer meets the plane now.
    private void FindCut()
    {
        _cutWires.Clear();
        var laser = ActiveTool?.Laser;
        if (laser == null)
            return;
        foreach (var (space, from) in _cutStarts)
        {
            if (space.IsDestroyed || space.View is not { } view)
                continue;
            _crossing.Clear();
            if (space.IsShown && RayOnGraph(space, out var to)
                && (space.Slot.LocalPointToGlobal(to) - laser.RayOrigin).Length <= GraphReach)
                view.WiresCrossing(from, to, _crossing);
            if (_crossing.Count > 0)
            {
                _cutWires.AddRange(_crossing);
                view.SetCutMarks(_crossing);
                _marked.Add(space);
            }
            else if (_marked.Remove(space))
            {
                view.SetCutMarks(null);
            }
        }
    }

    private bool FinishCut()
    {
        var space = _cutSpace;
        bool cutting = _cutting;
        if (cutting && space is { IsDestroyed: false })
            FindCut();
        var wires = new List<ScriptWireKey>(_cutWires);
        CancelCut();
        _cutWires.Clear();
        if (!cutting)
        {
            if (_readyType != null)
                TapEmpty();
            return true;
        }
        if (space != null && !space.IsDestroyed && wires.Count > 0)
        {
            string? error = null;
            Guarded(() => ScriptGraphEditing.CutWires(space, wires, out error));
            if (error != null)
                Notice(error);
        }
        return true;
    }

    private void CancelCut()
    {
        foreach (var marked in _marked)
        {
            if (!marked.IsDestroyed)
                marked.View?.SetCutMarks(null);
        }
        _marked.Clear();
        _cutStarts.Clear();
        _cutArmed = false;
        _cutting = false;
        _cutSpace = null;
        if (_cutSlot is { IsDestroyed: false } slot && slot.ActiveSelf.Value)
            slot.ActiveSelf.Value = false;
    }

    private void ShowCutLine(ScriptGraphSpace space)
    {
        if (_cutSlot == null || _cutSlot.IsDestroyed || !ReferenceEquals(_cutSlot.Parent, space.Slot))
        {
            if (_cutSlot is { IsDestroyed: false } old)
                old.Destroy();
            _cutSlot = space.Slot.AddLocalSlot("Cut line");
            _cutLine = _cutSlot.AttachComponent<ScriptWireMesh>();
            ScriptGraphView.Draw(_cutSlot, _cutLine);
        }
        _cutSlot.ActiveSelf.Value = true;
        _cutLine!.SetWires(new[] { new ScriptWireCurve(_cutFrom, _cutTo, ScriptEditorStyle.Cut, ScriptEditorStyle.Cut, Straight: true) });
    }

    // The graph the laser meets away from any card, near enough to its cards to be working on it, and
    // where, in that graph's own space: any graph in the world, the one worked on last asked first.
    private bool SpaceUnderRay(out ScriptGraphSpace space, out float3 local)
    {
        space = null!;
        local = default;
        var laser = ActiveTool?.Laser;
        var world = World;
        if (laser == null || world == null)
            return false;
        float best = GraphReach;
        var current = CurrentSpace;
        if (current != null)
            Consider(current, laser, ref space, ref local, ref best);
        ScriptGraphSpace.GetAll(world, _spaces);
        foreach (var candidate in _spaces)
        {
            if (!ReferenceEquals(candidate, current))
                Consider(candidate, laser, ref space, ref local, ref best);
        }
        return space != null;
    }

    private void Consider(ScriptGraphSpace candidate, InteractionLaser laser, ref ScriptGraphSpace space, ref float3 local, ref float best)
    {
        if (candidate.IsDestroyed || !candidate.IsShown || !RayOnGraph(candidate, out var point))
            return;
        float distance = (candidate.Slot.LocalPointToGlobal(point) - laser.RayOrigin).Length;
        if (distance > best || !NearCards(candidate, point))
            return;
        best = distance;
        space = candidate;
        local = point;
    }

    // Within the margin around the graph's cards, in the graph's own space.
    private static bool NearCards(ScriptGraphSpace space, float3 point, float reach = GraphMargin)
    {
        float margin = space.Slot.GlobalScaleToLocal(reach);
        foreach (var node in space.Nodes)
        {
            // One in a hand has its place in the hand.
            if (!ReferenceEquals(node.Slot.Parent, space.Slot))
                continue;
            var at = node.Slot.LocalPosition.Value;
            var layout = space.View?.CardOf(node)?.Layout ?? ScriptCardLayout.For(node, 0);
            float width = layout.Width * ScriptEditorStyle.CardScale;
            float height = layout.Height * ScriptEditorStyle.CardScale;
            if (point.x >= at.x - margin && point.x <= at.x + width + margin && point.y <= at.y + margin && point.y >= at.y - height - margin)
                return true;
        }
        return false;
    }

    private static ScriptType? PortType(ScriptCardHandle handle)
    {
        var overload = handle.Node?.Overload;
        if (overload == null)
            return null;
        if (handle.Kind == ScriptCardHandleKind.DataInput)
        {
            int index = overload.InputIndex(handle.Port);
            return index >= 0 ? overload.Inputs[index].Type : null;
        }
        int output = overload.OutputIndex(handle.Port);
        return output >= 0 ? overload.Outputs[output].Type : null;
    }

    // The readied node's name, floating just above the tip.
    private void ShowReady(bool show)
    {
        var world = World;
        if (world == null)
            return;
        if (!show)
        {
            if (_readySlot is { IsDestroyed: false } hidden && hidden.ActiveSelf.Value)
                hidden.ActiveSelf.Value = false;
            return;
        }
        if (_readySlot == null || _readySlot.IsDestroyed)
        {
            _readySlot = world.RootSlot.AddLocalSlot("Script tool ready node");
            _readyLabel = MakeLabel(world, _readySlot, DashTheme.Text);
        }
        if (!_readySlot.ActiveSelf.Value)
            _readySlot.ActiveSelf.Value = true;
        string name = GraphLanguage.For(_readyKind).Registry.TryGetDefinition(_readyType!, out var definition)
            ? definition.Path[(definition.Path.LastIndexOf('/') + 1)..]
            : _readyType!;
        string text = _readySignature == null ? name : $"{name} ({_readySignature})";
        if (_readyLabel!.Text.Value != text)
            _readyLabel.Text.Value = text;
        Face(_readySlot, Tip + new float3(0f, 0.035f, 0f));
    }

    private void Notice(string text)
    {
        LumoraLogger.Debug($"Script Node Tool: {text}");
        var world = World;
        var laser = ActiveTool?.Laser;
        if (world == null || laser == null)
            return;

        if (_noticeSlot == null || _noticeSlot.IsDestroyed)
        {
            _noticeSlot = world.RootSlot.AddLocalSlot("Script tool notice");
            _notice = MakeLabel(world, _noticeSlot, DashTheme.Warning);
        }
        var at = laser.HasAimPoint ? laser.AimPoint : laser.RayOrigin + laser.RayDirection * 0.5f;
        Face(_noticeSlot, at + new float3(0f, 0.04f, 0f));
        _noticeSlot.ActiveSelf.Value = true;
        _notice!.Text.Value = text;
        _noticeUntil = world.Time.TotalTime + NoticeSeconds;
    }

    private static TextRenderer MakeLabel(World world, Slot slot, color tint)
    {
        var label = slot.AttachComponent<TextRenderer>();
        var font = slot.AddSlot("Font").AttachComponent<FontProvider>();
        font.URL.Value = ImportDialog.ResolveFontUrl(world) ?? DashTheme.FontRegular;
        label.Font.Target = font;
        label.Size.Value = 0.018f;
        label.Color.Value = tint;
        label.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        label.OutlineThickness.Value = 2f;
        label.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 1f);
        return label;
    }

    private void Face(Slot slot, float3 at)
    {
        slot.GlobalPosition = at;
        var head = World?.LocalUser?.Root?.HeadPosition;
        if (head == null)
            return;
        var toward = head.Value - at;
        toward.y = 0f;
        if (toward.LengthSquared > 1e-6f)
            slot.GlobalRotation = floatQ.LookRotation(toward.Normalized, float3.Up).Inverse;
    }
}

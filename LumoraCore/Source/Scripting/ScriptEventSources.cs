// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Physics;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The world events a running script listens to beyond touch controls: a grabbable's holder changing, a
// field changing, an object deleted, colliders coming into and out of a trigger, and what a relay on an
// object hears on the machines where people act on it (a panel button, a beam). All of it hooked on the
// authority, and undone when the script stops or its graph changes.
//
// Triggers are looked at about twenty times a second through overlap questions, people included by
// their bodies. Collisions come from the physics hook through World.Physics.WatchContacts. Synced
// values are compared once a frame against what was last seen, on whichever machine the script runs. -xlinka
internal sealed class ScriptEventSources
{
    public const double TriggerSeconds = 0.05;

    private readonly ScriptHostRuntime _runtime;
    private readonly List<Action> _undo = new();
    private readonly Dictionary<Slot, List<ScriptEventEntry>> _relayed = new();
    private readonly List<ScriptSourceRelay> _relays = new();
    private readonly List<Trigger> _triggers = new();
    private readonly List<SyncedWatch> _synced = new();
    private readonly Dictionary<IField, ulong> _fieldFrame = new();
    private readonly List<Slot> _hits = new();
    private readonly List<User> _people = new();
    private double _nextTriggers;

    // What still touches each watched slot, and the frame each touch began, for On Collision Stay.
    private readonly List<ContactStays> _stays = new();
    private readonly List<Slot> _stayScratch = new();

    private sealed class ContactStays
    {
        public Slot Slot = null!;
        public List<ScriptEventEntry> Entries = null!;
        public readonly Dictionary<Slot, ulong> Touching = new();
    }

    // The text a watched field's change will be heard with while its call waits to run: a burst of keys is
    // heard once, with the text the last of them left.
    private readonly Dictionary<Slot, EditText> _editWaiting = new();

    private sealed class EditText
    {
        public string Text = string.Empty;
    }

    private readonly Dictionary<Slot, Dictionary<User, ulong>> _hovering = new();
    private readonly List<User> _goneScratch = new();

    // The On Footstep nodes, heard from every avatar this machine poses in the script's world.
    private readonly List<ScriptEventEntry> _footsteps = new();
    private Action<Components.Avatar.AvatarIK, Components.Avatar.AvatarFootstep>? _onFootstep;

    // Spawns, saves, scoped variables and stream chat.
    private readonly ScriptWorldEventSources _world;

    // People touching watched colliders, for the On Person events.
    private readonly ScriptPersonContacts _persons;

    // A Local script hears the panel buttons and beams of whoever is at this machine itself, the way a
    // relay hears them for a Shared one.
    private readonly List<(Button Button, Action<UIInteractionContext> Submitted)> _buttons = new();
    private readonly List<(InteractionLaser Laser, Action<IInteractionTarget?> Changed, Action<IInteractionTarget, float3> Activated)> _beams = new();
    private readonly Dictionary<Slot, HashSet<InteractionLaser>> _beamsOver = new();
    private double _nextBeamLook = double.NegativeInfinity;

    private sealed class Trigger
    {
        public Slot Slot = null!;
        public readonly List<ScriptEventEntry> Entries = new();
        public HashSet<Slot> Inside = new();
    }

    private sealed class SyncedWatch
    {
        public Slot Slot = null!;
        public readonly List<ScriptEventEntry> Entries = new();
        public Dictionary<string, (string Type, string Value)> Seen = new(StringComparer.Ordinal);
        public Dictionary<string, (string Type, string Value)> Now = new(StringComparer.Ordinal);
    }

    public ScriptEventSources(ScriptHostRuntime runtime)
    {
        _runtime = runtime;
        _world = new ScriptWorldEventSources(runtime);
        _persons = new ScriptPersonContacts(runtime);
    }

    public void Subscribe(IEnumerable<ScriptEventEntry> events, Func<ScriptEventEntry, object?> sourceOf)
    {
        Unsubscribe();
        var grabs = new Dictionary<Grabbable, List<ScriptEventEntry>>();
        var fields = new Dictionary<IField, List<ScriptEventEntry>>();
        var deletes = new Dictionary<Slot, List<ScriptEventEntry>>();
        var collisions = new Dictionary<Slot, List<ScriptEventEntry>>();
        var seats = new Dictionary<Components.Avatar.Seat, List<ScriptEventEntry>>();
        var tools = new Dictionary<ToolItem, List<ScriptEventEntry>>();
        var home = _runtime.Host.Slot;
        foreach (var entry in events)
        {
            var type = entry.PayloadType;
            var source = sourceOf(entry);
            // An event node that hears its event by itself says how in its own Listen.
            if (EventListeners.TryListen(_runtime, entry, source, _undo))
                continue;
            if (type == typeof(GrabStarted) || type == typeof(GrabEnded))
            {
                if (source is Slot { IsDestroyed: false } slot && slot.GetComponent<Grabbable>() is { } grab)
                    Add(grabs, grab, entry);
            }
            else if (type == typeof(FieldChanged))
            {
                if (source is IField field && !field.IsDestroyed)
                    Add(fields, field, entry);
            }
            else if (type == typeof(SlotDestroyed))
            {
                if (source is Slot { IsDestroyed: false } slot)
                    Add(deletes, slot, entry);
            }
            else if (type == typeof(HoverStarted) || type == typeof(HoverEnded) || type == typeof(BeamClicked) || type == typeof(HoverStaying)
                || type == typeof(EditStarted) || type == typeof(TextEdited) || type == typeof(EditEnded) || type == typeof(TextSubmitted)
                || type == typeof(DropReceived) || type == typeof(ToolPressed) || type == typeof(ToolReleased))
            {
                if (source is Slot { IsDestroyed: false } slot)
                    Add(_relayed, slot, entry);
            }
            else if (type == typeof(ButtonPressed))
            {
                // A panel button on the script's own slot already reaches the host through the host itself.
                if (source is Slot { IsDestroyed: false } slot && !ReferenceEquals(slot, home))
                    Add(_relayed, slot, entry);
            }
            else if (IsPressStep(entry))
            {
                // Heard through the relay on the slot, the script's own as well.
                if (source is Slot { IsDestroyed: false } slot)
                    Add(_relayed, slot, entry);
            }
            else if (type == typeof(TriggerEntered) || type == typeof(TriggerExited) || type == typeof(TriggerStaying))
            {
                if (source is Slot { IsDestroyed: false } slot)
                    AddTrigger(slot, entry);
            }
            else if (type == typeof(CollisionEntered) || type == typeof(CollisionExited) || type == typeof(CollisionStaying))
            {
                if (source is Slot { IsDestroyed: false } slot)
                    Add(collisions, slot, entry);
            }
            else if (type == typeof(ToolEquipped) || type == typeof(ToolDequipped))
            {
                if (source is Slot { IsDestroyed: false } slot && slot.GetComponent<ToolItem>() is { IsDestroyed: false } item)
                    Add(tools, item, entry);
            }
            else if (type == typeof(Footstep))
            {
                _footsteps.Add(entry);
            }
            else if (type == typeof(SyncedChanged))
            {
                if (source is Slot { IsDestroyed: false } slot)
                    AddSynced(slot, entry);
            }
            else if (type == typeof(SeatTaken) || type == typeof(SeatLeft))
            {
                if (source is Slot { IsDestroyed: false } slot && slot.GetComponent<Components.Avatar.Seat>() is { IsDestroyed: false } seat)
                    Add(seats, seat, entry);
            }
            else if (type == typeof(PersonEntered) || type == typeof(PersonStaying) || type == typeof(PersonExited))
            {
                if (source is Slot { IsDestroyed: false } slot)
                    _persons.Add(slot, entry);
            }
            else
            {
                _world.Add(entry, source);
            }
        }
        _world.Start();
        foreach (var (grab, entries) in grabs)
            WatchGrab(grab, entries);
        foreach (var (field, entries) in fields)
            WatchField(field, entries);
        foreach (var (slot, entries) in deletes)
            WatchDelete(slot, entries);
        foreach (var (slot, entries) in collisions)
            WatchCollisions(slot, entries);
        foreach (var (seat, entries) in seats)
            WatchSeat(seat, entries);
        foreach (var (item, entries) in tools)
            WatchTool(item, entries);
        if (_footsteps.Count > 0)
        {
            _onFootstep = HeardFootstep;
            Components.Avatar.AvatarIK.Footstep += _onFootstep;
        }
        if (_runtime.IsLocal)
            ListenHere();
        else
            KeepRelays();
    }

    public void Unsubscribe()
    {
        foreach (var undo in _undo)
            undo();
        _undo.Clear();
        _relayed.Clear();
        _triggers.Clear();
        _synced.Clear();
        _fieldFrame.Clear();
        _stays.Clear();
        _editWaiting.Clear();
        _hovering.Clear();
        if (_onFootstep != null)
            Components.Avatar.AvatarIK.Footstep -= _onFootstep;
        _onFootstep = null;
        _footsteps.Clear();
        _world.Stop();
        _persons.Clear();
        foreach (var (button, submitted) in _buttons)
            button.Submitted -= submitted;
        _buttons.Clear();
        foreach (var (laser, changed, activated) in _beams)
        {
            laser.TargetChanged -= changed;
            laser.Activated -= activated;
        }
        _beams.Clear();
        _beamsOver.Clear();
        _nextBeamLook = double.NegativeInfinity;
    }

    // Unsubscribing for good: the relays this script put on objects go as well.
    public void Release()
    {
        Unsubscribe();
        KeepRelays();
    }

    // A panel button that takes presses, or a context menu entry that does.
    internal static bool Pressable(Slot? slot)
        => slot != null && (slot.GetComponent<Button>() is { CanInteract: true }
            || slot.GetComponent<Components.UI.ContextMenuEntry>() is { Enabled.Value: true, Interactable.Value: true });

    // On Released is a panel button's press ending as well as a touch control's, so it follows the steps too.
    internal static bool IsPressStep(ScriptEventEntry entry)
        => entry.PayloadType == typeof(PressBegan) || entry.PayloadType == typeof(PressHeld) || entry.PayloadType == typeof(PressEnded)
            || entry.PayloadType == typeof(ButtonReleased);

    // Where a press is on the element pressed: in the element's own layout space, and across its rect.
    internal static (float2 Local, float2 Normalized) PointsOn(InteractionElement element, in UIInteractionContext context)
    {
        var local = context.PointIn(element.Slot);
        var rect = element.RectTransform?.LocalComputeRect ?? default;
        var size = rect.Size;
        var from = local - rect.Min;
        return (local, new float2(size.x != 0f ? from.x / size.x : 0f, size.y != 0f ? from.y / size.y : 0f));
    }

    // A press landing on, held on, or ending on a panel button on a watched object, with the Touch domain any press
    // needs. A button may switch itself off the moment it is pressed, to hand the drag to what lies under it, so
    // only a hidden or non-interactable button is refused, and an ending is never refused while the button is there.
    // -xlinka
    public void RelayPressStep(User? user, Slot source, ScriptSourceKind kind, float3 point, float2 local, float2 normalized)
    {
        if (!_relayed.TryGetValue(source, out var entries) || source.IsDestroyed)
            return;
        if (source.GetComponent<Button>() is not { IsDestroyed: false } button
            || (kind != ScriptSourceKind.PressEnd && (!button.Interactable.Value || !source.IsActive)))
            return;
        if (source.World?.DataModelPermissions?.AllowsDomain(user, DataModelPermissionDomain.Touch) == false)
            return;
        foreach (var entry in entries)
        {
            var node = entry.NodeId;
            var type = entry.PayloadType;
            if (kind == ScriptSourceKind.PressBegin && type == typeof(PressBegan))
                _runtime.Enqueue(runner => runner.Raise(node, new PressBegan(user, point, local, normalized)));
            else if (kind == ScriptSourceKind.PressHold && type == typeof(PressHeld))
                _runtime.Enqueue(runner => runner.Raise(node, new PressHeld(user, point, local, normalized)));
            else if (kind == ScriptSourceKind.PressEnd && type == typeof(PressEnded))
                _runtime.Enqueue(runner => runner.Raise(node, new PressEnded(user, point, local, normalized)));
            else if (kind == ScriptSourceKind.PressEnd && type == typeof(ButtonReleased))
                _runtime.Enqueue(runner => runner.Raise(node, new ButtonReleased(user, point)));
        }
    }

    // What a relay on a watched object passed on, from whoever acted there.
    public void Relay(User? user, Slot source, ScriptSourceKind kind, float3 point)
    {
        if (!_relayed.TryGetValue(source, out var entries))
            return;
        if (kind is ScriptSourceKind.Press or ScriptSourceKind.Click
            && source.World?.DataModelPermissions?.AllowsDomain(user, DataModelPermissionDomain.Touch) == false)
            return;
        if (kind == ScriptSourceKind.Press && !Pressable(source))
            return;
        if (kind is ScriptSourceKind.HoverEnter or ScriptSourceKind.HoverExit && entries.Exists(entry => entry.PayloadType == typeof(HoverStaying)))
            TrackHover(user, source, kind);
        foreach (var entry in entries)
        {
            var node = entry.NodeId;
            var type = entry.PayloadType;
            if (kind == ScriptSourceKind.Press && type == typeof(ButtonPressed))
                _runtime.Enqueue(runner => runner.Raise(node, new ButtonPressed(user, point)));
            else if (kind == ScriptSourceKind.HoverEnter && type == typeof(HoverStarted))
                _runtime.Enqueue(runner => runner.Raise(node, new HoverStarted(user)));
            else if (kind == ScriptSourceKind.HoverExit && type == typeof(HoverEnded))
                _runtime.Enqueue(runner => runner.Raise(node, new HoverEnded(user)));
            else if (kind == ScriptSourceKind.Click && type == typeof(BeamClicked))
                _runtime.Enqueue(runner => runner.Raise(node, new BeamClicked(user, point)));
        }
    }

    // What a relay passed on that carries more than a point: typing into a field on the watched object, a
    // drop onto a surface on it, a scripted tool on it pressed or let go of. Typing needs the Touch domain
    // and a tool press ToolUse, the rights a person needs to do either by hand; a change of text is heard
    // once however many keys went into it before its call ran, with the text they left.
    public void Relay(User? user, Slot source, ScriptSourceKind kind, float3 point, string? text, Slot? other)
    {
        if (kind < ScriptSourceKind.EditStart)
        {
            Relay(user, source, kind, point);
            return;
        }
        if (!_relayed.TryGetValue(source, out var entries))
            return;
        var permissions = source.World?.DataModelPermissions;
        bool typing = kind is ScriptSourceKind.EditStart or ScriptSourceKind.EditChange or ScriptSourceKind.EditEnd or ScriptSourceKind.EditSubmit;
        bool pressing = kind is ScriptSourceKind.ToolPrimaryDown or ScriptSourceKind.ToolPrimaryUp
            or ScriptSourceKind.ToolSecondaryDown or ScriptSourceKind.ToolSecondaryUp;
        if (typing && (source.GetComponent<TextInput>() == null || permissions?.AllowsDomain(user, DataModelPermissionDomain.Touch) == false))
            return;
        // Only whoever has the tool equipped presses it, and only something that may be received lands.
        if (pressing && (source.GetComponent<ScriptedTool>() is not { ActiveTool: HandTool { IsDestroyed: false } hand }
                || !ReferenceEquals(hand.Slot?.ActiveUser, user)
                || permissions?.AllowsDomain(user, DataModelPermissionDomain.ToolUse) == false))
            return;
        if (kind == ScriptSourceKind.Received && (source.GetComponent<DropSurface>() == null || other is not { IsDestroyed: false }
                || ScriptHands.GrabbableAt(other) is not { Receivable: true }))
            return;
        string said = text ?? string.Empty;
        EditText? edit = null;
        if (kind == ScriptSourceKind.EditChange)
        {
            if (_editWaiting.TryGetValue(source, out var waiting))
            {
                waiting.Text = said;
                return;
            }
            if (!entries.Exists(entry => entry.PayloadType == typeof(TextEdited)))
                return;
            edit = new EditText { Text = said };
            _editWaiting[source] = edit;
        }
        bool secondary = kind is ScriptSourceKind.ToolSecondaryDown or ScriptSourceKind.ToolSecondaryUp;
        foreach (var entry in entries)
        {
            var node = entry.NodeId;
            var type = entry.PayloadType;
            if (kind == ScriptSourceKind.EditStart && type == typeof(EditStarted))
                _runtime.Enqueue(runner => runner.Raise(node, new EditStarted(user)));
            else if (kind == ScriptSourceKind.EditChange && type == typeof(TextEdited) && edit != null)
            {
                var heard = edit;
                _runtime.Enqueue(runner =>
                {
                    if (_editWaiting.TryGetValue(source, out var waiting) && ReferenceEquals(waiting, heard))
                        _editWaiting.Remove(source);
                    return runner.Raise(node, new TextEdited(user, heard.Text));
                });
            }
            else if (kind == ScriptSourceKind.EditEnd && type == typeof(EditEnded))
                _runtime.Enqueue(runner => runner.Raise(node, new EditEnded(user, said)));
            else if (kind == ScriptSourceKind.EditSubmit && type == typeof(TextSubmitted))
                _runtime.Enqueue(runner => runner.Raise(node, new TextSubmitted(user, said)));
            else if (kind == ScriptSourceKind.Received && type == typeof(DropReceived))
                _runtime.Enqueue(runner => runner.Raise(node, new DropReceived(other, user)));
            else if (kind is ScriptSourceKind.ToolPrimaryDown or ScriptSourceKind.ToolSecondaryDown && type == typeof(ToolPressed))
                _runtime.Enqueue(runner => runner.Raise(node, new ToolPressed(user, secondary)));
            else if (kind is ScriptSourceKind.ToolPrimaryUp or ScriptSourceKind.ToolSecondaryUp && type == typeof(ToolReleased))
                _runtime.Enqueue(runner => runner.Raise(node, new ToolReleased(user, secondary)));
        }
    }

    public void PollTriggers(double now)
    {
        _persons.Poll(now);
        if (_triggers.Count == 0 || now < _nextTriggers)
            return;
        _nextTriggers = now + TriggerSeconds;
        var physics = _runtime.Host.World?.Physics;
        if (physics == null)
            return;
        foreach (var watch in _triggers)
        {
            var slot = watch.Slot;
            if (slot.IsDestroyed)
                continue;
            // A switched off object holds nothing, as a switched off collider does: what was inside leaves.
            var inside = new HashSet<Slot>();
            foreach (var collider in slot.GetComponents<Collider>())
            {
                if (!collider.Enabled.Value || !slot.IsActive)
                    continue;
                Overlap(physics, collider, _hits, _people);
                foreach (var hit in _hits)
                {
                    if (hit is { IsDestroyed: false } && !ReferenceEquals(hit, slot) && !hit.IsDescendantOf(slot)
                        && !ScriptReads.IsWornPart(hit))
                        inside.Add(hit);
                }
                foreach (var user in _people)
                {
                    var body = ScriptReads.RootOf(user)?.Slot;
                    if (body is { IsDestroyed: false } && !slot.IsDescendantOf(body))
                        inside.Add(body);
                }
            }
            foreach (var hit in inside)
            {
                if (!watch.Inside.Contains(hit))
                {
                    var user = ScriptReads.BodyOf(hit);
                    Raise(watch.Entries, typeof(TriggerEntered), node => runner => runner.Raise(node, new TriggerEntered(hit, user)));
                }
            }
            foreach (var old in watch.Inside)
            {
                if (!inside.Contains(old))
                {
                    var user = ScriptReads.BodyOf(old);
                    Raise(watch.Entries, typeof(TriggerExited), node => runner => runner.Raise(node, new TriggerExited(old, user)));
                }
            }
            foreach (var hit in inside)
            {
                if (watch.Inside.Contains(hit))
                {
                    var user = ScriptReads.BodyOf(hit);
                    Raise(watch.Entries, typeof(TriggerStaying), node => runner => runner.Raise(node, new TriggerStaying(hit, user)));
                }
            }
            watch.Inside = inside;
        }
    }

    // Every name whose value or type moved since the last look, or that came or went, heard once.
    public void PollSynced()
    {
        foreach (var watch in _synced)
        {
            ReadSynced(watch.Slot, watch.Now);
            foreach (var (name, now) in watch.Now)
            {
                if (!watch.Seen.TryGetValue(name, out var seen) || seen != now)
                    RaiseSynced(watch.Entries, name);
            }
            foreach (var name in watch.Seen.Keys)
            {
                if (!watch.Now.ContainsKey(name))
                    RaiseSynced(watch.Entries, name);
            }
            (watch.Seen, watch.Now) = (watch.Now, watch.Seen);
        }
    }

    public void PollWorld() => _world.Poll();

    private void RaiseSynced(List<ScriptEventEntry> entries, string name)
        => Raise(entries, typeof(SyncedChanged), node => runner => runner.Raise(node, new SyncedChanged(name)));

    // What a slot's synced values read as now: for each name, the first host's, as Read Synced finds it.
    private static void ReadSynced(Slot slot, Dictionary<string, (string Type, string Value)> into)
    {
        into.Clear();
        if (slot.IsDestroyed)
            return;
        var components = slot.Components;
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i] is not ScriptHost { IsDestroyed: false } host)
                continue;
            foreach (var value in host.Synced)
            {
                string name = value.Key.Value ?? string.Empty;
                if (name.Length > 0)
                    into.TryAdd(name, (value.Type.Value ?? string.Empty, value.Value.Value ?? string.Empty));
            }
        }
    }

    private void AddSynced(Slot slot, ScriptEventEntry entry)
    {
        var watch = _synced.Find(w => ReferenceEquals(w.Slot, slot));
        if (watch == null)
        {
            watch = new SyncedWatch { Slot = slot };
            ReadSynced(slot, watch.Seen);
            _synced.Add(watch);
        }
        watch.Entries.Add(entry);
    }

    private static void Overlap(WorldPhysics physics, Collider collider, List<Slot> into, List<User> people)
    {
        var slot = collider.Slot;
        var scale = slot.GlobalScale;
        switch (collider)
        {
            case BoxCollider box:
                var size = box.Size.Value;
                var center = slot.LocalPointToGlobal(box.Offset.Value);
                var extent = new float3(size.x * scale.x, size.y * scale.y, size.z * scale.z);
                physics.OverlapBox(center, extent, slot.GlobalRotation, into, hitTriggers: true);
                physics.OverlapPeople(center, extent, slot.GlobalRotation, people);
                break;
            case SphereCollider sphere:
                float grow = MathF.Max(MathF.Abs(scale.x), MathF.Max(MathF.Abs(scale.y), MathF.Abs(scale.z)));
                var middle = slot.LocalPointToGlobal(sphere.Offset.Value);
                physics.OverlapSphere(middle, sphere.Radius.Value * grow, into, hitTriggers: true);
                physics.OverlapPeople(middle, sphere.Radius.Value * grow, people);
                break;
            default:
                // Any other shape by its bounds, turned with the slot; one with no size yet, a hull not worked
                // out, finds nothing.
                var bounds = collider.GetLocalBounds();
                var span = bounds.Max - bounds.Min;
                var across = new float3(MathF.Abs(span.x * scale.x), MathF.Abs(span.y * scale.y), MathF.Abs(span.z * scale.z));
                if (!(across.x > 1e-5f && across.y > 1e-5f && across.z > 1e-5f))
                {
                    into.Clear();
                    people.Clear();
                    break;
                }
                var mid = slot.LocalPointToGlobal((bounds.Min + bounds.Max) * 0.5f);
                physics.OverlapBox(mid, across, slot.GlobalRotation, into, hitTriggers: true);
                physics.OverlapPeople(mid, across, slot.GlobalRotation, people);
                break;
        }
    }

    // Touches the physics hook reports for the watched slot, from either side of the touch. Two moving bodies
    // that meet each report it, so a thing begins touching with its first report and stops with its last.
    // -xlinka
    private void WatchCollisions(Slot slot, List<ScriptEventEntry> entries)
    {
        var physics = slot.World?.Physics;
        if (physics == null)
            return;
        ContactStays? stays = null;
        if (entries.Exists(entry => entry.PayloadType == typeof(CollisionStaying)))
        {
            stays = new ContactStays { Slot = slot, Entries = entries };
            _stays.Add(stays);
        }
        var reports = new Dictionary<Slot, int>();
        var stop = physics.WatchContacts(slot,
            contact =>
            {
                if (contact.Other is { } other)
                {
                    reports.TryGetValue(other, out int count);
                    reports[other] = count + 1;
                    if (count > 0)
                        return;
                }
                var user = ScriptReads.BodyOf(contact.Other);
                if (stays != null && contact.Other != null)
                    stays.Touching[contact.Other] = slot.World?.Time.UpdateIndex ?? 0;
                Raise(entries, typeof(CollisionEntered), node => runner => runner.Raise(node,
                    new CollisionEntered(contact.Other, user, contact.Point, contact.Normal, contact.Impulse)));
            },
            contact =>
            {
                // A touch that began before the watch did was never heard beginning, so its end is not heard either.
                if (contact.Other is { } other)
                {
                    if (!reports.TryGetValue(other, out int count))
                        return;
                    if (count > 1)
                    {
                        reports[other] = count - 1;
                        return;
                    }
                    reports.Remove(other);
                }
                var user = ScriptReads.BodyOf(contact.Other);
                if (stays != null && contact.Other != null)
                    stays.Touching.Remove(contact.Other);
                Raise(entries, typeof(CollisionExited), node => runner => runner.Raise(node, new CollisionExited(contact.Other, user)));
            });
        _undo.Add(stop);
    }

    // Once a frame: every touch that began before this frame and has not ended, and every beam that came onto
    // a watched object before this frame and is still on it. A thing or a person gone without the touch or the
    // beam ending is let go of here.
    public void PollStays()
    {
        ulong frame = _runtime.Host.World?.Time.UpdateIndex ?? 0;
        PollHovers(frame);
        foreach (var stays in _stays)
        {
            _stayScratch.Clear();
            foreach (var (other, began) in stays.Touching)
            {
                if (other.IsDestroyed)
                    _stayScratch.Add(other);
                else if (began < frame)
                {
                    var user = ScriptReads.BodyOf(other);
                    var touching = other;
                    Raise(stays.Entries, typeof(CollisionStaying), node => runner => runner.Raise(node, new CollisionStaying(touching, user)));
                }
            }
            foreach (var gone in _stayScratch)
                stays.Touching.Remove(gone);
        }
    }

    private void PollHovers(ulong frame)
    {
        if (_hovering.Count == 0)
            return;
        foreach (var (slot, people) in _hovering)
        {
            if (slot.IsDestroyed || !_relayed.TryGetValue(slot, out var entries))
                continue;
            _goneScratch.Clear();
            foreach (var (user, began) in people)
            {
                if (user.IsDestroyed)
                    _goneScratch.Add(user);
                else if (began < frame)
                {
                    var who = user;
                    Raise(entries, typeof(HoverStaying), node => runner => runner.Raise(node, new HoverStaying(who)));
                }
            }
            foreach (var gone in _goneScratch)
                people.Remove(gone);
        }
    }

    // Who has a beam on each watched object, and the frame it came on, for On Hover Stay.
    private void TrackHover(User? user, Slot source, ScriptSourceKind kind)
    {
        if (user == null)
            return;
        if (!_hovering.TryGetValue(source, out var people))
        {
            if (kind != ScriptSourceKind.HoverEnter)
                return;
            people = new Dictionary<User, ulong>();
            _hovering[source] = people;
        }
        if (kind == ScriptSourceKind.HoverEnter)
            people[user] = _runtime.Host.World?.Time.UpdateIndex ?? 0;
        else
            people.Remove(user);
    }

    // A foot in this world came down: named with its person and whatever a short ray finds under it, their
    // own body left out.
    private void HeardFootstep(Components.Avatar.AvatarIK ik, Components.Avatar.AvatarFootstep step)
    {
        var world = _runtime.Host.World;
        if (ik == null || world == null || !ReferenceEquals(ik.World, world) || ik.UserRoot.Target is not { IsDestroyed: false } root)
            return;
        var user = root.ActiveUser;
        if (user == null || user.IsDestroyed)
            return;
        Slot? ground = null;
        var exclude = new List<Slot>(1) { root.Slot };
        if (world.Physics.Raycast(step.Position + float3.Up * 0.1f, float3.Down, exclude, 0.35f) is { } hit
            && ScriptReads.Visible(_runtime.Runner.Context, hit.Slot))
            ground = hit.Slot;
        var footstep = new Footstep(user, step.Side, step.Position, step.Rotation, step.Speed, step.Landing, ground);
        foreach (var entry in _footsteps)
        {
            var node = entry.NodeId;
            _runtime.Enqueue(runner => runner.Raise(node, footstep));
        }
    }

    // A tool is equipped when its link to a hand is set and put away when it clears; the link replicates,
    // so the host hears a guest's equip. Same init-phase rule as a grab. -xlinka
    private void WatchTool(ToolItem item, List<ScriptEventEntry> entries)
    {
        var held = item.OverrideActiveTool.Target;
        ReferenceEvent<Tool> changed = reference =>
        {
            if (reference.IsInInitPhase)
                return;
            var now = reference.Target;
            if (ReferenceEquals(now, held))
                return;
            if (held != null)
            {
                var from = held.IsDestroyed ? null : held.Slot?.ActiveUser;
                var side = held.IsDestroyed ? Chirality.None : held.Side.Value;
                Raise(entries, typeof(ToolDequipped), node => runner => runner.Raise(node, new ToolDequipped(from, side)));
            }
            if (now != null)
            {
                var by = now.Slot?.ActiveUser;
                var side = now.Side.Value;
                Raise(entries, typeof(ToolEquipped), node => runner => runner.Raise(node, new ToolEquipped(by, side)));
            }
            held = now;
        };
        item.OverrideActiveTool.OnTargetChange += changed;
        _undo.Add(() => item.OverrideActiveTool.OnTargetChange -= changed);
    }

    // A guest's grab arrives as a replicated change, applied while the reference reads as loading, so
    // only the init phase is skipped here. Comparing with the last holder drops the repeats. -xlinka
    private void WatchGrab(Grabbable grab, List<ScriptEventEntry> entries)
    {
        var held = grab.Grabber;
        ReferenceEvent<Grabber> changed = reference =>
        {
            if (reference.IsInInitPhase)
                return;
            var now = reference.Target;
            if (ReferenceEquals(now, held))
                return;
            if (held != null)
            {
                var from = held.OwningUser;
                Raise(entries, typeof(GrabEnded), node => runner => runner.Raise(node, new GrabEnded(from)));
            }
            if (now != null)
            {
                var by = now.OwningUser;
                Raise(entries, typeof(GrabStarted), node => runner => runner.Raise(node, new GrabStarted(by)));
            }
            held = now;
        };
        grab.GrabberRef.OnTargetChange += changed;
        _undo.Add(() => grab.GrabberRef.OnTargetChange -= changed);
    }

    // The host's ruling on who holds the seat, the one every machine agrees on: someone left it when the
    // ruling moves off them, took it when it names them. A move straight from one person to another is both.
    private void WatchSeat(Components.Avatar.Seat seat, List<ScriptEventEntry> entries)
    {
        var ruled = seat.Occupant.Target;
        ReferenceEvent<User> changed = reference =>
        {
            if (reference.IsInInitPhase)
                return;
            var now = reference.Target;
            if (ReferenceEquals(now, ruled))
                return;
            if (ruled != null)
            {
                var left = ruled;
                Raise(entries, typeof(SeatLeft), node => runner => runner.Raise(node, new SeatLeft(left)));
            }
            if (now != null)
            {
                var took = now;
                Raise(entries, typeof(SeatTaken), node => runner => runner.Raise(node, new SeatTaken(took)));
            }
            ruled = now;
        };
        seat.Occupant.OnTargetChange += changed;
        _undo.Add(() => seat.Occupant.OnTargetChange -= changed);
    }

    // Once a frame at most, however many writes the frame held.
    private void WatchField(IField field, List<ScriptEventEntry> entries)
    {
        Action<IChangeable> changed = _ =>
        {
            ulong frame = _runtime.Host.World?.Time.UpdateIndex ?? 0;
            if (_fieldFrame.TryGetValue(field, out ulong last) && last == frame)
                return;
            _fieldFrame[field] = frame;
            // The host learns who wrote a change as it decodes one that came in; one made here leaves the last
            // writer in place until it goes out, so naming it would name whoever wrote before. -xlinka
            var by = field is ConflictingSyncElement { IsLoading: true } member ? member.LastModifyingUser : null;
            Raise(entries, typeof(FieldChanged), node => runner => runner.Raise(node, new FieldChanged(by)));
        };
        field.Changed += changed;
        _undo.Add(() => field.Changed -= changed);
    }

    // Destroyed, or parked where an undo can bring it back: either way it is gone from the world. Brought
    // back, it can go again.
    private void WatchDelete(Slot slot, List<ScriptEventEntry> entries)
    {
        bool gone = false;
        void Gone()
        {
            if (gone)
                return;
            gone = true;
            var name = slot.Name.Value ?? string.Empty;
            Raise(entries, typeof(SlotDestroyed), node => runner => runner.Raise(node, new SlotDestroyed(name)));
        }
        Action<Slot> destroyed = _ => Gone();
        ReferenceEvent<Slot> moved = reference =>
        {
            if (reference.IsInInitPhase)
                return;
            var graveyard = UndoGraveyard.Find(slot.World);
            if (graveyard != null && ReferenceEquals(reference.Target, graveyard))
                Gone();
            else
                gone = false;
        };
        slot.OnPrepareDestroy += destroyed;
        slot.ParentSlotRef.OnTargetChange += moved;
        _undo.Add(() =>
        {
            slot.OnPrepareDestroy -= destroyed;
            slot.ParentSlotRef.OnTargetChange -= moved;
        });
    }

    // One relay per watched object for this script, put there by the authority, never saved; the ones no
    // longer wanted are taken away.
    private void ListenHere()
    {
        foreach (var (slot, entries) in _relayed)
        {
            if (!entries.Exists(entry => entry.PayloadType == typeof(ButtonPressed)) || slot.GetComponent<Button>() is not { } button)
                continue;
            var source = slot;
            Action<UIInteractionContext> submitted = context => Relay(context.Actor ?? source.World?.LocalUser, source, ScriptSourceKind.Press, context.WorldPoint);
            button.Submitted += submitted;
            _buttons.Add((button, submitted));
        }
        foreach (var (slot, entries) in _relayed)
        {
            if (entries.Exists(IsPressStep) && slot.GetComponent<Button>() is { } pressed)
                ListenToSteps(slot, pressed);
        }
        foreach (var slot in _relayed.Keys)
            ListenToParts(slot);
    }

    // A press landing on, held on and ending on a panel button, as whoever is at this machine works it.
    private void ListenToSteps(Slot slot, Button button)
    {
        void Step(ScriptSourceKind kind, in UIInteractionContext context)
        {
            var (local, normalized) = PointsOn(button, in context);
            RelayPressStep(context.Actor ?? slot.World?.LocalUser, slot, kind, context.WorldPoint, local, normalized);
        }
        Action<UIInteractionContext> began = context => Step(ScriptSourceKind.PressBegin, in context);
        Action<UIInteractionContext> held = context => Step(ScriptSourceKind.PressHold, in context);
        Action<UIInteractionContext> ended = context => Step(ScriptSourceKind.PressEnd, in context);
        button.Pressed += began;
        button.Dragged += held;
        button.Released += ended;
        _undo.Add(() =>
        {
            button.Pressed -= began;
            button.Dragged -= held;
            button.Released -= ended;
        });
    }

    // The typing, drops and tool presses of whoever is at this machine, on a watched object, heard here at
    // once, the way its relay would pass them on.
    private void ListenToParts(Slot slot)
    {
        var local = slot.World?.LocalUser;
        foreach (var field in slot.GetComponents<TextInput>())
        {
            Action<TextInput> started = _ => Relay(local, slot, ScriptSourceKind.EditStart, float3.Zero, string.Empty, null);
            Action<TextInput, string> changed = (_, text) => Relay(local, slot, ScriptSourceKind.EditChange, float3.Zero, text, null);
            Action<TextInput, string> ended = (_, text) => Relay(local, slot, ScriptSourceKind.EditEnd, float3.Zero, text, null);
            Action<TextInput, string> submitted = (_, text) => Relay(local, slot, ScriptSourceKind.EditSubmit, float3.Zero, text, null);
            field.EditingStarted += started;
            field.EditingChanged += changed;
            field.FocusLost += ended;
            field.EditingFinished += submitted;
            _undo.Add(() =>
            {
                field.EditingStarted -= started;
                field.EditingChanged -= changed;
                field.FocusLost -= ended;
                field.EditingFinished -= submitted;
            });
        }
        foreach (var surface in slot.GetComponents<DropSurface>())
        {
            Action<IGrabbable, Grabber> received = (grabbable, grabber) =>
                Relay(grabber?.OwningUser ?? local, slot, ScriptSourceKind.Received, float3.Zero, null, (grabbable as Component)?.Slot);
            surface.Received += received;
            _undo.Add(() => surface.Received -= received);
        }
        foreach (var tool in slot.GetComponents<ScriptedTool>())
        {
            Action<ScriptedTool, bool, bool> pressed = (_, secondary, down) => Relay(local, slot,
                secondary ? (down ? ScriptSourceKind.ToolSecondaryDown : ScriptSourceKind.ToolSecondaryUp)
                    : (down ? ScriptSourceKind.ToolPrimaryDown : ScriptSourceKind.ToolPrimaryUp),
                float3.Zero, null, null);
            tool.Pressed += pressed;
            _undo.Add(() => tool.Pressed -= pressed);
        }
    }

    private int _beamsSearchedAt = -1;
    private Slot? _beamsUnder;

    // The beams of whoever is at this machine, looked for about once a second, and only when the body's
    // structure changed since the last look.
    public void FollowBeams(double now)
    {
        if (!_runtime.IsLocal || _relayed.Count == 0 || now < _nextBeamLook)
            return;
        _nextBeamLook = now + 1.0;
        for (int i = _beams.Count - 1; i >= 0; i--)
        {
            if (!_beams[i].Laser.IsDestroyed)
                continue;
            foreach (var over in _beamsOver.Values)
                over.Remove(_beams[i].Laser);
            _beams.RemoveAt(i);
        }
        var root = _runtime.Host.World?.LocalUser?.Root?.Slot;
        if (root == null || root.IsDestroyed)
            return;
        int structure = Slot.StructureEpoch;
        if (structure == _beamsSearchedAt && ReferenceEquals(root, _beamsUnder))
            return;
        _beamsSearchedAt = structure;
        _beamsUnder = root;
        foreach (var laser in root.GetComponentsInChildren<InteractionLaser>())
        {
            if (_beams.Exists(beam => ReferenceEquals(beam.Laser, laser)))
                continue;
            var beam = laser;
            Action<IInteractionTarget?> changed = target => Hovered(beam, target);
            Action<IInteractionTarget, float3> activated = (target, point) =>
            {
                foreach (var slot in _relayed.Keys)
                {
                    if (Covers(slot, target))
                        Relay(slot.World?.LocalUser, slot, ScriptSourceKind.Click, point);
                }
            };
            laser.TargetChanged += changed;
            laser.Activated += activated;
            _beams.Add((laser, changed, activated));
        }
    }

    private void Hovered(InteractionLaser laser, IInteractionTarget? target)
    {
        foreach (var slot in _relayed.Keys)
        {
            if (!_beamsOver.TryGetValue(slot, out var over))
            {
                over = new HashSet<InteractionLaser>();
                _beamsOver[slot] = over;
            }
            bool before = over.Count > 0;
            if (Covers(slot, target))
                over.Add(laser);
            else
                over.Remove(laser);
            bool after = over.Count > 0;
            if (after != before)
                Relay(slot.World?.LocalUser, slot, after ? ScriptSourceKind.HoverEnter : ScriptSourceKind.HoverExit, float3.Zero);
        }
    }

    private static bool Covers(Slot slot, IInteractionTarget? target)
        => target is Component { Slot: { } hit } && !slot.IsDestroyed && (ReferenceEquals(hit, slot) || hit.IsDescendantOf(slot));

    private void KeepRelays()
    {
        if (_runtime.IsLocal)
            return;
        var host = _runtime.Host;
        for (int i = _relays.Count - 1; i >= 0; i--)
        {
            var relay = _relays[i];
            if (relay.IsDestroyed || !_relayed.ContainsKey(relay.Slot))
            {
                if (!relay.IsDestroyed)
                {
                    using (relay.World?.DataModelPermissions?.EnterSystemBypass())
                        relay.Destroy();
                }
                _relays.RemoveAt(i);
            }
        }
        foreach (var slot in _relayed.Keys)
        {
            if (_relays.Exists(r => ReferenceEquals(r.Slot, slot)) || slot.World is not { IsAuthority: true } world)
                continue;
            ScriptSourceRelay? relay = null;
            foreach (var existing in slot.GetComponents<ScriptSourceRelay>())
            {
                if (ReferenceEquals(existing.Host.Target, host))
                    relay = existing;
            }
            if (relay == null)
            {
                using (world.DataModelPermissions?.EnterSystemBypass())
                {
                    relay = slot.AttachComponent<ScriptSourceRelay>();
                    relay.Persistent = false;
                    relay.Host.Target = host;
                }
            }
            _relays.Add(relay);
        }
        // Each relay passes on press steps only where something listens to them, so a press held on a button
        // nobody follows that closely costs nothing on the wire.
        foreach (var relay in _relays)
        {
            bool steps = _relayed.TryGetValue(relay.Slot, out var entries) && entries.Exists(IsPressStep);
            if (relay.PressSteps.Value == steps || relay.World is not { IsAuthority: true } world)
                continue;
            using (world.DataModelPermissions?.EnterSystemBypass())
                relay.PressSteps.Value = steps;
        }
    }

    private void AddTrigger(Slot slot, ScriptEventEntry entry)
    {
        var watch = _triggers.Find(t => ReferenceEquals(t.Slot, slot));
        if (watch == null)
        {
            watch = new Trigger { Slot = slot };
            _triggers.Add(watch);
        }
        watch.Entries.Add(entry);
    }

    private void Raise(List<ScriptEventEntry> entries, Type payload, Func<string, Func<ScriptRunner, ScriptCallResult>> call)
    {
        foreach (var entry in entries)
        {
            if (entry.PayloadType == payload)
                _runtime.Enqueue(call(entry.NodeId));
        }
    }

    private static void Add<TKey>(Dictionary<TKey, List<ScriptEventEntry>> map, TKey key, ScriptEventEntry entry) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<ScriptEventEntry>();
            map[key] = list;
        }
        list.Add(entry);
    }
}

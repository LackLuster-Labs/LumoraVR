// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Inactive non-persistent holding pen for slots whose destruction is undoable. Parking instead of
// destroying means undo needs no serialization round-trip; parked slots are destroyed for real when
// their step leaves the history.
public static class UndoGraveyard
{
    private const string SlotName = "UndoGraveyard";

    public static Slot? Acquire(World? world)
    {
        var root = world?.RootSlot;
        if (root == null)
            return null;

        var graveyard = root.FindChild(SlotName, recursive: false);
        if (graveyard == null || graveyard.IsDestroyed)
        {
            // Made in the maker's own allocation. In the world's byte a frozen world refuses even the host, and
            // the half-made pen that refusal left behind took in what was parked next, on the host alone. -xlinka
            using var own = world!.EnterLocalUserAllocation();
            graveyard = root.AddSlot(SlotName);
            graveyard.Persistent.Value = false;
            graveyard.ActiveSelf.Value = false;
        }
        return graveyard;
    }

    public static Slot? Find(World? world)
    {
        var graveyard = world?.RootSlot?.FindChild(SlotName, recursive: false);
        return graveyard == null || graveyard.IsDestroyed ? null : graveyard;
    }

    // What is parked here has been destroyed as far as anyone looking is concerned; only undo can see it.
    public static bool IsGraveyard(Slot? slot)
        => slot != null && slot.Parent is { IsRootSlot: true } && slot.SlotName.Value == SlotName;

    // Parked anywhere in here is out of the world, though the slot itself still reads alive.
    public static bool Holds(Slot? slot)
    {
        for (var current = slot; current != null; current = current.Parent)
        {
            if (IsGraveyard(current))
                return true;
        }
        return false;
    }

    // Called when a manager tears down. The pen is shared, so it only goes when the last parked slot
    // has gone with it - and only the authority swings the axe, or every peer that saw the same user
    // leave would send its own destroy for the same slot.
    public static void DisposeIfEmpty(World? world)
    {
        if (world == null || !world.IsAuthority)
            return;
        var graveyard = Find(world);
        if (graveyard != null && graveyard.Children.Count == 0 && graveyard.Components.Count == 0)
            graveyard.Destroy();
    }
}

// Reversible existence change for a set of slots: Destroy (park on record, undo restores) and
// Duplicate/Create (undo parks, redo restores) are the two directions of the same operation.
//
// Restoring is refused outright when the slot's original parent has been destroyed since. The old
// behaviour was to fall back to the world root, which quietly resurrected a piece of a deleted
// hierarchy at the world origin and reported success; failing instead lets the manager drop the step
// and the eviction finishes the destroy the user asked for. -xlinka
public sealed class SlotExistenceUndoBatch : IUndoBatch, IUndoTargetQuery
{
    private sealed class Entry
    {
        public Slot Slot = null!;
        public Slot? OriginalParent;
        public float3 LocalPosition;
        public floatQ LocalRotation;
        public float3 LocalScale;
        public bool WasActive;
        public readonly List<Survivor> Survivors = new();
    }

    // What the park let out to the world root, and where it hung, so an undo can hang it back.
    private readonly struct Survivor
    {
        public readonly Slot Slot;
        public readonly Slot? Parent;
        public readonly float3 LocalPosition;
        public readonly floatQ LocalRotation;
        public readonly float3 LocalScale;

        public Survivor(Slot slot)
        {
            Slot = slot;
            Parent = slot.Parent;
            LocalPosition = slot.LocalPosition.Value;
            LocalRotation = slot.LocalRotation.Value;
            LocalScale = slot.LocalScale.Value;
        }
    }

    private readonly List<Entry> _entries = new();
    private readonly World _world;
    private readonly bool _isDestroy;
    private bool _parked;

    public LocaleText LocalizedDescription { get; }

    public string Description => LocalizedDescription.Resolve();

    private SlotExistenceUndoBatch(World world, LocaleText description, bool isDestroy)
    {
        _world = world;
        LocalizedDescription = description;
        _isDestroy = isDestroy;
    }

    // Park the slots now (undoable destroy). Null if nothing could be parked. A slot the actor may not
    // destroy is left alone, for the caller's plain destroy to refuse: parking is only a move, so a guest
    // could park someone else's thing, and the real destroy the park stood for was then refused when the
    // step left the history, which left it in the pen for good, out of sight. -xlinka
    public static SlotExistenceUndoBatch? Destroy(World? world, IEnumerable<Slot> slots)
        => Destroy(world, slots, UndoLocale.Destroy);

    public static SlotExistenceUndoBatch? Destroy(World? world, IEnumerable<Slot> slots, LocaleText description)
    {
        var batch = Create(world, slots, description, isDestroy: true);
        if (batch == null)
            return null;
        return batch.Park() ? batch : null;
    }

    // Track freshly created slots (undoable duplicate/spawn).
    public static SlotExistenceUndoBatch? Created(World? world, IEnumerable<Slot> slots, LocaleText description)
        => Create(world, slots, description, isDestroy: false);

    private static SlotExistenceUndoBatch? Create(World? world, IEnumerable<Slot> slots, LocaleText description, bool isDestroy)
    {
        if (world == null)
            return null;

        var batch = new SlotExistenceUndoBatch(world, description, isDestroy);
        foreach (var slot in slots)
        {
            if (slot == null || slot.IsDestroyed)
                continue;
            // Parking the root would try to reparent the world under the graveyard and switch it off.
            // Both writes are refused, so the step would replay as nothing while reporting success. -xlinka
            if (slot.IsRootSlot)
                continue;
            if (isDestroy && !slot.MayDestroy())
                continue;
            batch._entries.Add(new Entry
            {
                Slot = slot,
                OriginalParent = slot.Parent,
                LocalPosition = slot.LocalPosition.Value,
                LocalRotation = slot.LocalRotation.Value,
                LocalScale = slot.LocalScale.Value,
                WasActive = slot.ActiveSelf.Value,
            });
        }
        return batch._entries.Count > 0 ? batch : null;
    }

    public bool Undo() => _isDestroy ? Restore() : Park();

    public bool Redo() => _isDestroy ? Park() : Restore();

    public bool ReferencesElement(IWorldElement element)
    {
        if (element is not Slot slot)
            return false;
        foreach (var entry in _entries)
        {
            if (UndoTargets.Touches(entry.Slot, slot) || UndoTargets.Touches(entry.OriginalParent, slot))
                return true;
        }
        return false;
    }

    private bool Park()
    {
        var graveyard = UndoGraveyard.Acquire(_world);
        if (graveyard == null)
            return false;

        bool any = false;
        foreach (var entry in _entries)
        {
            if (entry.Slot.IsDestroyed || UndoTargets.ContainsWornAvatar(entry.Slot))
                continue;
            // What the destroy would leave standing comes out first, as the destroy would let it out. When
            // this machine cannot move one of those by itself, the delete is a real one: a park is a plain
            // move nobody else answers, where every peer answers a destroy. The same when the actor may take
            // the slot down but not move it, a moderator on someone else's thing.
            if (!entry.Slot.ParentSlotRef.IsWriteAllowedQuietly() || !entry.Slot.ActiveSelf.IsWriteAllowedQuietly()
                || !LetOutSurvivors(entry))
            {
                entry.Slot.Destroy();
                continue;
            }
            entry.Slot.SetParent(graveyard);
            entry.Slot.ActiveSelf.Value = false;
            any = true;
        }
        _parked = any;
        return any;
    }

    private static readonly List<Slot> SurvivorScratch = new();

    private static bool LetOutSurvivors(Entry entry)
    {
        entry.Survivors.Clear();
        if (!entry.Slot.CollectParkSurvivors(SurvivorScratch))
            return false;
        foreach (var survivor in SurvivorScratch)
        {
            entry.Survivors.Add(new Survivor(survivor));
            Slot.LetOut(survivor);
        }
        SurvivorScratch.Clear();
        return true;
    }

    // Back where the park found them, when they still stand where it left them and this machine may hang
    // them there. A person who was let down stays standing.
    private void HangBackSurvivors(Entry entry)
    {
        var root = _world.RootSlot;
        foreach (var survivor in entry.Survivors)
        {
            var slot = survivor.Slot;
            var parent = survivor.Parent;
            if (slot == null || slot.IsDestroyed || parent == null || parent.IsDestroyed || !ReferenceEquals(slot.Parent, root)
                || (slot.ActiveUserRoot is { } body && ReferenceEquals(body.Slot, slot)))
                continue;
            Slot.HangBack(slot, parent, survivor.LocalPosition, survivor.LocalRotation, survivor.LocalScale);
        }
        entry.Survivors.Clear();
    }

    private bool Restore()
    {
        // All or nothing on the destination: a parent that has since been destroyed means this step
        // has nowhere valid to put its slots back.
        foreach (var entry in _entries)
        {
            if (!entry.Slot.IsDestroyed && UndoGraveyard.Holds(entry.Slot) && !UndoTargets.CanRestoreUnder(entry.OriginalParent))
                return false;
        }

        bool any = false;
        foreach (var entry in _entries)
        {
            if (entry.Slot.IsDestroyed || !UndoGraveyard.Holds(entry.Slot) || UndoTargets.ContainsWornAvatar(entry.Slot))
                continue;

            var parent = entry.OriginalParent ?? _world.RootSlot;
            if (parent == null)
                continue;

            PutBack(entry, parent);
            any = true;
        }
        if (any)
            _parked = false;
        return any;
    }

    private void PutBack(Entry entry, Slot parent)
    {
        entry.Slot.SetParent(parent);
        entry.Slot.LocalPosition.Value = entry.LocalPosition;
        entry.Slot.LocalRotation.Value = entry.LocalRotation;
        entry.Slot.LocalScale.Value = entry.LocalScale;
        entry.Slot.ActiveSelf.Value = entry.WasActive;
        HangBackSurvivors(entry);
    }

    // A parked slot's materials kept drawing on everything else that wears them (an orb deleted after painting
    // with it), so the step leaving the history must not strip them off those surfaces long after the delete
    // looked done. What is still worn outside goes to an assets slot. Anything else is one plain destroy, not
    // a component-by-component strip of a whole parked avatar. -xlinka
    public void OnEvicted()
    {
        if (!_parked)
            return;
        foreach (var entry in _entries)
        {
            if (entry.Slot.IsDestroyed)
                continue;
            if (!UndoGraveyard.Holds(entry.Slot))
                continue;
            // Rights can change while a step waits, a role taken away. The destroy this park stood for is
            // refused then, and the slot sat in the pen for good, out of sight on every peer. It goes back
            // where it was instead, the honest end of a delete that did not happen; the world root when
            // that is gone. A put-back this machine may not write either leaves it parked, with a warning.
            if (!entry.Slot.MayDestroy())
            {
                GiveBack(entry);
                continue;
            }
            if (entry.Slot.HasAssetsReferencedOutside())
                entry.Slot.DestroyPreservingAssets();
            else
                entry.Slot.Destroy();
        }
        _parked = false;
    }

    private void GiveBack(Entry entry)
    {
        var parent = entry.OriginalParent is { IsDestroyed: false } original ? original : _world.RootSlot;
        if (parent == null || entry.Slot.IsDestroyed)
            return;
        try
        {
            PutBack(entry, parent);
            Logging.Logger.Log($"Undo: '{entry.Slot.Name.Value}' could no longer be destroyed by this user and was put back where it was.");
        }
        catch (System.UnauthorizedAccessException ex)
        {
            Logging.Logger.Warn($"Undo: '{entry.Slot.Name.Value}' can neither be destroyed nor put back by this user and stays parked: {ex.Message}");
        }
    }
}

// Reversible user scale change (Reset Scale).
public sealed class UserScaleUndoBatch : IUndoBatch
{
    private readonly UserRoot _userRoot;
    private readonly float _before;
    private readonly float _after;

    public LocaleText LocalizedDescription => UndoLocale.Scale;

    public string Description => LocalizedDescription.Resolve();

    public UserScaleUndoBatch(UserRoot userRoot, float before, float after)
    {
        _userRoot = userRoot;
        _before = before;
        _after = after;
    }

    public bool Undo() => Apply(_before);

    public bool Redo() => Apply(_after);

    private bool Apply(float scale)
    {
        if (_userRoot == null || _userRoot.IsDestroyed)
            return false;
        var feet = _userRoot.FeetPosition;
        _userRoot.GlobalScale = scale;
        _userRoot.ShiftBy(feet - _userRoot.FeetPosition);
        return true;
    }

    public void OnEvicted()
    {
    }
}

// Shared rules for "can this step still put something back, and does it touch that slot".
internal static class UndoTargets
{
    // Wearing changes the meaning of a world object. Old spawn/move steps cannot take the body away. -xlinka
    public static bool ContainsWornAvatar(Slot slot)
    {
        foreach (var form in slot.GetComponentsInChildren<Avatar.AvatarForm>())
        {
            if (form.Wearer != null)
                return true;
        }
        return false;
    }

    // A null parent means the slot sat at the top of the world, which stays restorable.
    public static bool CanRestoreUnder(Slot? parent) => parent == null || !parent.IsDestroyed;

    public static bool Touches(Slot? candidate, Slot subtreeRoot)
    {
        if (candidate == null)
            return false;
        return ReferenceEquals(candidate, subtreeRoot) || candidate.IsDescendantOf(subtreeRoot);
    }
}

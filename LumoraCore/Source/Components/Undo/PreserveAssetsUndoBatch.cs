// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Components;

// Reversible "destroy but keep the shared assets": the strip runs as usual, and this record holds
// everything needed to put the object back exactly as it was, shared providers included.
//
// Two halves, because the two kinds of thing the strip removes have different escape routes.
//
// SLOTS are moved, never destroyed. An emptied slot goes to the undo graveyard instead of dying, so
// its RefID, its members and every reference aimed at it stay valid; undo is a reparent back to
// where it came from. Slots that KEPT a provider were already only moved (under the asset holder),
// so those reverse the same way. Nothing outside the object has to be re-bound, which is the whole
// point - the surviving providers are shared, and their users must not notice the round trip.
//
// COMPONENTS have no such route, since a component cannot change slots. Those are serialized while
// still live and re-attached on undo through the shared translator, so the references they held
// resolve back to their targets. What does not come back: [NonPersistent] member values, which the
// save path skips by design.
//
// Redo is not a replay of a recording, it re-runs the strip. The tree is back in its original shape
// by then, so the operation reaches the same answer and produces a fresh set of records. -xlinka
public sealed class PreserveAssetsUndoBatch : IUndoBatch, ISlotStripRecorder, IUndoTargetQuery
{
    private sealed class SlotRecord
    {
        public Slot Slot = null!;
        public Slot? Parent;
        public float3 Position;
        public floatQ Rotation;
        public float3 Scale;
        public bool Active;
        public bool Parked;
    }

    private sealed class ComponentRecord
    {
        public Slot Owner = null!;
        public Type Type = null!;
        public DataTreeNode Saved = null!;
        public List<RefID> Dead = null!;
    }

    private readonly World _world;
    private readonly Slot _root;
    private readonly Slot? _explicitHolder;
    private readonly Slot _graveyard;
    private readonly ReferenceTranslator _translator = new();

    private readonly List<SlotRecord> _slots = new();
    private readonly List<ComponentRecord> _components = new();
    private Slot? _createdHolder;
    private int _lost;
    private bool _performed;

    public LocaleText LocalizedDescription => UndoLocale.PreserveAssets;

    public string Description => LocalizedDescription.Resolve();

    private PreserveAssetsUndoBatch(World world, Slot root, Slot? explicitHolder, Slot graveyard)
    {
        _world = world;
        _root = root;
        _explicitHolder = explicitHolder;
        _graveyard = graveyard;
    }

    // null means the caller should run the plain destroy instead - nothing has been changed at that point
    public static PreserveAssetsUndoBatch? Perform(Slot? root, Slot? relocateAssets = null)
    {
        if (root == null || root.IsDestroyed || root.IsProtected || root.World == null || !root.MayDestroy())
            return null;
        // Acquired up front: without somewhere to park emptied slots there is nothing to undo, and
        // finding that out halfway through the strip would leave a half-reversible mess.
        var graveyard = UndoGraveyard.Acquire(root.World);
        if (graveyard == null)
            return null;

        var batch = new PreserveAssetsUndoBatch(root.World, root, relocateAssets, graveyard);
        return batch.Run() ? batch : null;
    }

    public bool Undo()
    {
        if (!_performed)
            return false;

        // The destination has to be intact before anything moves. A recorded parent that has since
        // been destroyed means this step would rebuild the object into a hierarchy that is gone, so it
        // refuses and the manager drops it.
        foreach (var record in _slots)
        {
            if (record.Slot != null && !record.Slot.IsDestroyed && !UndoTargets.CanRestoreUnder(record.Parent))
                return false;
        }

        bool any = false;

        // Slots first: a component can only be re-attached to a slot that is back where it belongs,
        // and re-parenting in reverse record order puts ancestors back before their descendants.
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            var record = _slots[i];
            if (record.Slot == null || record.Slot.IsDestroyed)
                continue;
            var parent = record.Parent ?? _world.RootSlot;
            if (parent == null)
                continue;
            record.Slot.SetParent(parent);
            record.Slot.LocalPosition.Value = record.Position;
            record.Slot.LocalRotation.Value = record.Rotation;
            record.Slot.LocalScale.Value = record.Scale;
            record.Slot.ActiveSelf.Value = record.Active;
            any = true;
        }

        // Every identity goes first, in its own pass. A component restored early can reference one
        // restored later, and while that later one's GUID still resolves to its DEAD RefID the
        // reference lands on a corpse instead of queueing for the rebuilt element.
        foreach (var record in _components)
            UndoSerialization.Forget(_translator, record.Dead);

        // Reverse order: the strip walked components back-to-front within each slot, so replaying it
        // backwards lands them in roughly their original order.
        var control = new LoadControl(_world, _translator);
        for (int i = _components.Count - 1; i >= 0; i--)
        {
            var record = _components[i];
            if (record.Owner == null || record.Owner.IsDestroyed)
                continue;
            try
            {
                var component = record.Owner.AttachComponent(record.Type, runOnAttachBehavior: false);
                if (component == null)
                    continue;
                component.Load(record.Saved, control);
                record.Dead = new List<RefID>();
                UndoSerialization.CollectIdentities(component, record.Dead);
                any = true;
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"PreserveAssetsUndo: restoring {record.Type.Name} failed: {ex.Message}");
            }
        }
        control.FinishLoad();

        if (_createdHolder != null && !_createdHolder.IsDestroyed
            && _createdHolder.Children.Count == 0 && _createdHolder.Components.Count == 0)
        {
            _createdHolder.Destroy();
        }
        _createdHolder = null;

        _performed = false;
        return any;
    }

    public bool Redo() => !_performed && Run();

    public void OnEvicted()
    {
        // Parked slots are still alive. Once this record leaves the history nothing can bring them
        // back, so the destroy the user asked for finally happens for real.
        if (!_performed)
            return;
        foreach (var record in _slots)
        {
            if (record.Parked && record.Slot != null && !record.Slot.IsDestroyed)
                record.Slot.Destroy();
        }
        _slots.Clear();
        _components.Clear();
        _performed = false;
    }

    public bool ReferencesElement(IWorldElement element)
    {
        if (element is not Slot slot)
            return false;
        if (UndoTargets.Touches(_root, slot))
            return true;
        foreach (var record in _slots)
        {
            if (UndoTargets.Touches(record.Slot, slot) || UndoTargets.Touches(record.Parent, slot))
                return true;
        }
        return false;
    }

    private bool Run()
    {
        _slots.Clear();
        _components.Clear();
        _createdHolder = null;
        _lost = 0;

        _root.DestroyPreservingAssets(_explicitHolder, this);

        if (_slots.Count == 0 && _components.Count == 0)
            return false;

        _performed = true;
        if (_lost > 0)
            Logging.Logger.Warn($"PreserveAssetsUndo: {_lost} component(s) could not be captured and will not come back.");
        return true;
    }

    void ISlotStripRecorder.ComponentStripped(Component component)
    {
        var saved = UndoSerialization.SaveComponent(component, _translator);
        if (saved == null)
        {
            _lost++;
            return;
        }
        var dead = new List<RefID>();
        UndoSerialization.CollectIdentities(component, dead);
        _components.Add(new ComponentRecord
        {
            Owner = component.Slot,
            Type = component.GetType(),
            Saved = saved,
            Dead = dead,
        });
    }

    bool ISlotStripRecorder.SlotEmptied(Slot slot)
    {
        if (_graveyard.IsDestroyed)
            return false;
        Capture(slot, parked: true);
        slot.SetParent(_graveyard);
        slot.ActiveSelf.Value = false;
        return true;
    }

    void ISlotStripRecorder.SlotRelocating(Slot slot, Slot holder) => Capture(slot, parked: false);

    void ISlotStripRecorder.HolderCreated(Slot holder) => _createdHolder = holder;

    private void Capture(Slot slot, bool parked)
    {
        _slots.Add(new SlotRecord
        {
            Slot = slot,
            Parent = slot.Parent,
            Position = slot.LocalPosition.Value,
            Rotation = slot.LocalRotation.Value,
            Scale = slot.LocalScale.Value,
            Active = slot.ActiveSelf.Value,
            Parked = parked,
        });
    }
}

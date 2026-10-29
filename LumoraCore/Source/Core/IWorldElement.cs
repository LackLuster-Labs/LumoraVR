// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Networking.Sync;
using Lumora.Warden;

namespace Lumora.Core;

public interface IWorldElement : IPermissionTarget
{
    World World { get; }

	RefID ReferenceID { get; }

	bool IsInitialized { get; }

    bool IsDestroyed { get; }

	bool IsPersistent { get; }

	string ParentHierarchyToString();

	// PERMISSION GATE VIEW
	// The gate lives below the engine and knows nothing about these types, so every fact it needs is
	// answered here, once, instead of the gate type-switching on engine classes. Keep these as pure
	// reads: the moment one of them starts deciding something, the policy has leaked back into the
	// thing it is supposed to be gating. The switch ORDER is load-bearing - Slot and Component are both
	// Workers, and a Worker inherits its parent's ownership while those two carry their own. -xlinka

	ulong IPermissionTarget.Id => ReferenceID.RawValue;

	string IPermissionTarget.HierarchyPath => ParentHierarchyToString();

	bool IPermissionTarget.IsInitializing
		=> this is SyncElement { IsInInitPhase: true } or SyncElement { IsLoading: true };

	PermissionTargetKind IPermissionTarget.PermissionKind => this switch
	{
		Slot _ => PermissionTargetKind.Slot,
		Component _ => PermissionTargetKind.Component,
		SyncElement _ => PermissionTargetKind.SyncElement,
		Worker _ => PermissionTargetKind.Worker,
		_ => PermissionTargetKind.Other
	};

	IPermissionTarget? IPermissionTarget.OwnershipParent => this switch
	{
		Slot _ => null,
		Component component => component.Slot,
		SyncElement syncElement => syncElement.Parent,
		Worker worker => worker.Parent,
		_ => null
	};

	IPermissionActor? IPermissionTarget.StructuralOwner => this switch
	{
		Slot slot => slot.ActiveUser,
		Component component => component.Slot?.ActiveUser,
		_ => null
	};

	IPermissionTarget? IPermissionTarget.SharedObjectRoot(IPermissionTarget other)
		=> ObjectBoundary.SharedRoot(this, other as IWorldElement);

	bool IPermissionTarget.IsWearerMachinery => ObjectBoundary.IsWearerMachinery(this);

	bool IPermissionTarget.IsUiState => this is SyncElement { IsUiState: true };

	bool IPermissionTarget.IsInside(IPermissionTarget other)
	{
		var slot = ObjectBoundary.SlotOf(this);
		var top = ObjectBoundary.SlotOf(other as IWorldElement);
		return slot != null && top != null && (ReferenceEquals(slot, top) || slot.IsDescendantOf(top));
	}

	bool IPermissionTarget.ChangedHandsRecently => ObjectBoundary.SlotOf(this) is { ChangedHandsRecently: true };

	// The nearest no-copy marker at or above this element. Walks up through the component's slot and then
	// the slot lineage, so marking a container protects everything inside it without every child carrying
	// its own marker. Only ever asked on an explicit save/export, never on a per-write path. -xlinka
	IPermissionCopyProtection? IPermissionTarget.CopyProtection
	{
		get
		{
			// A marker that currently blocks nothing (disabled, or both toggles off) must not shadow an
			// enabled one further up the lineage. -xlinka
			if (this is IPermissionCopyProtection own && (own.BlocksSaveCopy || own.BlocksExport))
				return own;

			// A member's owner is whatever it hangs off; climb until a slot turns up or the chain runs out.
			IWorldElement? node = this;
			for (int depth = 0; node != null && node is not Slot && depth < 8; depth++)
				node = (node as Component)?.Slot ?? (node as SyncElement)?.Parent ?? (node as Worker)?.Parent;

			var slot = node as Slot;
			while (slot != null)
			{
				var marker = slot.GetComponent<Component>(c => c is IPermissionCopyProtection);
				if (marker is IPermissionCopyProtection protection
					&& (protection.BlocksSaveCopy || protection.BlocksExport))
					return protection;
				slot = slot.Parent;
			}

			return null;
		}
	}

	// A user owns everything parented under their own UserRoot - avatar, body nodes, tools, nameplate. This is
	// the STRUCTURAL ownership signal, and the reliable one: a user's allocation byte reads as 0 on their own
	// client until the host-authored AllocationID syncs across, and the cached ActiveUserRoot lags a beat behind
	// the body being built - both of which otherwise (wrongly) deny a user the right to drive their own
	// head/hands/nameplate. The User -> UserRoot link (UserRootRef) is replicated and set on both ends. -xlinka
	bool IPermissionTarget.IsUnderActorRoot(IPermissionActor actor)
	{
		if (actor?.RootElement is not Slot rootSlot)
		{
			return false;
		}

		// A component is under the root when its SLOT is; a component with no slot yet is under nothing.
		var slot = this as Slot ?? (this as Component)?.Slot;
		if (slot == null)
		{
			return false;
		}

		return ReferenceEquals(slot, rootSlot) || slot.IsDescendantOf(rootSlot);
	}

	// Up from this element's slot to the actor's root, looking for a grab the actor is holding: whatever is
	// under a held object came along with it. Only asked of something that already reads as under the
	// actor's root, so the walk ends there. -xlinka
	bool IPermissionTarget.IsCarriedBy(IPermissionActor actor)
	{
		if (actor?.RootElement is not Slot rootSlot)
			return false;

		IWorldElement? node = this;
		for (int depth = 0; node != null && node is not Slot && depth < 8; depth++)
			node = (node as Component)?.Slot ?? (node as SyncElement)?.Parent ?? (node as Worker)?.Parent;

		var slot = node as Slot;
		for (int depth = 0; slot != null && !ReferenceEquals(slot, rootSlot) && depth < 512; depth++)
		{
			if (slot.GrabSurface is { } surface && ReferenceEquals(surface.CurrentHolder, actor))
				return true;
			slot = slot.Parent;
		}
		return false;
	}
}

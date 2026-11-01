// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Fluids;

// Who simulates an element, the rule a rigid body's IsSimulationOwner follows, read from public members only: a local
// element belongs to this machine, anything under a user root to that user's machine, and the rest of the world to
// the authority. The keeper is a different question (who bins the ledger); this one says who may write a body's
// impulse, a container's frame or a person's record. -xlinka
public static class FluidOwnership
{
    public static bool IsSimulatedHere(Slot? slot)
    {
        if (slot == null || slot.IsDestroyed)
            return false;
        var world = slot.World;
        if (world == null)
            return false;
        if (slot.IsLocalElement)
            return true;
        // Structural rather than the allocation byte, which lags behind at join.
        if (slot.ActiveUserRoot != null)
            return slot.IsUnderLocalUser;
        return world.IsAuthority;
    }

    public static bool IsSimulatedHere(Component? component) => component != null && !component.IsDestroyed && IsSimulatedHere(component.Slot);

    // The user whose machine simulates the slot; null for world content, which the authority simulates.
    public static User? OwnerOf(Slot? slot) => slot?.ActiveUserRoot?.ActiveUser;

    // The user who minted an element: the user byte of its RefID. A FluidPerson is trusted only when this is the
    // wearer, since an editor with consent could otherwise attach a forged one to someone else's root.
    public static bool MintedBy(IWorldElement? element, User? user)
        => element != null && user != null && element.ReferenceID.GetUserByte() == user.AllocationID.Value;
}

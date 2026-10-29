// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Warden;

// How a world session behaves with respect to editing. Chosen at host time from the world's allowed
// modes and baked into the session - it is NOT a live toggle, and the lock is enforced
// host-authoritatively at the datamodel gate (so no client, and not the host, can bypass it).
public enum WorldMode
{
    // Full in-world editing: spawn, build, manipulate, dev tools and inspectors. The default
    // creation world.
    Builder,

    // Social space: the authored world is frozen for EVERYONE (including the host) - no building,
    // no dev tools, no inspectors. Its props can still be picked up and its hinges turned. Users keep
    // their own avatar and may bring their own items.
    Social,

    // Like Social but stricter: users can't spawn items and their scripts don't run. They still wear their own
    // avatars, or the world's.
    Event
}

// The per-mode policy data. This is the single definition of the Social/Event lock floor: read it
// here rather than re-deriving "mode != Builder" at each call site, or the floor drifts between the
// gate and whatever consults it (locomotion, tool availability, UI). -xlinka
public static class WorldModePolicy
{
    // Whether the mode freezes the authored world for EVERYONE, host included. The engine applies
    // this as PermissionEngine.SocialLock; nothing else may loosen it.
    public static bool SocialLockFloor(WorldMode mode) => mode switch
    {
        WorldMode.Social => true,
        WorldMode.Event => true,
        _ => false
    };

    // Whether users may still bring and handle their own items in this mode.
    public static bool AllowsOwnItems(WorldMode mode) => mode != WorldMode.Event;

    // The CEILING a mode puts on a capability domain, binding every role including the host. This is the
    // same floor SocialLockFloor expresses, said per domain: config toggles resolve first and this is
    // applied once afterwards, so a host toggle can restrict below it and can never raise past it. Adding
    // a domain means answering it here, or the mode stops constraining it. -xlinka
    public static bool ModeAllows(WorldMode mode, PermissionDomain domain) => domain switch
    {
        // Bringing your own items. Frozen worlds still allow it; an event does not.
        PermissionDomain.Spawn => AllowsOwnItems(mode),

        // Build tools, inspectors, dev gear. The whole point of a non-Builder world is that these are
        // gone, and gone for the host too.
        PermissionDomain.ToolUse => mode == WorldMode.Builder,

        // Interacting with what is already there is what a social world IS.
        PermissionDomain.Touch => true,

        // A user's script rides on a user's object, so it goes wherever their own items may go. An event lets
        // people wear their avatars but runs none of their scripts, the world's own scripts aside.
        PermissionDomain.Script => AllowsOwnItems(mode),

        // Taking a copy is not editing, so the freeze does not reach it. Ownership, the per-role toggles
        // and the protection marker are what gate these.
        PermissionDomain.SaveCopy => true,
        PermissionDomain.Export => true,

        _ => false
    };
}

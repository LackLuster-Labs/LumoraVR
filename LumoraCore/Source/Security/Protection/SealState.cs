// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Security;

// What this machine knows of a seal. Each machine checks for itself; nothing here is synced.
public enum SealState
{
    // Attached and still waiting for the accounts service to sign it, or never signed: it blocks nobody and the
    // authority never acts over it.
    Unsigned,
    Checking,
    Verified,
    // The signature does not check out against the owner's keys, or the seal is malformed.
    Broken,
}

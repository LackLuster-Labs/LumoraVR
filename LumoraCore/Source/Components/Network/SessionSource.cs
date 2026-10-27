// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Network;

// lets a user tell a machine on their own network from one on the internet, and lets join failures
// land where they belong
public enum SessionSource
{
    // or hosted by us
    Local,

    Internet
}

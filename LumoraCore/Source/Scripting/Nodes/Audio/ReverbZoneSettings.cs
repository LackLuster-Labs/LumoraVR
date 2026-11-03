// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A reverb zone's settings as one value, as its fields hold them. All zeros for one the script cannot see.
[Node("Audio/Reverb/Reverb Zone Settings", Capability.WorldRead, Description = "A reverb zone's settings as one value, as its fields hold them.")]
public sealed class ReverbZoneSettings : ReadNode
{
    [return: Port("settings")]
    public static ReverbSettings Run(ScriptContext ctx, ReverbZone? zone)
        => ScriptReads.Visible(ctx, zone) ? zone!.Settings : default;
}

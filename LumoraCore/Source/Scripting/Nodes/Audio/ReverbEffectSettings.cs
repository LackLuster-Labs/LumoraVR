// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A reverb effect's settings as one value, as its fields hold them. All zeros for one the script cannot see.
[Node("Audio/Reverb/Reverb Effect Settings", Capability.WorldRead, Description = "A reverb effect's settings as one value, as its fields hold them.")]
public sealed class ReverbEffectSettings : ReadNode
{
    [return: Port("settings")]
    public static ReverbSettings Run(ScriptContext ctx, ReverbEffect? effect)
        => ScriptReads.Visible(ctx, effect) ? effect!.Settings : default;
}

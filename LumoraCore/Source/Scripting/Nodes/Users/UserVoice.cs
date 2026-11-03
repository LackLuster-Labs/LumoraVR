// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// How a user is heard. Mode is the voice mode they chose, held to what the world's voice rules allow, and
// Mute while they are muted or silenced; a listener's own override on their machine is left out. Muted is
// their own mute or the host's; silenced is the host's silence, which only the host can lift. Mute and
// false for a user the script cannot see.
[Node("Users/Status/Voice", Capability.UsersRead, Description = "How a user is heard.",
    Keywords = "muted, mute, whisper, shout, broadcast, silenced")]
public sealed class UserVoice : ReadNode
{
    [return: Port("mode")]
    public static VoiceMode Run(ScriptContext ctx, User? user, out bool muted, out bool silenced)
    {
        muted = silenced = false;
        if (!ScriptReads.Visible(ctx, user))
            return VoiceMode.Mute;
        muted = user!.IsMuted.Value;
        silenced = user.IsSilenced.Value;
        if (muted || silenced)
            return VoiceMode.Mute;
        var mode = user.voiceMode.Value;
        var cap = user.MaxAllowedVoiceMode;
        return mode > cap ? cap : mode;
    }
}

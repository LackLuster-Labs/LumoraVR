// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Core.Assets;
using Lumora.Core.Audio;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Plays a clip the script is bound to once at a point, then the sound removes itself. Everyone hears it
// from a Shared script, only this machine from a Local one. A user's script needs the Spawn permission,
// since the sound is a short-lived object in the world.
//
// Volume runs 0 to 1. Speed moves the pitch with it. Spatial blend runs from 0, heard the same everywhere,
// to 1, placed in the room. Doppler is 0 for none and 1 as physics has it. It plays at full volume within
// min distance and fades to silence at max distance. When too many sounds play at once the higher
// priorities keep playing; 128 is every sound's default.
//
// The second form takes every setting a sound has: how it fades with distance, the volume group it counts
// under, whether its distances grow and shrink with the object (local) or stay in world metres (global),
// the scale they are held between (a max of 0 is no limit), whether it skips the listener's effects,
// whether it is heard the same everywhere, and a parent it follows, with point in the parent's space. A
// Shared script's parent is world content outside anyone's body; a Local one's is its own object or what it
// made there.
[Node("Media/Play One Shot", Capability.Audio, Description = "Plays a clip the script is bound to once at a point, then the sound removes itself.",
    Keywords = "sound effect, sfx, play sound, one shot, play clip")]
public sealed class PlayOneShot : ActionNode
{
    [return: Port("sound")]
    public static Slot? Run(ScriptContext ctx, IAssetProvider<AudioClip>? clip, float3 point, float volume = 1f, float speed = 1f,
        float spatialBlend = 1f, float doppler = 0f, float minDistance = 1f, float maxDistance = 25f, int priority = 128)
        => ScriptSounds.PlayOnce(ctx, clip, point,
            new ScriptSounds.Sound(volume, speed, spatialBlend, doppler, minDistance, maxDistance, priority));

    [return: Port("sound")]
    public static Slot? Run(ScriptContext ctx, IAssetProvider<AudioClip>? clip, float3 point, float volume, float speed,
        float spatialBlend, float doppler, float minDistance, float maxDistance, int priority,
        AudioRolloffCurve rolloff = AudioRolloffCurve.LogarithmicFadeOff, AudioTypeGroup group = AudioTypeGroup.SoundEffect,
        AudioDistanceSpace distanceSpace = AudioDistanceSpace.Local, float minScale = 0f, float maxScale = 0f,
        bool ignoreEffects = false, bool global = false, Slot? parent = null)
        => ScriptSounds.PlayOnce(ctx, clip, point,
            new ScriptSounds.Sound(volume, speed, spatialBlend, doppler, minDistance, maxDistance, priority, rolloff, group,
                distanceSpace, minScale, maxScale, ignoreEffects, global), parent);
}

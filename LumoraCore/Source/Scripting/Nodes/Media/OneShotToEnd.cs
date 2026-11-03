// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Components.Audio;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Plays a clip once at a point the way Play One Shot does and carries on through next at once. ended runs
// once the clip has played through; failed when it does not load, when the sound goes before the clip has
// played through (a one-shot lasts OneShotSound.MaxSeconds at most), or at once when the script cannot see
// the clip, with error saying why. Watched on the machine the script runs on.
[Node("Media/Play One Shot To End", Capability.Audio, Description = "Plays an audio clip once at a position and answers ended once it has played through, or failed when it does not load or is cut off first.",
    Keywords = "wait, await, finished, ended, on end, done, sound effect")]
[Answers("ended", "failed")]
public sealed class OneShotToEnd : AwaitNode<SoundEnded>
{
    public static void Start(ScriptContext ctx, AwaitTicket<SoundEnded> ticket, IAssetProvider<AudioClip>? clip, float3 point,
        float volume = 1f, float speed = 1f, float spatialBlend = 1f, float doppler = 0f, float minDistance = 1f,
        float maxDistance = 25f, int priority = 128)
    {
        Slot? sound;
        try
        {
            sound = ScriptSounds.PlayOnce(ctx, clip, point,
                new ScriptSounds.Sound(volume, speed, spatialBlend, doppler, minDistance, maxDistance, priority));
        }
        catch (System.InvalidOperationException ex)
        {
            ticket.Answer("failed", new SoundEnded(ex.Message));
            return;
        }
        catch
        {
            ticket.Abandon();
            throw;
        }
        if (sound == null)
        {
            ticket.Answer("failed", new SoundEnded("the script cannot see that clip"));
            return;
        }

        var player = sound.GetComponent<AudioClipPlayer>();
        string? error = null;
        // When the clip would be through at the speed it plays, for a sound taken away in the same long frame it
        // finished in, before a look saw it finished.
        double through = double.PositiveInfinity;
        ScriptWaits.Watch(ctx, ticket, () =>
        {
            double now = ctx.World.Time.TotalTime;
            if (player == null || player.IsDestroyed || sound.IsDestroyed)
            {
                if (error == null && now >= through)
                    return ("ended", new SoundEnded(string.Empty));
                return ("failed", new SoundEnded(error ?? "the sound stopped before its clip played through"));
            }
            if (player.LoadError is { } why)
                error = "the clip does not load: " + why;
            if (player.IsFinished)
                return ("ended", new SoundEnded(string.Empty));
            if (player.IsPlaying && player.ClipLength > 0.0 && speed > 0f)
                through = now + System.Math.Max(0.0, player.ClipLength - player.Position) / speed - 0.05;
            return null;
        });
    }
}

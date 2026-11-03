// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A stretch of a clip's samples, count of them from start seconds in, and the rate they play at: one channel
// (0 first), or with channel -1 every channel averaged. What runs past the end is left out, as is a channel the
// clip does not have. length is the whole clip in seconds. Empty, at rate 0, while the clip loads or for one the
// script cannot see. Up to 65536 samples at once.
[Node("Assets/Audio/Clip Samples", Capability.WorldRead, Description = "A stretch of a clip's samples, count of them from start seconds in, and the rate they play at: one channel (0 first), or with channel -1 every channel averaged.",
    Keywords = "audio data, pcm, read audio clip, samples of clip")]
public sealed class ClipSamples : ReadNode
{
    [return: Port("samples")]
    public static ScriptList<float> Run(ScriptContext ctx, IAssetProvider<AudioClip>? clip, out int rate, out float length,
        float start = 0f, int count = 1024, int channel = -1)
    {
        rate = 0;
        length = 0f;
        var samples = new ScriptList<float>(ctx.Collections);
        var data = clip is Component provider && ScriptReads.Visible(ctx, provider) ? clip.Asset?.Data : null;
        if (data == null)
            return samples;
        rate = data.SampleRate;
        length = (float)data.Duration;
        int channels = data.Channels;
        if (count <= 0 || !float.IsFinite(start) || channel >= channels)
            return samples;

        int frames = data.FrameCount;
        int first = (int)System.Math.Clamp(System.Math.Floor((double)start * rate), 0, frames);
        int n = System.Math.Min(System.Math.Min(count, ScriptAudioAnalysis.MaxSamples), frames - first);
        var pcm = data.Samples;
        for (int i = 0; i < n; i++)
        {
            int at = (first + i) * channels;
            if (channel >= 0)
            {
                samples.Add(pcm[at + channel]);
                continue;
            }
            float sum = 0f;
            for (int c = 0; c < channels; c++)
                sum += pcm[at + c];
            samples.Add(sum / channels);
        }
        return samples;
    }
}

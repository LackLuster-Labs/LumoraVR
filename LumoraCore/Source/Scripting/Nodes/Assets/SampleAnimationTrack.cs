// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Assets.Animation;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Track number track of an animation at time seconds, as T: before its first key it holds the first
// value, past its last the last. ok is false, and the value T's default, when the animation has not
// loaded here, there is no such track, or the track holds something other than T.
[Node("Assets/Animations/Sample Animation Track", Capability.WorldRead, Description = "Reads an animation track at a time in seconds, using the selected value type.",
    Keywords = "keyframes, read animation, evaluate animation")]
public sealed class SampleAnimationTrack<T> : ReadNode
{
    [return: Port("value")]
    public static T Run(ScriptContext ctx, IAssetProvider<AnimationAsset>? animation, int track, float time, out bool ok)
    {
        ok = false;
        var clip = ScriptAssets.Clip(ctx, animation);
        if (clip == null || (uint)track >= (uint)clip.TrackCount || !float.IsFinite(time)
            || clip[track] is not IAnimationTrack<T> typed || !typed.Evaluate(time, out var value))
            return default!;
        ok = true;
        return value;
    }
}

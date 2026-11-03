// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Which track of an animation moves property on the node called node, 0 first, or -1 when none does or
// the animation has not loaded here. Names are exact and case sensitive, as the clip keeps them: a bone
// or slot name, and a member such as Position, or Component.Member for one on a component.
[Node("Assets/Animations/Find Animation Track", Capability.WorldRead, Description = "Which track of an animation moves property on the node called node, 0 first, or -1 when none does or the animation has not loaded here.")]
public sealed class FindAnimationTrack : ReadNode
{
    [return: Port("track")]
    public static int Run(ScriptContext ctx, IAssetProvider<AnimationAsset>? animation, string? node, string? property)
    {
        var clip = ScriptAssets.Clip(ctx, animation);
        if (clip == null)
            return -1;
        node ??= string.Empty;
        property ??= string.Empty;
        var tracks = clip.Tracks;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Node == node && tracks[i].Property == property)
                return i;
        }
        return -1;
    }
}

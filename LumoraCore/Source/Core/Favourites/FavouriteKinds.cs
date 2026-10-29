// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core;

public static class FavouriteKinds
{
    public static readonly FavouriteKind[] All = (FavouriteKind[])Enum.GetValues(typeof(FavouriteKind));

    // Lower case, for the middle of a sentence.
    public static string Noun(this FavouriteKind kind) => kind switch
    {
        FavouriteKind.Avatar => "avatar",
        FavouriteKind.Home => "home",
        FavouriteKind.VideoPlayer => "video player",
        FavouriteKind.AudioPlayer => "audio player",
        FavouriteKind.Portal => "portal",
        FavouriteKind.WorldOrb => "world orb",
        FavouriteKind.Camera => "camera",
        FavouriteKind.Mirror => "mirror",
        FavouriteKind.Inspector => "inspector",
        FavouriteKind.Keyboard => "keyboard",
        _ => kind.ToString(),
    };

    public static string Title(this FavouriteKind kind)
    {
        var noun = kind.Noun();
        return char.ToUpperInvariant(noun[0]) + noun.Substring(1);
    }

    // The component a saved object has to carry to stand in for one made from a menu. Avatars and homes
    // are told apart by the inventory itself.
    public static Type? Component(this FavouriteKind kind) => kind switch
    {
        FavouriteKind.VideoPlayer => typeof(Components.Media.VideoPlayer),
        FavouriteKind.AudioPlayer => typeof(Components.Media.AudioPlayer),
        FavouriteKind.Portal => typeof(Components.WorldPortal),
        FavouriteKind.WorldOrb => typeof(Components.WorldOrb),
        FavouriteKind.Camera => typeof(Components.PhotoCamera),
        FavouriteKind.Mirror => typeof(Components.Mirror),
        FavouriteKind.Inspector => typeof(Components.SceneInspectorPanel),
        FavouriteKind.Keyboard => typeof(Components.UI.KeyboardDesign),
        _ => null,
    };

    // The component has to sit on the object itself, not somewhere inside it: an inspector or a keyboard is
    // opened as the whole object, so a room with one standing in a corner is not one.
    public static bool OnRoot(this FavouriteKind kind) => kind is FavouriteKind.Inspector or FavouriteKind.Keyboard;
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// A package picture's upload settings, onto the picture's own: whether it has mips, its size cap, and whether it
// may be block compressed (asked off directly, or by naming a raw format). Readable needs nothing: a picture here
// gives its pixels back whenever asked, decoding them again once the GPU has its copy.
//
// The filter and the wrap modes ride on the picture and the material builds its slots to match (point and
// anisotropic filtering, clamping; mirroring repeats). A package picture always asks for its wrap, Repeat
// included, so a slot made to clamp repeats what the package repeats. Left out: the anisotropic level, which is
// one setting for the whole renderer here, and the mip bias, which no slot carries. So are the package's own
// transport choices (crunch, direct load, an exact variant, its mip filter), which describe how it stored the
// file rather than what the picture should look like. -xlinka
public static partial class PackageMemberMapper
{
    private static void ApplyPicture(ImageProvider picture, DataTreeDictionary data)
    {
        if (StoredFlag(data, "MipMaps") is bool mips)
            picture.GenerateMipmaps.Value = mips;

        // No cap is stored as nothing; a cap snaps to our size buckets when the picture loads.
        if (StoredPicture(data, "MaxSize") is IConvertible cap and not string and not bool)
        {
            int size = Convert.ToInt32(cap, CultureInfo.InvariantCulture);
            if (size > 0)
                picture.MaxSizeOverride.Value = size;
        }

        bool uncompressed = StoredFlag(data, "Uncompressed") == true
            || StoredPicture(data, "PreferredFormat") is string format && format.StartsWith("Raw", StringComparison.Ordinal);
        if (uncompressed)
            picture.AllowCompression.Value = false;

        // No filter stored means the package's default, which is what our slots already do.
        picture.Filter.Value = StoredPicture(data, "FilterMode") switch
        {
            "Point" => TextureFilter.Point,
            "Anisotropic" => TextureFilter.Anisotropic,
            "Bilinear" or "Trilinear" => TextureFilter.Linear,
            _ => TextureFilter.Slot,
        };
        picture.UseWrapModes.Value = true;
    }

    private static object? StoredPicture(DataTreeDictionary data, string member) =>
        ((data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data") as DataTreeValue)?.Value;

    private static bool? StoredFlag(DataTreeDictionary data, string member) => StoredPicture(data, member) switch
    {
        bool flag => flag,
        string text when bool.TryParse(text, out bool parsed) => parsed,
        _ => null,
    };
}

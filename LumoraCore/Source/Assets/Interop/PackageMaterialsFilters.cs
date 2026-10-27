// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// SCREEN FILTERS, THE DEPTH PROJECTION AND TWO PROPERTY BLOCKS. Gamma, Grayscale, Invert, Pixelate, Posterize
// and Depth, DepthProjectionMaterial, MainAndMaskTexturePropertyBlock and Projection360PropertyBlock all go by
// the package's own type names, and the filters keep its member names, rendering and canvas state included,
// so they land by name. Sidedness inverts into Culling through the global alias. -xlinka
public static partial class PackageTypeMap
{
    static void AliasesMatsFilters(Dictionary<string, string> aliases)
    {
    }
}

public static partial class PackageMemberMapper
{
    // A stored member with this name never lands by name; ApplyMatsFilters takes it where it has a use.
    private const string HandledMatsFilters = "\u0000matsf";

    private static readonly string[] ScreenFiltersMatsFilters =
        { "GammaMaterial", "GrayscaleMaterial", "InvertMaterial", "PixelateMaterial", "PosterizeMaterial", "DepthMaterial" };

    static void MemberAliasesMatsFilters(Dictionary<string, Dictionary<string, string>> table)
    {
        // A clip rect is stored as a record of its corner and size, which the by-name copy cannot read.
        foreach (var filter in ScreenFiltersMatsFilters)
            MergeMatsFilters(table, filter, ("Rect", HandledMatsFilters));
        // The package picks between a grab shared by every pixelate and one per object. Here the screen is
        // copied once per frame either way, so the choice shows nothing.
        MergeMatsFilters(table, "PixelateMaterial", ("PerObject", HandledMatsFilters));
        // The package names the two textures by what they hold; ours say they are textures.
        MergeMatsFilters(table, "DepthProjectionMaterial", ("Color", "ColorTexture"), ("Depth", "DepthTexture"));
    }

    private static void MergeMatsFilters(Dictionary<string, Dictionary<string, string>> table, string ours, params (string Stored, string To)[] pairs)
    {
        if (!table.TryGetValue(ours, out var members))
            table[ours] = members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (stored, to) in pairs)
            members.TryAdd(stored, to);
    }

    static void ApplyMatsFilters(Component component, DataTreeDictionary data)
    {
        var rect = component switch
        {
            GammaMaterial gamma => gamma.Rect,
            GrayscaleMaterial grayscale => grayscale.Rect,
            InvertMaterial invert => invert.Rect,
            PixelateMaterial pixelate => pixelate.Rect,
            PosterizeMaterial posterize => posterize.Rect,
            DepthMaterial depth => depth.Rect,
            _ => null,
        };
        if (rect != null && RectMatsFilters(data) is { } stored)
            rect.Value = stored;
    }

    // Corner and size in the canvas's own x and y, y up, the corner the minimum: the same layout as ours, and
    // canvas space is not mirrored on the way in the way mesh space is.
    private static Rect? RectMatsFilters(DataTreeDictionary data)
    {
        if ((data.TryGetNode("Rect") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeDictionary record)
            return null;
        float? Field(string name)
        {
            var node = record.TryGetNode(name);
            if (node is DataTreeDictionary holder && holder.TryGetNode("Data") is { } inner)
                node = inner;
            return node is DataTreeValue { Value: IConvertible value } && value is not string and not bool
                ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : null;
        }
        if (Field("X") is not { } x || Field("Y") is not { } y || Field("Width") is not { } width || Field("Height") is not { } height)
            return null;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(height))
            return null;
        return new Rect(x, y, width, height);
    }
}

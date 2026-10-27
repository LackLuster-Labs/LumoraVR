// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// SPECIAL MATERIALS: the atmosphere sky, the unlit distance lerp, the volume texture, the mesh and texture
// debug views, the UV rect, the UI circle segment and the crystal cells, and the package's old flat toon type,
// which its own loader turns into the toon we call XiexeToonMaterial. -xlinka
public static partial class PackageTypeMap
{
    static void AliasesMatsSpecial(Dictionary<string, string> aliases)
    {
        aliases["ProceduralSkyMaterial"] = "AtmosphereSkyMaterial";
        // The volume material's older name is still what older packages store.
        aliases["VolumeUnlitMaterial"] = "VolumeTextureMaterial";
        aliases["VolumeAdditiveMaterial"] = "VolumeTextureMaterial";
        aliases["DebugMaterial"] = "MeshDebugMaterial";
        aliases["TextureDebugMaterial"] = "TextureChannelMaterial";
        aliases["UV_RectMaterial"] = "UVRectMaterial";
        aliases["UI_CircleSegment"] = "UICircleSegmentMaterial";
        // The package retired its flat toon by rebuilding every one as its Xiexe toon on load, same-named members
        // carried over and the alpha cutoff renamed; this does the same. Our FlatToonMaterial is a different
        // model (no emission, a stepped ramp of its own), so landing there would drop half the material.
        aliases["FlatLitToonMaterial"] = "XiexeToonMaterial";

        // The old circle-segment material draws only the vertex data of the old segment mesh, which has no
        // counterpart here and which the package's own world orb destroys, material and all, as it loads. With
        // nothing to draw it is a strip, not a loss. Named here so it does not hinge on the particle shims' prefix
        // rule it also happens to match. BuiltInMaterial is its older name.
        Carried.Add("LegacyCircleSegmentMaterial");
        Carried.Add("BuiltInMaterial");
    }
}

public static partial class PackageMemberMapper
{
    // A stored member with this name never lands; ApplyMatsSpecial takes it instead, or it did nothing in the
    // package to begin with. ApplyMatsSpecial writes only members routed here, so it does not care whether it runs
    // before or after the stored members are assigned.
    private const string HandledSpecial = "\u0000matss";

    static void MemberAliasesMatsSpecial(Dictionary<string, Dictionary<string, string>> table)
    {
        MergeSpecial(table, "VolumeTextureMaterial", ("Mode", "DisplayMode"), ("Exp", "Exponent"));

        // The package's distance lerp passes the vertex colour along and never multiplies it in, so both of its
        // vertex colour members do nothing.
        MergeSpecial(table, "UnlitDistanceLerpMaterial", ("UseVertexColors", HandledSpecial),
            ("VertexColorInterpolationSpace", HandledSpecial));

        // A colour offset, which the position rule would mirror for its name; read as stored.
        MergeSpecial(table, "MeshDebugMaterial", ("Offset", HandledSpecial));

        // Both rects are UV rects and turn over with V. The stored ZTest never reached the package's shader, which
        // always tests depth the normal way.
        MergeSpecial(table, "UVRectMaterial", ("Rect", HandledSpecial), ("ClipRect", HandledSpecial),
            ("ZTest", HandledSpecial));

        MergeSpecial(table, "UICircleSegmentMaterial", ("Rect", HandledSpecial));

        // The flat toon's cutoff is the toon's AlphaClip. Its shadow strength, outline tint and colour mask tiling
        // have no member on the toon its loader builds and go with the rebuild. The outline is read whole in
        // ApplyMatsSpecial, for the flat toon and the toon proper alike.
        MergeSpecial(table, "XiexeToonMaterial", ("AlphaCutoff", "AlphaClip"), ("Shadow", HandledSpecial),
            ("OutlineTint", HandledSpecial), ("ColorMaskScale", HandledSpecial), ("ColorMaskOffset", HandledSpecial),
            ("Outline", HandledSpecial), ("OutlineWidth", HandledSpecial));
    }

    private static void MergeSpecial(Dictionary<string, Dictionary<string, string>> table, string ours,
        params (string Stored, string To)[] pairs)
    {
        if (!table.TryGetValue(ours, out var members))
            table[ours] = members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (stored, to) in pairs)
            members.TryAdd(stored, to);
    }

    static void ApplyMatsSpecial(Component component, DataTreeDictionary data)
    {
        switch (component)
        {
            case MeshDebugMaterial debug:
                if (ReadNumberArray(data, "Offset") is { Length: >= 3 } offset)
                    debug.Offset.Value = new float3(offset[0], offset[1], offset[2]);
                break;

            case UVRectMaterial uvRect:
                if (RectSpecial(StoredSpecial(data, "Rect")) is { } rect)
                    uvRect.Rect.Value = FlipVSpecial(rect);
                // An unset clip is stored as a null value; any stored rect switches the clip on.
                if (data.TryGetNode("ClipRect") != null)
                {
                    var clip = RectSpecial(StoredSpecial(data, "ClipRect"));
                    uvRect.UseClipRect.Value = clip.HasValue;
                    if (clip is { } clipRect)
                        uvRect.ClipRect.Value = FlipVSpecial(clipRect);
                }
                break;

            case UICircleSegmentMaterial segment:
                // A clip in the mesh's own XY, not a UV rect: kept as stored.
                if (RectSpecial(StoredSpecial(data, "Rect")) is { } segmentRect)
                    segment.Rect.Value = segmentRect;
                break;

            case XiexeToonMaterial toon:
                ApplyToonOutlineSpecial(toon, data);
                break;
        }
    }

    // The package's toon draws an outline only with an outline style set, and pushes it out by a hundredth of the
    // width in the mesh's own units; ours pushes by the width itself, in model-space metres. Its default width of
    // 1 under the default style of None drew nothing there and came in here as the toon's five millimetre ceiling
    // on every toon. The flat toon's styles are named differently, and the rebuild into a toon never carried them
    // over, so a flat toon arrives without an outline whatever it had. Lit shades the hull by the lights, Emissive
    // draws it flat; the style lands on Outline, None included, so a stored width can never draw a hull the
    // package did not.
    private static void ApplyToonOutlineSpecial(XiexeToonMaterial toon, DataTreeDictionary data)
    {
        if (data.TryGetNode("OutlineWidth") == null && data.TryGetNode("Outline") == null)
            return;
        var style = StoredSpecial(data, "Outline") is DataTreeValue { Value: string name } ? name : null;
        toon.Outline.Value = style switch
        {
            _ when "Lit".Equals(style, StringComparison.OrdinalIgnoreCase) => ToonOutlineStyle.Lit,
            _ when "Emissive".Equals(style, StringComparison.OrdinalIgnoreCase) => ToonOutlineStyle.Emissive,
            _ => ToonOutlineStyle.None,
        };
        bool drawn = toon.Outline.Value != ToonOutlineStyle.None;
        float width = StoredSpecial(data, "OutlineWidth") is DataTreeValue { Value: IConvertible stored } && stored is not string and not bool
            ? Convert.ToSingle(stored, CultureInfo.InvariantCulture) : 1f;
        toon.OutlineWidth.Value = drawn && float.IsFinite(width) ? System.Math.Max(width, 0f) * 0.01f : 0f;
    }

    // The package counts V up from the bottom, ours down from the top, as the meshes come in mirrored.
    private static Rect FlipVSpecial(in Rect rect) => new Rect(rect.x, 1f - rect.y - rect.height, rect.width, rect.height);

    // A stored rect is a record of its corner and size.
    private static Rect? RectSpecial(DataTreeNode? node)
    {
        if (node is not DataTreeDictionary rect)
            return null;
        if (FieldSpecial(rect, "X") is not { } x || FieldSpecial(rect, "Y") is not { } y
            || FieldSpecial(rect, "Width") is not { } width || FieldSpecial(rect, "Height") is not { } height)
            return null;
        return new Rect(x, y, width, height);
    }

    private static float? FieldSpecial(DataTreeDictionary record, string member)
        => StoredSpecial(record, member) is DataTreeValue { Value: IConvertible value } && value is not string and not bool
            && Convert.ToSingle(value, CultureInfo.InvariantCulture) is var number && float.IsFinite(number)
            ? number : null;

    // A member's stored value, under its holder's Data or bare.
    private static DataTreeNode? StoredSpecial(DataTreeDictionary data, string member)
    {
        var node = data.TryGetNode(member);
        return node is DataTreeDictionary holder && holder.TryGetNode("Data") is { } inner ? inner : node;
    }
}

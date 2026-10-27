// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// THE MATERIALS THAT WERE ALREADY OURS: the Xiexe toon, both overlays, the fresnel lerp, the wireframe, the blur,
// the matcap and the four colour filters. They resolve by name and most of what a package stores now lands on a
// member of the same name; what is here is what does not line up one to one. A face to draw becomes a face to
// cull, with Auto meaning both faces on a blended surface. Units that differ are converted. Canvas state with
// no effect in a world is consumed, and said so in the log when its stored value would have done something.
// -xlinka
public static partial class PackageTypeMap
{
    // Every one of these types goes by the same name here.
    static void AliasesMatsExisting(Dictionary<string, string> aliases)
    {
    }
}

public static partial class PackageMemberMapper
{
    // A stored member with this name never lands; ApplyMatsExisting takes it instead.
    private const string HandledMatsExisting = "\u0000matse";

    static void MemberAliasesMatsExisting(Dictionary<string, Dictionary<string, string>> table)
    {
        // The global alias sends UseVertexColors to UseVertexColor; this material keeps the plural. UV sets are
        // stored as indices from 0, ours are named sets. Saturation there is a factor with 1 neutral, here an
        // amount with 0 neutral. The outline width and style are read whole by the special materials
        // (ApplyToonOutlineSpecial).
        MergeMatsExisting(table, "XiexeToonMaterial", ("UseVertexColors", "UseVertexColors"),
            ("AlbedoUV", HandledMatsExisting), ("NormalUV", HandledMatsExisting), ("MetallicUV", HandledMatsExisting),
            ("OcclusionUV", HandledMatsExisting), ("EmissionUV", HandledMatsExisting), ("ThicknessUV", HandledMatsExisting),
            ("Saturation", HandledMatsExisting), ("ColorMask", HandledMatsExisting), ("__legacyCutout", HandledMatsExisting));

        // The front state's texture is ours under its usual name.
        MergeMatsExisting(table, "OverlayUnlitMaterial", ("FrontTexture", "Texture"), ("Sidedness", HandledMatsExisting));
        MergeMatsExisting(table, "OverlayFresnelMaterial", ("Sidedness", HandledMatsExisting));
        // A lerp texture there replaces the scalar where ours multiplies it.
        MergeMatsExisting(table, "FresnelLerpMaterial", ("Sidedness", HandledMatsExisting), ("Lerp", HandledMatsExisting));

        // The wireframe's thickness is pixels or metres by ScreenSpace, and two-sided means culling plus the inner
        // colours here. Its one texture multiplies lines and fill alike.
        MergeMatsExisting(table, "WireframeMaterial", ("Exp", "FresnelExponent"), ("Texture", "FillTexture"),
            ("Thickness", HandledMatsExisting), ("DoubleSided", HandledMatsExisting));

        // One shared copy of the screen per frame either way here.
        MergeMatsExisting(table, "BlurMaterial", ("Sidedness", HandledMatsExisting), ("PerObject", HandledMatsExisting),
            ("ColorMask", HandledMatsExisting));

        foreach (var filter in new[] { "HSVMaterial", "LUTMaterial", "ThresholdMaterial", "ChannelMatrixMaterial" })
            MergeMatsExisting(table, filter, ("Sidedness", HandledMatsExisting));

        // Placeholders the package keeps for canvas state its stencil surface never uses.
        MergeMatsExisting(table, "PBS_StencilMaterialSpecular", ("__rectClipDummy", HandledMatsExisting),
            ("__rectDummy", HandledMatsExisting));
    }

    private static void MergeMatsExisting(Dictionary<string, Dictionary<string, string>> table, string ours, params (string Stored, string To)[] pairs)
    {
        if (!table.TryGetValue(ours, out var members))
            table[ours] = members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (stored, to) in pairs)
            members.TryAdd(stored, to);
    }

    static void ApplyMatsExisting(Component component, DataTreeDictionary data)
    {
        switch (component)
        {
            case XiexeToonMaterial toon:
                ApplyXiexeExisting(toon, data);
                break;
            case OverlayUnlitMaterial overlay:
                overlay.Culling.Value = SidednessExisting(data, overlay.Culling.Value);
                if (StoredExisting(data, "FrontTexture") is string && StoredExisting(data, "BehindTexture") is null)
                    Logging.Logger.Warn($"Package import: OverlayUnlitMaterial on '{component.Slot?.Name}' has a front texture and none behind; the behind state reads the front one here.");
                break;
            case OverlayFresnelMaterial fresnel:
                fresnel.Culling.Value = SidednessExisting(data, fresnel.Culling.Value);
                break;
            case FresnelLerpMaterial lerp:
                lerp.Culling.Value = SidednessExisting(data, lerp.Culling.Value);
                if (StoredExisting(data, "LerpTexture") is string)
                    lerp.Lerp.Value = 1f;
                else if (FloatExisting(data, "Lerp") is float amount)
                    lerp.Lerp.Value = amount;
                break;
            case WireframeMaterial wireframe:
                ApplyWireframeExisting(wireframe, data);
                break;
            case BlurMaterial blur:
                // The package's blur is the scatter, plain: no wash and no fade of its own.
                blur.Mode.Value = BlurMode.Scatter;
                blur.TintColor.Value = new colorHDR(0f, 0f, 0f, 0f);
                blur.Opacity.Value = 1f;
                blur.Culling.Value = SidednessExisting(data, blur.Culling.Value);
                ReportColorMaskExisting(component, data);
                break;
            case HSVMaterial hsv:
                hsv.Culling.Value = SidednessExisting(data, hsv.Culling.Value);
                break;
            case LUTMaterial lut:
                lut.Culling.Value = SidednessExisting(data, lut.Culling.Value);
                break;
            case ThresholdMaterial threshold:
                threshold.Culling.Value = SidednessExisting(data, threshold.Culling.Value);
                break;
            case ChannelMatrixMaterial matrix:
                matrix.Culling.Value = SidednessExisting(data, matrix.Culling.Value);
                break;
        }
    }

    private static void ApplyXiexeExisting(XiexeToonMaterial toon, DataTreeDictionary data)
    {
        // Every map carries its own transform in the package.
        toon.MapsFollowAlbedo.Value = false;
        // With no ramp mask the package reads the ramp's bottom row; ours counts rows from the top.
        toon.ShadowRampRow.Value = 1f;

        UvSetExisting(toon.AlbedoUV, data, "AlbedoUV");
        UvSetExisting(toon.NormalUV, data, "NormalUV");
        UvSetExisting(toon.MetallicUV, data, "MetallicUV");
        UvSetExisting(toon.OcclusionUV, data, "OcclusionUV");
        UvSetExisting(toon.EmissionUV, data, "EmissionUV");
        UvSetExisting(toon.ThicknessUV, data, "ThicknessUV");

        if (FloatExisting(data, "Saturation") is float saturation)
            toon.Saturation.Value = saturation - 1f;

        // Saves from before the material had a blend mode marked a cutout surface with this flag alone.
        if (StoredExisting(data, "__legacyCutout") is true && data.TryGetNode("BlendMode") == null)
            toon.AlphaMode.Value = AlphaMode.Cutout;

        // Opaque either way: a surface that leaves alpha unwritten looks the same in the world. A flat toon's
        // ColorMask is a mask texture instead, which its rebuild into this toon drops.
        ReportColorMaskExisting(toon, data);
    }

    private static void ApplyWireframeExisting(WireframeMaterial wireframe, DataTreeDictionary data)
    {
        bool screenSpace = StoredExisting(data, "ScreenSpace") is true;
        if (FloatExisting(data, "Thickness") is float thickness)
        {
            // Screen space there measures twice the pixels from each edge; ours reaches half coverage at half the
            // width, so the two read as the same line.
            if (screenSpace)
                wireframe.LineWidth.Value = System.Math.Clamp(thickness, 0.1f, 20f);
            else
                wireframe.Thickness.Value = thickness;
        }

        if (StoredExisting(data, "DoubleSided") is bool doubleSided)
        {
            wireframe.Culling.Value = doubleSided ? Culling.None : Culling.Back;
            wireframe.UseInnerColors.Value = doubleSided;
        }
        wireframe.TextureTintsLines.Value = StoredExisting(data, "Texture") is string;
    }

    // The package names the face to DRAW, and Auto draws both on a blended surface.
    private static Culling SidednessExisting(DataTreeDictionary data, Culling current)
    {
        if (TextExisting(data, "Sidedness") is not { } sidedness)
            return current;
        bool blended = TextExisting(data, "BlendMode") is "Alpha" or "Transparent" or "Additive" or "Multiply";
        return sidedness switch
        {
            "Auto" => blended ? Culling.None : Culling.Back,
            "Front" => Culling.Back,
            "Back" => Culling.Front,
            "Double" or "DualSided" or "Both" => Culling.None,
            _ => current,
        };
    }

    private static void ReportColorMaskExisting(Component component, DataTreeDictionary data)
    {
        if (TextExisting(data, "ColorMask") is { } text && Enum.TryParse<ColorMask>(text, out var mask)
            && mask is not (ColorMask.RGBA or ColorMask.RGB))
            Logging.Logger.Warn($"Package import: {component.GetType().Name} on '{component.Slot?.Name}' stores ColorMask = {text}, which has no effect on ours.");
    }

    private static void UvSetExisting(Sync<TextureUVChannel> set, DataTreeDictionary data, string name)
    {
        if (StoredExisting(data, name) is IConvertible index and not string)
            set.Value = (TextureUVChannel)System.Math.Clamp(Convert.ToInt32(index, CultureInfo.InvariantCulture), 0, 3);
    }

    private static float? FloatExisting(DataTreeDictionary data, string name)
        => StoredExisting(data, name) is IConvertible value and not string and not bool
            ? Convert.ToSingle(value, CultureInfo.InvariantCulture)
            : null;

    private static string? TextExisting(DataTreeDictionary data, string name)
        => StoredExisting(data, name) as string;

    private static object? StoredExisting(DataTreeDictionary data, string name)
        => (data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue value ? value.Value : null;
}

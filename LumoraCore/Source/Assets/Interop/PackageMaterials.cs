// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// MATERIALS. A package splits most of its PBS families into a metallic and a specular type with the same
// effect; ours keep the effect as one material and the workflow as a member, so both package types land on
// it and the workflow is read off which members the package stored. -xlinka
public static partial class PackageTypeMap
{
    static void AliasesMats(Dictionary<string, string> aliases)
    {
        // One surface lit from both sides, with the vertex-colour forms of the same shader.
        aliases["PBS_DualSidedMetallic"] = "PBS_TwoSided";
        aliases["PBS_DualSidedSpecular"] = "PBS_TwoSided";
        aliases["PBS_VertexColorMetallic"] = "PBS_TwoSided";
        aliases["PBS_VertexColorSpecular"] = "PBS_TwoSided";

        aliases["PBS_RimMetallic"] = "PBS_RimLit";
        aliases["PBS_RimSpecular"] = "PBS_RimLit";

        aliases["PBS_IntersectMetallic"] = "PBS_IntersectGlow";
        aliases["PBS_IntersectSpecular"] = "PBS_IntersectGlow";

        aliases["PBSLerpMetallic"] = "PBS_TwoLayer";
        aliases["PBSLerpSpecular"] = "PBS_TwoLayer";

        aliases["PBS_MultiUV_Metallic"] = "PBS_MultiUV";
        aliases["PBS_MultiUV_Specular"] = "PBS_MultiUV";

        aliases["PBS_ColorSplatMetallic"] = "PBS_Splatmap";
        aliases["PBS_ColorSplatSpecular"] = "PBS_Splatmap";

        aliases["PBS_TriplanarMetallic"] = "PBS_TriplanarSurface";
        aliases["PBS_TriplanarSpecular"] = "PBS_TriplanarSurface";

        // Other materials for some views of a renderer; the merge of overrides that land on one view is in
        // FinishViewOverridesMats.
        aliases["RenderMaterialOverride"] = "ViewMaterialOverride";

        // A picture of one colour is a checker whose two colours are that colour.
        aliases["SolidColorTexture"] = "CheckerTextureProvider";

        AliasesMatsFilters(aliases);
        AliasesMatsSpecial(aliases);
        AliasesMatsExisting(aliases);
    }
}

public static partial class PackageMemberMapper
{
    // A stored member with this name never lands; ApplyMats takes it instead.
    private const string HandledMats = "\u0000mats";

    static void MemberAliasesMats(Dictionary<string, Dictionary<string, string>> table)
    {
        // The package stores a blended surface as a bool and a forced depth write as another; ours are the alpha
        // handling and ZWrite (ApplySurfaceMats). The stored gamma value drives nothing on a rim surface.
        MergeMats(table, "PBS_SurfaceBase", ("Transparent", HandledMats), ("ForceZWrite", HandledMats));
        MergeMats(table, "PBS_RimLit", ("GammaCurve", HandledMats));
        MergeMats(table, "PBS_TwoSided", ("VertexColorTarget", "VertexColors"));
        // UV sets are stored as plain indices from 0; ours are named sets (ApplyMats). The package's multi-UV
        // shader has no culling of its own and always draws front faces, whatever its member says.
        MergeMats(table, "PBS_MultiUV", ("AlbedoUV", HandledMats), ("SecondaryAlbedoUV", HandledMats),
            ("EmissionMapUV", HandledMats), ("SecondaryEmissionMapUV", HandledMats), ("NormalMapUV", HandledMats),
            ("OcclusionMapUV", HandledMats), ("MetallicMapUV", HandledMats), ("SpecularMapUV", HandledMats),
            ("Culling", HandledMats));
        // The splat shader neither clips nor switches its maps between scaling and replacing.
        MergeMats(table, "PBS_Splatmap", ("AlphaClip", HandledMats), ("MultiValue", HandledMats));

        // The canvas masking and raster state a projection, refraction or reflection carries for use inside a
        // UI. Ours draw in the world with none of it; ApplyMats says so when a stored value would have done
        // something, rather than dropping it unseen.
        var rasterState = new[] { "ColorMask", "Rect", "RectClip", "StencilComparison", "StencilID", "StencilOperation",
            "StencilReadMask", "StencilWriteMask", "ZTest" };
        foreach (var name in rasterState)
        {
            MergeMats(table, "Projection360Material", (name, HandledMats));
            MergeMats(table, "RefractMaterial", (name, HandledMats));
        }
        MergeMats(table, "Projection360Material", ("OffsetFactor", HandledMats), ("OffsetUnits", HandledMats));
        // One shared copy of the screen per frame either way here.
        MergeMats(table, "RefractMaterial", ("ZWrite", HandledMats), ("PerObject", HandledMats));
        MergeMats(table, "ReflectionMaterial", ("ZWrite", HandledMats));

        foreach (var (type, members) in GammaMetallicMats)
            foreach (var member in members)
                MergeMats(table, type, (member, HandledMats));

        // How a picture's pixels were stored: Linear or sRGB, or nothing for the material to decide.
        MergeMats(table, "ImageProvider", ("PreferredProfile", "Profile"));
        // Which view a renderer's other materials are for, read whole by ApplyMats.
        MergeMats(table, "ViewMaterialOverride", ("Context", HandledMats));
        // The colour and size are read whole by ApplyMats. Sampling state means nothing to one colour, and the
        // colour is stored in the picture's own profile, which is how ours stores it too.
        MergeMats(table, "CheckerTextureProvider", ("Color", HandledMats), ("Size", HandledMats), ("Mipmaps", "GenerateMipmaps"),
            ("FilterMode", HandledMats), ("AnisotropicLevel", HandledMats), ("WrapModeU", HandledMats), ("WrapModeV", HandledMats),
            ("MipmapBias", HandledMats), ("Profile", HandledMats), ("Format", HandledMats));

        MemberAliasesMatsFilters(table);
        MemberAliasesMatsSpecial(table);
        MemberAliasesMatsExisting(table);
        MemberAliasesMatsText(table);
    }

    // The package's plain metallic surface takes its Metallic slider gamma encoded, like a colour channel, and
    // decodes it before shading; ours shades metalness straight, so the stored value is decoded on the way in.
    // Read raw, a surface authored as 0.58 shades as 0.58 instead of 0.30: darker diffuse and a mirror finish on
    // what was meant to be half-metal. The package's other PBS shaders read their slider as it is, so only this
    // one is decoded. -xlinka
    private static readonly (string Type, string[] Members)[] GammaMetallicMats =
    {
        ("PBS_Metallic", new[] { "Metallic" }),
    };

    private static void DecodeMetallicMats(Component component, DataTreeDictionary data)
    {
        foreach (var (type, members) in GammaMetallicMats)
        {
            if (!IsOrDerives(component.GetType(), type))
                continue;
            foreach (var member in members)
            {
                if (StoredMats(data, member) is not IConvertible stored || stored is string || stored is bool)
                    continue;
                if (component.GetType().GetField(member)?.GetValue(component) is Sync<float> field)
                    field.Value = ColorSpace.GammaToLinear(Convert.ToSingle(stored, System.Globalization.CultureInfo.InvariantCulture));
            }
            return;
        }
    }

    private static bool IsOrDerives(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
            if (type.Name == name)
                return true;
        return false;
    }

    static void EnumAliasesMats(Dictionary<string, Dictionary<string, string>> table)
    {
        // The package names the vertex colour's third target after the workflow.
        table["VertexColorTarget"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Metallic"] = "Reflectance",
            ["Specular"] = "Reflectance",
        };
        // Alpha is never encoded on our side, so the package's sRGB-with-alpha reads as plain sRGB.
        table["PreferredProfile"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sRGBAlpha"] = "sRGB",
        };
    }

    // A picture with no stored profile is read the way the first material slot it lands in wants. The package's
    // PBS and toon materials want their metalness, occlusion and specular maps as sRGB, where ours take a data
    // slot as stored, so a picture still open after the links resolve is fixed to sRGB when one of those slots
    // holds it. The layered surface's occlusion maps want theirs linear, which is what ours do anyway. -xlinka
    private static readonly HashSet<string> SrgbDataSlotsMats = new(StringComparer.Ordinal)
    {
        "MetallicMap", "MetallicGlossMap", "OcclusionMap", "SpecularMap",
        "MetallicMap0", "MetallicMap1", "SpecularMap0", "SpecularMap1", "SpecularMap2", "SpecularMap3",
        "MetallicMap01", "MetallicMap23",
    };

    // After the links resolve: what only shows once materials, textures, meshes and renderers know each other.
    internal static void FinishMats(ICollection<IWorldElement> built)
    {
        FinishProfilesMats(built);
        FinishWireframesMats(built);
        FinishViewOverridesMats(built);
    }

    // The package keeps one override per kind of view; ours tells only the eyes from the rest, so a mirror and a
    // portal override of the same renderer are one here. The first keeps its materials and takes any slot only a
    // later one names; the later ones go, or each would draw a copy of its own.
    private static void FinishViewOverridesMats(IEnumerable<IWorldElement> built)
    {
        var kept = new Dictionary<(Components.MeshRenderer, Components.ViewMaterialScope), Components.ViewMaterialOverride>();
        foreach (var element in built)
        {
            if (element is not Components.ViewMaterialOverride view || view.IsDestroyed || view.Renderer.Target is not { } renderer)
                continue;
            var key = (renderer, view.View.Value);
            if (!kept.TryGetValue(key, out var first))
            {
                kept[key] = view;
                continue;
            }
            foreach (var slot in view.Overrides)
            {
                bool named = false;
                foreach (var held in first.Overrides)
                    named |= held.Index.Value == slot.Index.Value;
                if (named)
                    continue;
                var added = first.Overrides.Add();
                added.Index.Value = slot.Index.Value;
                added.Material.Target = slot.Material.Target;
            }
            view.Destroy();
        }
    }

    private static void FinishProfilesMats(IEnumerable<IWorldElement> built)
    {
        foreach (var element in built)
        {
            if (element is not MaterialProvider material || element.IsDestroyed)
                continue;
            foreach (var field in material.GetType().GetFields())
            {
                if (!SrgbDataSlotsMats.Contains(field.Name)
                    || field.GetValue(material) is not AssetRef<TextureAsset> slot
                    || slot.Target is not ImageProvider { Profile.Value: TextureProfile.Slot } picture)
                    continue;
                picture.Profile.Value = TextureProfile.sRGB;
            }
        }
    }

    private static void MergeMats(Dictionary<string, Dictionary<string, string>> table, string ours, params (string Stored, string To)[] pairs)
    {
        if (!table.TryGetValue(ours, out var members))
            table[ours] = members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (stored, to) in pairs)
            members.TryAdd(stored, to);
    }

    static void ApplyMats(Component component, DataTreeDictionary data)
    {
        ApplyMatsFilters(component, data);
        ApplyMatsSpecial(component, data);
        ApplyMatsExisting(component, data);
        ApplyMatsText(component, data);
        DecodeMetallicMats(component, data);
        switch (component)
        {
            case PBS_SurfaceBase surface:
                surface.Workflow.Value = WorkflowMats(data);
                ApplySurfaceMats(surface, data);
                break;
            case PBS_TwoLayer twoLayer:
                twoLayer.Workflow.Value = WorkflowMats(data);
                break;
            case PBS_Splatmap splat:
                splat.Workflow.Value = WorkflowMats(data);
                break;
            case Projection360Material or RefractMaterial or ReflectionMaterial:
                ReportRasterStateMats(component, data);
                break;
            // The package looks a matcap up with V counted from the picture's bottom row and ours from its top,
            // so an imported one is read mirrored top to bottom. Native ones keep the lookup they were made with.
            // The wearer's own eyes, or one of the views that film the scene; ours tell those apart only from the eyes.
            case CheckerTextureProvider solid when StoredColourMats(data, "Color") is { } colour:
                solid.ColorA.Value = colour;
                solid.ColorB.Value = colour;
                if ((data.TryGetNode("Size") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeList size
                    && ReadNumbers(size) is { Length: >= 2 } extent && extent[0] >= 1f && extent[1] >= 1f)
                {
                    solid.Width.Value = (int)extent[0];
                    solid.Height.Value = (int)extent[1];
                    solid.CellSize.Value = (int)System.Math.Max(extent[0], extent[1]);
                }
                break;
            case Components.ViewMaterialOverride view:
                view.View.Value = StoredMats(data, "Context") is "UserView"
                    ? Components.ViewMaterialScope.OwnEyes : Components.ViewMaterialScope.OtherViews;
                break;
            case MatcapMaterial matcap:
                matcap.MatcapScale.Value = new float2(1f, -1f);
                matcap.MatcapOffset.Value = new float2(0f, 1f);
                break;
            case XiexeToonMaterial toon:
                toon.MatcapScale.Value = new float2(1f, -1f);
                toon.MatcapOffset.Value = new float2(0f, 1f);
                break;
            case PBS_MultiUV multiUV:
                multiUV.Workflow.Value = WorkflowMats(data);
                UvSetMats(multiUV.AlbedoUV, data, "AlbedoUV");
                UvSetMats(multiUV.SecondaryAlbedoUV, data, "SecondaryAlbedoUV");
                UvSetMats(multiUV.EmissionMapUV, data, "EmissionMapUV");
                UvSetMats(multiUV.SecondaryEmissionMapUV, data, "SecondaryEmissionMapUV");
                UvSetMats(multiUV.NormalMapUV, data, "NormalMapUV");
                UvSetMats(multiUV.OcclusionMapUV, data, "OcclusionMapUV");
                UvSetMats(multiUV.MetallicMapUV, data, "MetallicMapUV");
                UvSetMats(multiUV.SpecularMapUV, data, "SpecularMapUV");
                break;
        }
    }

    // A specular type stores its reflectance as colours and maps, a metallic one as values and maps, layered
    // types with the layer's number after the name.
    private static PBSWorkflow WorkflowMats(DataTreeDictionary data)
    {
        foreach (var name in data.Children.Keys)
        {
            if (name.StartsWith("SpecularColor", StringComparison.Ordinal) || name.StartsWith("SpecularMap", StringComparison.Ordinal))
                return PBSWorkflow.Specular;
        }
        return PBSWorkflow.Metallic;
    }

    private static void UvSetMats(Sync<TextureUVChannel> set, DataTreeDictionary data, string name)
    {
        if (StoredMats(data, name) is IConvertible index and not string)
            set.Value = (TextureUVChannel)System.Math.Clamp(Convert.ToInt32(index, System.Globalization.CultureInfo.InvariantCulture), 0, 3);
    }

    private static void ApplySurfaceMats(PBS_SurfaceBase surface, DataTreeDictionary data)
    {
        if (StoredMats(data, "Transparent") is bool transparent)
            surface.AlphaHandling.Value = transparent ? AlphaHandling.AlphaBlend : AlphaHandling.Opaque;
        if (StoredMats(data, "ForceZWrite") is bool forceZWrite)
            surface.ZWrite.Value = forceZWrite ? ZWrite.On : ZWrite.Auto;
    }

    // What a stored raster member reads when it changes nothing.
    private static readonly (string Member, string[] Neutral)[] NeutralRasterMats =
    {
        ("ColorMask", new[] { "RGBA" }),
        ("RectClip", new[] { "False" }),
        ("StencilComparison", new[] { "Always", "Disabled" }),
        ("StencilOperation", new[] { "Keep" }),
        ("ZTest", new[] { "LessOrEqual", "Less" }),
        ("ZWrite", new[] { "Auto" }),
        ("OffsetFactor", new[] { "0" }),
        ("OffsetUnits", new[] { "0" }),
    };

    private static void ReportRasterStateMats(Component component, DataTreeDictionary data)
    {
        foreach (var (member, neutral) in NeutralRasterMats)
        {
            // Only what this type takes no member for; a ZWrite on a projection lands on its own.
            if (ResolveMemberName(component, member) != HandledMats || StoredMats(data, member) is not { } value)
                continue;
            string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            // A refraction never writes depth here, which is what Off asks for.
            if (member == "ZWrite" && text == "Off" && component is RefractMaterial)
                continue;
            // A stencil reference of zero at or below the buffer passes everywhere.
            if (member == "StencilComparison" && text == "LessOrEqual"
                && StoredMats(data, "StencilID") is IConvertible id && Convert.ToInt32(id, System.Globalization.CultureInfo.InvariantCulture) == 0)
                continue;
            if (Array.IndexOf(neutral, text) >= 0)
                continue;
            Logging.Logger.Warn($"Package import: {component.GetType().Name} on '{component.Slot?.Name}' stores {member} = {text}, which has no effect on ours.");
        }
    }

    private static color? StoredColourMats(DataTreeDictionary data, string name)
        => (data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data") is { } node
            && TryConvert(node, typeof(color), out var value, static url => url, name) && value is color colour
            ? colour
            : null;

    private static object? StoredMats(DataTreeDictionary data, string name)
        => (data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue value ? value.Value : null;
}

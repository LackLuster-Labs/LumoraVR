// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lumora.Audio;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Utility;
using Lumora.Core.Components.Variables;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

public static partial class PackageTypeMap
{
    static partial void AliasesK4(Dictionary<string, string> aliases)
    {
        // FOOTSTEPS. A package collider names the step sound it makes through a player component and a list of
        // sound sets, usually driven from world variables picked by the slot's tag; ours is the surface tag and
        // the set, and FootstepPlayback does the listening. The variables and drivers are typed by the set. -xlinka
        aliases["FootstepSoundPlayer"] = "FootstepSurface";
        aliases["FootstepSoundDefinition"] = "FootstepSoundSet";
        ClosedAliases["DynamicReferenceVariableDriver<IFootstepSoundMaterial>"] = typeof(ReferenceVariableDriver<FootstepSoundSet>);
        ClosedAliases["DynamicReferenceVariable<IFootstepSoundMaterial>"] = typeof(ReferenceVariable<FootstepSoundSet>);

        // Tags that say which renderer entries a collider stands for, and where the material tool may not paint.
        aliases["MaterialRelay"] = "MeshRendererMaterialRelay";
        aliases["MaterialApplyPolicy"] = "MaterialPaintPolicy";

        // A sprite is a region of a texture with a nine-slice frame, which is our Helio sprite.
        aliases["SpriteProvider"] = "Sprite";

        // Text and interface materials are our own text and UI materials. World text draws through the material
        // TextRenderer builds itself and wears the package's as its style; Helio text draws through its canvas
        // and takes the tint. See FoldTextLookK4. -xlinka
        aliases["TextUnlitMaterial"] = "TextMaterial";
        aliases["UI_TextUnlitMaterial"] = "UITextMaterial";
        aliases["UI_UnlitMaterial"] = "UIUnlitMaterial";

        // Keeping a material built while nothing draws with it.
        ClosedAliases["AssetLoader<Material>"] = typeof(AssetHolder<MaterialAsset>);

        // A volume texture comes in as a strip of its slices (PackageRawBitmap), which is how our lookup tables
        // take one; the only volumes a world carries are colour grades.
        aliases["StaticTexture3D"] = "ImageProvider";
        aliases["SimplexTexture"] = "SimplexTextureProvider";
        aliases["NoiseTexture"] = "NoiseTextureProvider";
        aliases["GridTexture"] = "GridTextureProvider";

        // A grabbable handle whose position and facing drive a slice material's plane.
        aliases["GenericSlicer"] = "SlicePlaneDriver";

        // How loud a source is, as a field.
        aliases["VolumeMeter"] = "AudioLevelMeter";

        // A sound's recent waveform drawn across a canvas rect. The package hosts the line as a record inside a
        // generic graphic; ours is the one graphic, which keeps the record's members as its own.
        ClosedAliases["RectMesh<AudioSourceWaveformMesh>"] = typeof(Components.UI.AudioWaveformGraphic);
        // The waveform is told what plays by a copy of an output's source; ours keeps sources as components.
        ClosedAliases["ReferenceCopy<IWorldAudioDataSource>"] = typeof(CopyReference<Component>);
    }
}

public static partial class PackageMemberMapper
{
    // A stored member with this name never lands; ApplyK4 takes it instead. The generic pass runs after the
    // hooks and would otherwise put the stored value straight back.
    private const string HandledK4 = "\u0000k4";

    // The distance-field span our font atlases are built with, for outline widths given in texels.
    private const float AtlasRangeK4 = 8f;

    // A package text material moves its face edge and its outline by a share of its distance field, scaled by
    // four over its pixel range, and its fonts lay a field of four texels round a 32 texel em; one unit is an
    // eighth of an em. Our atlases put 48 texels to the em, so a unit is six of ours. -xlinka
    private const float AtlasTexelsPerShiftK4 = 6f;

    static partial void MemberAliasesK4(Dictionary<string, Dictionary<string, string>> table)
    {
        MergeK4(table, "FootstepSurface", ("SoundMaterials", "Sounds"));
        MergeK4(table, "FootstepSoundSet", ("RolloffMode", HandledK4), ("Clips", HandledK4));
        // The fallback a variable driver writes while its variable is missing.
        MergeK4(table, "ReferenceVariableDriver`1", ("DefaultTarget", "DefaultValue"));

        // Entries of a renderer's list are fields, which a reference list cannot join; ApplyK4 queues them one by one.
        MergeK4(table, "MeshRendererMaterialRelay", ("MaterialRefs", HandledK4));
        MergeK4(table, "MaterialPaintPolicy", ("CanApply", "AllowPaint"));

        MergeK4(table, "Sprite", ("Rect", HandledK4), ("Scale", HandledK4), ("FixedSize", HandledK4));
        // A plain UI material is left off an image (AssignImageMaterialK4), so the material goes through ApplyK4.
        MergeK4(table, "Image", ("FlipHorizontally", "FlipHorizontal"), ("FlipVertically", "FlipVertical"),
            ("NineSliceSizing", HandledK4), ("Material", HandledK4));

        // Face and outline shifts are texels here and a share of the distance field there; the range belongs to
        // the atlas. Sides are a culling.
        MergeK4(table, "TextMaterial", ("OutlineThickness", HandledK4), ("FaceDilate", HandledK4), ("FaceSoftness", HandledK4),
            ("PixelRange", HandledK4), ("RenderQueue", HandledK4), ("Sidedness", HandledK4));
        MergeK4(table, "UITextMaterial", ("PixelRange", HandledK4), ("OutlineThickness", HandledK4), ("FaceDilate", HandledK4),
            ("FaceSoftness", HandledK4), ("Sidedness", HandledK4));
        // Clip, depth write, depth bias and mask carry straight across; sides are a culling, the mask mode an
        // enum of ours.
        MergeK4(table, "UIUnlitMaterial", ("Tint", "TintColor"), ("Sidedness", HandledK4), ("MaskMode", HandledK4));
        MergeK4(table, "Canvas", ("DefaultCulling", HandledK4));
        // Text alignment, line height and auto size under our names. A fitted size starts from the smaller
        // of the size and its ceiling (LayoutTextK4).
        MergeK4(table, "Text", ("HorizontalAlign", "HorizontalAlignment"), ("VerticalAlign", "VerticalAlignment"),
            ("LineHeight", "LineSpacing"), ("HorizontalAutoSize", "AutoSizeHorizontal"), ("VerticalAutoSize", "AutoSizeVertical"),
            ("AutoSizeMax", HandledK4), ("AlignmentMode", HandledK4));
        // World text: its tags, line spacing and fallback under our names; its size and what its alignment lines
        // up are worked out in LayoutWorldTextK4.
        MergeK4(table, "TextRenderer", ("ParseRichText", "RichText"), ("LineHeight", "LineSpacing"),
            ("Size", HandledK4), ("AlignmentMode", HandledK4));
        MergeK4(table, "LayoutElement", ("Priority", "PriorityValue"));
        // A row or column aligns along and across its axis, an overlap on each axis; ApplyK4 splits them.
        MergeK4(table, "HorizontalLayout", ("HorizontalAlign", HandledK4), ("VerticalAlign", HandledK4));
        MergeK4(table, "VerticalLayout", ("HorizontalAlign", HandledK4), ("VerticalAlign", HandledK4));
        MergeK4(table, "OverlappingLayout", ("HorizontalAlign", HandledK4), ("VerticalAlign", HandledK4));
        // A package scroll sits on the content it moves (ScrollRect.Viewport); its position is the same 0 to 1.
        MergeK4(table, "ScrollRect", ("NormalizedPosition", "Scroll"), ("ViewportOverride", "Viewport"),
            ("HorizontalAlign", HandledK4), ("VerticalAlign", HandledK4));
        MergeK4(table, "Slider", ("SlideDirection", "Direction"));
        MergeK4(table, "AudioWaveformGraphic", ("Mesh", HandledK4), ("Materials", HandledK4),
            ("Mesh.Source", "Source"), ("Mesh.HistoryLength", "HistoryLength"), ("Mesh.Points", "Points"),
            ("Mesh.Width", "Width"), ("Mesh.Color", "Color"), ("Mesh.UVScale", "UVScale"));

        MergeK4(table, "SimplexTextureProvider", ("Size", HandledK4), ("Mipmaps", "GenerateMipmaps"), ("Use3D", "Sample3D"), ("ZOffset", "Depth"));
        MergeK4(table, "NoiseTextureProvider", ("Size", HandledK4), ("Mipmaps", "GenerateMipmaps"), ("MonochromeMin", "Low"), ("MonochromeMax", "High"));
        MergeK4(table, "GridTextureProvider", ("Size", HandledK4), ("Mipmaps", "GenerateMipmaps"), ("BackgroundColor", "Background"), ("Grids", HandledK4));

        MergeK4(table, "AudioLevelMeter", ("Volume", "Level"), ("Method", HandledK4));
        MergeK4(table, "SlicePlaneDriver", ("_grabbedVisual", "GrabbedVisual"), ("Targets", HandledK4));
    }

    // Adds to whatever another family already keeps for the same component of ours, never over it.
    private static void MergeK4(Dictionary<string, Dictionary<string, string>> table, string ours, params (string Theirs, string To)[] pairs)
    {
        if (!table.TryGetValue(ours, out var members))
            table[ours] = members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (theirs, to) in pairs)
            members.TryAdd(theirs, to);
    }

    static partial void ApplyK4(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl)
    {
        switch (component)
        {
            case FootstepSoundSet set:
                ApplyFootstepSetK4(set, data, queueReferenceList);
                break;
            case MeshRendererMaterialRelay relay:
                ApplyMaterialRelayK4(relay, data, queueReference);
                break;
            case Helio.UI.Sprite sprite:
                ApplySpriteK4(sprite, data);
                break;
            case Helio.UI.Image image:
                ApplyImageK4(image, data);
                if (IdK4(data, "Material") is { } imageMaterial)
                    queueReference(new ImportLinkK4(found => AssignImageMaterialK4(image, found)), imageMaterial);
                break;
            case TextMaterial textMaterial:
                if (NumberK4(data, "OutlineThickness") is { } thickness)
                    textMaterial.OutlineThickness.Value = thickness * RangeScaleK4(data) * AtlasTexelsPerShiftK4;
                if (NumberK4(data, "FaceDilate") is { } dilate)
                    textMaterial.FaceDilate.Value = dilate * RangeScaleK4(data) * AtlasTexelsPerShiftK4;
                if (NumberK4(data, "FaceSoftness") is { } softness)
                    textMaterial.FaceSoftness.Value = softness * RangeScaleK4(data) * AtlasTexelsPerShiftK4;
                if (SidesK4(data) is { } textSides)
                    textMaterial.Culling.Value = textSides;
                break;
            case UITextMaterial uiLook:
                // Ours counts these in shares of our own atlas's field.
                if (NumberK4(data, "OutlineThickness") is { } uiThickness)
                    uiLook.OutlineThickness.Value = uiThickness * RangeScaleK4(data) * AtlasTexelsPerShiftK4 / AtlasRangeK4;
                if (NumberK4(data, "FaceDilate") is { } uiDilate)
                    uiLook.FaceDilate.Value = uiDilate * RangeScaleK4(data) * AtlasTexelsPerShiftK4 / AtlasRangeK4;
                if (NumberK4(data, "FaceSoftness") is { } uiSoftness)
                    uiLook.FaceSoftness.Value = uiSoftness * RangeScaleK4(data) * AtlasTexelsPerShiftK4 / AtlasRangeK4;
                if (SidesK4(data) is { } uiTextSides)
                    uiLook.Culling.Value = uiTextSides;
                break;
            case UIUnlitMaterial uiMaterial:
                if (SidesK4(data) is { } uiSides)
                    uiMaterial.Culling.Value = uiSides;
                if (StoredK4(data, "MaskMode") is DataTreeValue { Value: string maskMode })
                    uiMaterial.MaskMode.Value = maskMode == "AlphaClip" ? UIMaskMode.Clip : UIMaskMode.Multiply;
                break;
            case Helio.UI.Canvas canvas:
                // The package lays a canvas out the way its own text reads, from the slot's backward side once
                // the mirror is crossed (PackageHandedness). Its culling counts from that side.
                canvas.FaceBackward.Value = true;
                // Its layout elements combine per metric (see Canvas.PerMetricPriority).
                canvas.PerMetricPriority.Value = true;
                if (StoredK4(data, "DefaultCulling") is DataTreeValue { Value: string defaultCulling })
                {
                    canvas.Culling.Value = defaultCulling switch
                    {
                        "Off" => Culling.None,
                        "Front" => Culling.Front,
                        _ => Culling.Back,
                    };
                }
                break;
            case Components.UI.AudioWaveformGraphic waveform:
                ApplyWaveformK4(waveform, data, queueReference, queueReferenceList, remapUrl);
                break;
            case TextRenderer text:
                // Same mirror as a canvas: package text reads from the slot's backward side here.
                text.FaceBackward.Value = true;
                if (FirstIdK4(data, "Materials") is { } worldTextMaterial)
                    queueReference(new ImportLinkK4(found => FoldTextLookK4(text, found)), worldTextMaterial);
                LayoutWorldTextK4(text, data);
                break;
            case Helio.UI.Text uiText:
                if (FirstIdK4(data, "Materials") is { } uiTextMaterial)
                    queueReference(new ImportLinkK4(found => FoldTextLookK4(uiText, found)), uiTextMaterial);
                LayoutTextK4(uiText, data, queueReference);
                uiText.BlockMeasure.Value = BlockMeasureK4(data);
                // A justified package line stretches its glyphs' places, not its word gaps.
                uiText.Justification.Value = Helio.UI.TextJustification.Glyphs;
                // Lines take each font's full height, line gap and fallbacks included (Text.FullLineHeight).
                uiText.FullLineHeight.Value = true;
                // No text at all is how a field shows its hint; an empty one would hide it.
                if ((data.TryGetNode("Content") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: null }
                    && (data.TryGetNode("NullContent") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string { Length: > 0 } })
                    uiText.Content.Value = null!;
                break;
            case SimplexTextureProvider simplex:
                ApplySizeK4(data, simplex.Width, simplex.Height);
                break;
            case NoiseTextureProvider noise:
                ApplySizeK4(data, noise.Width, noise.Height);
                break;
            case GridTextureProvider grid:
                ApplyGridK4(grid, data);
                break;
            case PBS_Slice slice:
                OfferSlicePlanesK4(slice, data, queueReference);
                break;
            case SlicePlaneDriver slicer:
                JoinSlicerK4(slicer, data, queueReference);
                break;
            case Helio.UI.Layout.HorizontalLayout row:
                if (StoredK4(data, "HorizontalAlign") is DataTreeValue { Value: string rowMain })
                    row.MainAlignment.Value = MainAlignK4(rowMain);
                if (StoredK4(data, "VerticalAlign") is DataTreeValue { Value: string rowCross })
                    row.CrossAlignment.Value = CrossAlignK4(rowCross);
                break;
            case Helio.UI.Layout.VerticalLayout column:
                if (StoredK4(data, "VerticalAlign") is DataTreeValue { Value: string columnMain })
                    column.MainAlignment.Value = MainAlignK4(columnMain);
                if (StoredK4(data, "HorizontalAlign") is DataTreeValue { Value: string columnCross })
                    column.CrossAlignment.Value = CrossAlignK4(columnCross);
                break;
            case Helio.UI.ScrollRect scroll:
                scroll.HorizontalAlignment.Value = CrossAlignK4(StoredK4(data, "HorizontalAlign") is DataTreeValue { Value: string scrollX } ? scrollX : "Left");
                scroll.VerticalAlignment.Value = CrossAlignK4(StoredK4(data, "VerticalAlign") is DataTreeValue { Value: string scrollY } ? scrollY : "Top");
                // Without a viewport of its own it scrolls inside its parent's rect.
                if (IdK4(data, "ViewportOverride") == null && data.TryGetNode("ID") is DataTreeValue { Value: string scrollId })
                {
                    queueReference(new ImportLinkK4(_ =>
                    {
                        if (!scroll.IsDestroyed && scroll.Viewport.Target == null
                            && scroll.Slot?.Parent?.GetComponent<Helio.UI.RectTransform>() is { } parentRect)
                            scroll.Viewport.Target = parentRect;
                    }), scrollId);
                }
                break;
            case Helio.UI.Layout.OverlappingLayout overlap:
                if (StoredK4(data, "HorizontalAlign") is DataTreeValue { Value: string overlapX })
                    overlap.HorizontalAlignment.Value = CrossAlignK4(overlapX);
                if (StoredK4(data, "VerticalAlign") is DataTreeValue { Value: string overlapY })
                    overlap.VerticalAlignment.Value = CrossAlignK4(overlapY);
                break;
            case Components.Audio.AudioLevelMeter meter:
                if (StoredK4(data, "Method") is DataTreeValue { Value: string method })
                    meter.Method.Value = method == "Delta" ? Components.Audio.AudioLevelMethod.Change : Components.Audio.AudioLevelMethod.Magnitude;
                break;
        }
    }

    // FOOTSTEPS

    // The package keeps a list of clips, each with its own walking or landing switch, weight and pitch and
    // volume ranges. Ours keeps the walking clips and the landing clips, and one pitch and volume spread for
    // the set: the widest any clip asked for. -xlinka
    private static void ApplyFootstepSetK4(FootstepSoundSet set, DataTreeDictionary data,
        Action<object, string, IReadOnlyList<string>> queueReferenceList)
    {
        if (StoredK4(data, "RolloffMode") is DataTreeValue { Value: string rolloff })
        {
            set.Rolloff.Value = rolloff switch
            {
                "Linear" or "LinearFadeOff" => AudioRolloffCurve.Linear,
                "LogarithmicFadeOff" => AudioRolloffCurve.LogarithmicFadeOff,
                _ => AudioRolloffCurve.Logarithmic,
            };
        }
        if (StoredK4(data, "Clips") is not DataTreeList clips)
            return;
        var steps = new List<string>();
        var landings = new List<string>();
        float pitchSpread = 0f, volumeSpread = 0f;
        bool first = true;
        foreach (var entry in clips)
        {
            if (RecordK4(entry) is not { } record || IdK4(record, "Clip") is not { } clip)
                continue;
            if (BoolK4(record, "PlayForWalking") ?? true)
                steps.Add(clip);
            if (BoolK4(record, "PlayForLanding") ?? true)
                landings.Add(clip);
            pitchSpread = MathF.Max(pitchSpread, MathF.Max(MathF.Abs(NumberK4(record, "MinPitchVariation") ?? 0f), MathF.Abs(NumberK4(record, "MaxPitchVariation") ?? 0f)));
            volumeSpread = MathF.Max(volumeSpread, MathF.Max(MathF.Abs(NumberK4(record, "MinVolumeVariation") ?? 0f), MathF.Abs(NumberK4(record, "MaxVolumeVariation") ?? 0f)));
            if (first)
            {
                first = false;
                set.Pitch.Value = NumberK4(record, "VelocityMinPitch") ?? 1f;
                set.Volume.Value = NumberK4(record, "VelocityMaxVolume") ?? 1f;
            }
        }
        set.PitchVariation.Value = pitchSpread;
        set.VolumeVariation.Value = volumeSpread;
        if (steps.Count > 0)
            queueReferenceList(set.Steps, nameof(set.Steps), steps);
        // A landing list that is the walking list again would only repeat it; none means a step plays.
        if (landings.Count > 0 && landings.Count != steps.Count)
            queueReferenceList(set.Landings, nameof(set.Landings), landings);
    }

    // MATERIAL TAGS

    // Each entry names a material field on a renderer. Fields join through the single-reference pass, so every
    // entry gets its own element here and its own queued link.
    private static void ApplyMaterialRelayK4(MeshRendererMaterialRelay relay, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
    {
        if (StoredK4(data, "MaterialRefs") is not DataTreeList entries)
            return;
        foreach (var entry in entries)
        {
            var id = (entry as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string text } ? text : null;
            if (string.IsNullOrEmpty(id) || IsNullId(id))
                continue;
            var element = relay.Surfaces.Add();
            if (element != null)
                queueReference(element, id);
        }
    }

    // SPRITES

    // The package's region counts V up from the bottom; ours counts it down from the top row. Borders are shares
    // of the region in the package, left, bottom, right, top, which is what FractionalBorders reads.
    private static void ApplySpriteK4(Helio.UI.Sprite sprite, DataTreeDictionary data)
    {
        if (data.TryGetNode("FixedSize") == null)
            return;
        sprite.FractionalBorders.Value = true;
        if (NumberK4(data, "Scale") is { } scale && float.IsFinite(scale))
            sprite.BorderScale.Value = scale;
        if (NumberK4(data, "FixedSize") is { } fixedSize && float.IsFinite(fixedSize))
            sprite.FixedBorderSize.Value = fixedSize;
        if (StoredK4(data, "Rect") is DataTreeDictionary rect
            && FieldK4(rect, "X") is { } x && FieldK4(rect, "Y") is { } y
            && FieldK4(rect, "Width") is { } width && FieldK4(rect, "Height") is { } height)
            sprite.UVRect.Value = new Rect(x, 1f - y - height, width, height);
    }

    // A package image nine-slices whenever its sprite has a frame, and measures it the way its sizing says.
    private static void ApplyImageK4(Helio.UI.Image image, DataTreeDictionary data)
    {
        if (StoredK4(data, "NineSliceSizing") is DataTreeValue { Value: string sizing })
        {
            image.BorderSizing.Value = sizing switch
            {
                "FixedSize" => Helio.UI.BorderSizing.FixedSize,
                "RectWidth" => Helio.UI.BorderSizing.RectWidth,
                "RectHeight" => Helio.UI.BorderSizing.RectHeight,
                _ => Helio.UI.BorderSizing.Texels,
            };
        }
        if (IdK4(data, "Sprite") != null)
            image.NineSlice.Value = true;
    }

    // A UI material that only repeats Helio's own (no texture or mask, white, no stencil, no clip worth the name,
    // the canvas's sides, no depth write or bias) is left off, so the image keeps Helio's shared material, its
    // batching and its scroll clip; anything else is worn as authored. One drawing a single side of a two-sided
    // canvas is the plate a panel shows from behind, and is worn.
    private static void AssignImageMaterialK4(Helio.UI.Image image, IWorldElement found)
    {
        if (image.IsDestroyed || found is not IAssetProvider<MaterialAsset> material)
            return;
        var canvasCulling = image.Slot?.GetComponentInParents<Helio.UI.Canvas>()?.Culling.Value ?? Culling.Back;
        if (found is UIUnlitMaterial ui && ui.Texture.Target == null && ui.MaskTexture.Target == null && !ui.RectClip.Value
            && ui.TintColor.Value.r >= 0.999f && ui.TintColor.Value.g >= 0.999f && ui.TintColor.Value.b >= 0.999f && ui.TintColor.Value.a >= 0.999f
            && ui.ColorMask.Value == ColorMask.RGBA && ui.StencilComparison.Value == StencilComparison.Always
            && ui.StencilOperation.Value == StencilOperation.Keep && ui.BlendMode.Value == BlendMode.Alpha
            && ui.Culling.Value == canvasCulling && ui.ZWrite.Value != ZWrite.On
            && ui.OffsetFactor.Value == 0f && ui.OffsetUnits.Value == 0f
            && (!ui.AlphaClip.Value || ui.AlphaCutoff.Value <= 0.01f))
            return;
        image.Material.Target = material;
    }

    // The package's sides, counted from the face, as our culling: Front draws the face only (back culled),
    // Back the other side only, Double both; Auto draws both when see-through and the face otherwise.
    private static Culling? SidesK4(DataTreeDictionary data)
    {
        if (StoredK4(data, "Sidedness") is not DataTreeValue { Value: string sides })
            return null;
        return sides switch
        {
            "Front" => Culling.Back,
            "Back" => Culling.Front,
            "Double" => Culling.None,
            _ => StoredK4(data, "BlendMode") is DataTreeValue { Value: "Opaque" or "Cutout" } ? Culling.Back : Culling.None,
        };
    }

    // LAYOUT

    // Along a row or column: the package's justify spreads the leftover between the children.
    private static Helio.UI.Layout.MainAxisAlignment MainAlignK4(string align) => align switch
    {
        "Left" or "Top" => Helio.UI.Layout.MainAxisAlignment.Start,
        "Right" or "Bottom" => Helio.UI.Layout.MainAxisAlignment.End,
        "Justify" => Helio.UI.Layout.MainAxisAlignment.SpaceBetween,
        _ => Helio.UI.Layout.MainAxisAlignment.Center,
    };

    // Across one, or on either axis of an overlap, where justify has nothing to spread and centres.
    private static Helio.UI.Layout.LayoutAlignment CrossAlignK4(string align) => align switch
    {
        "Left" or "Top" => Helio.UI.Layout.LayoutAlignment.Start,
        "Right" or "Bottom" => Helio.UI.Layout.LayoutAlignment.End,
        _ => Helio.UI.Layout.LayoutAlignment.Center,
    };

    // A package text that fits itself never grows past its ceiling, so the size ours shrinks from is the
    // smaller of the two. Applied once every stored member has landed, the size among them.
    private static void LayoutTextK4(Helio.UI.Text text, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
    {
        if (BoolK4(data, "HorizontalAutoSize") != true && BoolK4(data, "VerticalAutoSize") != true)
            return;
        if (NumberK4(data, "AutoSizeMax") is not { } ceiling || !float.IsFinite(ceiling) || ceiling <= 0f)
            return;
        if (data.TryGetNode("ID") is not DataTreeValue { Value: string self })
            return;
        queueReference(new ImportLinkK4(_ =>
        {
            if (!text.IsDestroyed && text.Size.Value > ceiling)
                text.Size.Value = ceiling;
        }), self);
    }

    // A package world text lays out at a tenth of its size: its size 1 is an em of a tenth of a metre, and
    // ours is the em in metres. Its lines are each font's full height, line gap included, and as tall as the
    // tallest font they draw from. -xlinka
    private static void LayoutWorldTextK4(TextRenderer text, DataTreeDictionary data)
    {
        if (NumberK4(data, "Size") is { } size && float.IsFinite(size))
            text.Size.Value = size * 0.1f;
        text.FullLineHeight.Value = true;
        text.BlockMeasure.Value = BlockMeasureK4(data);
    }

    // Package text lines up the glyphs it draws with its rect (or origin) unless it says to line up its
    // baselines: a line height above the first down to the last.
    private static Helio.UI.TextBlockMeasure BlockMeasureK4(DataTreeDictionary data)
        => StoredK4(data, "AlignmentMode") is DataTreeValue { Value: "LineBased" }
            ? Helio.UI.TextBlockMeasure.Baselines
            : Helio.UI.TextBlockMeasure.Glyphs;

    // WAVEFORMS

    // The package keeps the line's settings as a record in the graphic's Mesh member; ours has them as its
    // own. The line re-meshes every frame it moves, so it gets a chunk of its own: the rest of the panel then
    // stays meshed while it does.
    private static void ApplyWaveformK4(Components.UI.AudioWaveformGraphic waveform, DataTreeDictionary data,
        Action<ISyncRef, string> queueReference, Action<object, string, IReadOnlyList<string>> queueReferenceList,
        Func<Uri, Uri?> remapUrl)
    {
        if (data.TryGetNode("Mesh") is DataTreeDictionary record)
        {
            foreach (var (stored, value) in record.Children)
            {
                if (value is not DataTreeDictionary holder || holder.TryGetNode("Data") is not { } node)
                    continue;
                for (int i = 0; i < waveform.SyncMemberCount; i++)
                {
                    if (waveform.GetSyncMemberName(i) != stored || waveform.GetSyncMember(i) is not { } member)
                        continue;
                    try
                    {
                        Assign(member, node, stored, stored, queueReference, queueReferenceList, remapUrl);
                    }
                    catch
                    {
                        // Left at its default, as any member that will not take its stored value.
                    }
                    break;
                }
            }
        }
        if (FirstIdK4(data, "Materials") is { } material)
            queueReference(waveform.Material, material);
        if (waveform.Slot != null && waveform.Slot.GetComponent<Helio.UI.GraphicChunkRoot>() == null)
            waveform.Slot.AttachComponent<Helio.UI.GraphicChunkRoot>();
    }

    // TEXT

    // What a package text material says about the text that wore it. World text wears it as its style, the
    // whole look: tint, outline, face, sides, draw order and stencil. A UI text material on world text (a sign
    // that stamps its letters into the stencil for a lit plate behind) is copied into a text material of ours
    // beside it, once. Helio text draws through its canvas's text material, so it takes the tint into its
    // colour. -xlinka
    private static readonly ConditionalWeakTable<UITextMaterial, TextMaterial> WorldStylesK4 = new();

    private static void FoldTextLookK4(Component text, IWorldElement found)
    {
        if (text.IsDestroyed)
            return;
        if (text is TextRenderer worldText)
        {
            switch (found)
            {
                case TextMaterial world:
                    worldText.Style.Target = world;
                    break;
                case UITextMaterial ui when !ui.IsDestroyed && ui.Slot != null:
                    worldText.Style.Target = WorldStylesK4.GetValue(ui, StyleFromUITextK4);
                    break;
            }
        }
        else if (text is Helio.UI.Text uiText)
        {
            colorHDR? tint = found switch
            {
                TextMaterial world => world.TintColor.Value,
                UITextMaterial ui => ui.TintColor.Value,
                _ => null,
            };
            if (tint is { } multiply)
                uiText.Color.Value = TintedK4(uiText.Color.Value, multiply);
        }
    }

    private static TextMaterial StyleFromUITextK4(UITextMaterial ui)
    {
        var style = ui.Slot.AttachComponent<TextMaterial>();
        style.TintColor.Value = ui.TintColor.Value;
        style.OutlineColor.Value = ui.OutlineColor.Value;
        style.OutlineThickness.Value = ui.OutlineThickness.Value * AtlasRangeK4;
        style.FaceDilate.Value = ui.FaceDilate.Value * AtlasRangeK4;
        style.FaceSoftness.Value = ui.FaceSoftness.Value * AtlasRangeK4;
        style.BlendMode.Value = ui.BlendMode.Value;
        style.Culling.Value = ui.Culling.Value;
        style.ZWrite.Value = ui.ZWrite.Value;
        style.ZTest.Value = ui.ZTest.Value;
        style.RenderQueue.Value = ui.RenderQueue.Value;
        style.ColorMask.Value = ui.ColorMask.Value;
        style.StencilComparison.Value = ui.StencilComparison.Value;
        style.StencilOperation.Value = ui.StencilOperation.Value;
        style.StencilID.Value = ui.StencilID.Value;
        style.StencilWriteMask.Value = ui.StencilWriteMask.Value;
        style.StencilReadMask.Value = ui.StencilReadMask.Value;
        CarryUITextLookMats(ui, style);
        return style;
    }

    private static color TintedK4(in color value, in colorHDR tint)
        => new color(value.r * tint.r, value.g * tint.g, value.b * tint.b, value.a * tint.a);

    // The package scales a distance-field width by four over its pixel range.
    private static float RangeScaleK4(DataTreeDictionary data)
        => NumberK4(data, "PixelRange") is { } range && range > 0f ? 4f / range : 1f;

    // PROCEDURAL TEXTURES

    private static void ApplySizeK4(DataTreeDictionary data, Sync<int> width, Sync<int> height)
    {
        if (StoredK4(data, "Size") is not DataTreeList size)
            return;
        var values = ReadNumbers(size);
        if (values.Length >= 2 && values[0] >= 1f && values[1] >= 1f)
        {
            width.Value = (int)values[0];
            height.Value = (int)values[1];
        }
    }

    // The package can stack grids; ours draws one, the first.
    private static void ApplyGridK4(GridTextureProvider grid, DataTreeDictionary data)
    {
        ApplySizeK4(data, grid.Width, grid.Height);
        if (StoredK4(data, "Grids") is not DataTreeList grids)
            return;
        foreach (var entry in grids)
        {
            if (RecordK4(entry) is not { } record)
                continue;
            if (PairK4(record, "Spacing") is { } spacing)
                grid.Spacing.Value = spacing;
            if (PairK4(record, "Offset") is { } offset)
                grid.Offset.Value = offset;
            if (PairK4(record, "Width") is { } lineWidth)
                grid.LineWidth.Value = lineWidth;
            if (StoredK4(record, "LineColor") is DataTreeList lineColor && ReadNumbers(lineColor) is { Length: >= 3 } c)
                grid.LineColor.Value = new color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f);
            break;
        }
    }

    // SLICE PLANES

    // A slicer names the plane fields of a slice material's list entries, which nothing else indexes, and the
    // two can be built in either order. The material offers its planes by field id; the slicer takes the first
    // it names, and the drives are joined once the material's list is filled, at reference resolution. -xlinka
    private sealed class SliceJoinsK4
    {
        public readonly Dictionary<string, (string MaterialId, int Index, bool Normal)> Planes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<(SlicePlaneDriver Driver, bool Normal)>> Waiting = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly ConditionalWeakTable<Slot, SliceJoinsK4> SliceJoinTableK4 = new();

    private static SliceJoinsK4? JoinsForK4(Component component)
    {
        var slot = component.Slot;
        if (slot == null)
            return null;
        while (slot.Parent != null && !slot.Parent.IsRootSlot)
            slot = slot.Parent;
        return SliceJoinTableK4.GetValue(slot, static _ => new SliceJoinsK4());
    }

    private static void OfferSlicePlanesK4(PBS_Slice slice, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
    {
        if (StoredK4(data, "Slicers") is not DataTreeList planes || data.TryGetNode("ID") is not DataTreeValue { Value: string materialId })
            return;
        var joins = JoinsForK4(slice);
        if (joins == null)
            return;
        int index = 0;
        foreach (var entry in planes)
        {
            if (RecordK4(entry) is { } record)
            {
                Offer(MemberIdK4(record, "Position"), false);
                Offer(MemberIdK4(record, "Normal"), true);
            }
            index++;
        }

        void Offer(string? fieldId, bool normal)
        {
            if (fieldId == null)
                return;
            joins.Planes[fieldId] = (materialId, index, normal);
            if (!joins.Waiting.Remove(fieldId, out var waiting))
                return;
            foreach (var (driver, drivesNormal) in waiting)
                QueueSliceDriveK4(driver, drivesNormal, materialId, index, queueReference);
        }
    }

    private static void JoinSlicerK4(SlicePlaneDriver slicer, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
    {
        if (StoredK4(data, "Targets") is not DataTreeList targets)
            return;
        var joins = JoinsForK4(slicer);
        if (joins == null)
            return;
        foreach (var entry in targets)
        {
            if (RecordK4(entry) is not { } record)
                continue;
            Join(IdK4(record, "Position"), false);
            Join(IdK4(record, "Normal"), true);
            break;
        }

        void Join(string? fieldId, bool normal)
        {
            if (fieldId == null)
                return;
            if (joins.Planes.TryGetValue(fieldId, out var plane))
            {
                QueueSliceDriveK4(slicer, normal, plane.MaterialId, plane.Index, queueReference);
                return;
            }
            if (!joins.Waiting.TryGetValue(fieldId, out var waiting))
                joins.Waiting[fieldId] = waiting = new List<(SlicePlaneDriver, bool)>(1);
            waiting.Add((slicer, normal));
        }
    }

    private static void QueueSliceDriveK4(SlicePlaneDriver driver, bool normal, string materialId, int index, Action<ISyncRef, string> queueReference)
    {
        queueReference(new ImportLinkK4(found =>
        {
            if (found is not PBS_Slice slice || driver.IsDestroyed || index >= slice.Slicers.Count)
                return;
            var plane = slice.Slicers[index];
            if (normal)
                driver.Normal.DriveTarget(plane.Normal);
            else
                driver.Point.DriveTarget(plane.Position);
        }), materialId);
    }

    // READING STORED DATA

    // A reference the importer resolves like any other, handing what it found to a callback instead of a
    // member: how one component reads another once the whole package has been built. -xlinka
    private sealed class ImportLinkK4 : ISyncRef
    {
        private readonly Action<IWorldElement> _resolved;

        public ImportLinkK4(Action<IWorldElement> resolved) => _resolved = resolved;

        public RefID Value { get; set; }
        public IWorldElement Target { get => null!; set { } }
        public IWorldElement RawTarget => null!;
        public Type TargetType => typeof(IWorldElement);
        public ReferenceState State => ReferenceState.Null;

        public void Clear() { }

        public bool TrySet(IWorldElement target)
        {
            try
            {
                _resolved(target);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Package import: a joined value did not carry: {ex.Message}");
            }
            return true;
        }
    }

    private static DataTreeNode? StoredK4(DataTreeDictionary data, string member)
    {
        var node = data.TryGetNode(member);
        return node is DataTreeDictionary holder && holder.TryGetNode("Data") is { } inner ? inner : node;
    }

    private static DataTreeDictionary? RecordK4(DataTreeNode entry)
    {
        var record = entry as DataTreeDictionary;
        return record?.TryGetNode("Data") is DataTreeDictionary nested ? nested : record;
    }

    private static string? IdK4(DataTreeDictionary data, string member)
        => StoredK4(data, member) is DataTreeValue { Value: string id } && id.Length > 0 && !IsNullId(id) ? id : null;

    // The id of the member itself, not of what it holds.
    private static string? MemberIdK4(DataTreeDictionary data, string member)
        => (data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("ID") is DataTreeValue { Value: string id } && id.Length > 0 ? id : null;

    private static string? FirstIdK4(DataTreeDictionary data, string member)
    {
        if (StoredK4(data, member) is not DataTreeList list)
            return null;
        foreach (var entry in list)
        {
            var id = entry switch
            {
                DataTreeDictionary d => (d.TryGetNode("Data") as DataTreeValue)?.Value as string,
                DataTreeValue v => v.Value as string,
                _ => null,
            };
            return string.IsNullOrEmpty(id) || IsNullId(id) ? null : id;
        }
        return null;
    }

    private static bool? BoolK4(DataTreeDictionary data, string member)
        => StoredK4(data, member) is DataTreeValue { Value: bool value } ? value : null;

    private static float? NumberK4(DataTreeDictionary data, string member)
        => StoredK4(data, member) is DataTreeValue { Value: IConvertible value } && value is not string and not bool
            ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : null;

    private static float? FieldK4(DataTreeDictionary data, string member) => NumberK4(data, member);

    private static float2? PairK4(DataTreeDictionary data, string member)
        => StoredK4(data, member) is DataTreeList list && ReadNumbers(list) is { Length: >= 2 } values
            ? new float2(values[0], values[1]) : null;
}

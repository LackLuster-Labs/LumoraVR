// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Assets;

// A volume texture (a scan, a cloud, a field of values) drawn by marching through a unit cube: put it on a box mesh
// of size 1 and scale the slot. Every StepSize along the view ray the volume is read, the window InputRange wide
// round InputOffset is stretched over 0..1 (values outside it count as nothing), raised to Exponent and scaled by
// Gain. DisplayMode adds the steps up, adds them until they pass AccumulationCutoff, or shows the first step
// brighter than HitThreshold. UseAlphaChannel reads the volume's alpha instead of its colour.
//
// Up to four Slices cut the volume away on the negative side of each and up to four Highlights tint a slab of
// it, both in the cube's own space. Additive by default; Multiply darkens what is behind, any other blend draws
// over it.
//
// The volume is the strip of square slices a package's volume texture arrives as (VolumeTexture_Core). -xlinka
[ComponentCategory("Assets/Materials")]
public class VolumeTextureMaterial : MaterialProvider
{
    public const int MaxPlanes = 4;

    public readonly AssetRef<TextureAsset> Volume;
    public readonly Sync<VolumeDisplayMode> DisplayMode;
    [Range(0.001f, 0.1f, "0.000")]
    public readonly Sync<float> StepSize;
    [Range(0.01f, 6f, "0.00")]
    public readonly Sync<float> Gain;
    [Range(0.01f, 6f, "0.00")]
    public readonly Sync<float> Exponent;
    [Range(0f, 1.5f, "0.00")]
    public readonly Sync<float> AccumulationCutoff;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> HitThreshold;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> InputRange;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> InputOffset;
    public readonly Sync<bool> UseAlphaChannel;

    public readonly SyncList<VolumeSlicePlane> Slices;
    public readonly SyncList<VolumeHighlight> Highlights;

    public readonly Sync<BlendMode> BlendMode;
    // -1 draws with the transparent surfaces.
    public readonly Sync<int> RenderQueue;

    private static readonly string[] SliceNames = { "Slice0", "Slice1", "Slice2", "Slice3" };
    private static readonly string[] HighlightNames = { "Highlight0", "Highlight1", "Highlight2", "Highlight3" };
    private static readonly string[] HighlightColorNames = { "HighlightColor0", "HighlightColor1", "HighlightColor2", "HighlightColor3" };

    private BlendMode? _blend;
    private string? _source;

    public override bool UseLoadingPlaceholder => false;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public VolumeTextureMaterial()
    {
        Volume = new AssetRef<TextureAsset>(this);
        DisplayMode = new Sync<VolumeDisplayMode>(this, VolumeDisplayMode.Additive);
        StepSize = new Sync<float>(this, 0.01f);
        Gain = new Sync<float>(this, 1f);
        Exponent = new Sync<float>(this, 1f);
        AccumulationCutoff = new Sync<float>(this, 1f);
        HitThreshold = new Sync<float>(this, 0.5f);
        InputRange = new Sync<float>(this, 1f);
        InputOffset = new Sync<float>(this, 0.5f);
        UseAlphaChannel = new Sync<bool>(this, false);

        Slices = new SyncList<VolumeSlicePlane>();
        Highlights = new SyncList<VolumeHighlight>();

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Additive);
        RenderQueue = new Sync<int>(this, -1);

        Slices.ElementsAdded += OnSlicesAdded;
        Slices.ElementsRemoving += OnSlicesRemoving;
        Slices.ElementsRemoved += (_, _, _) => PlanesChanged();
        Highlights.ElementsAdded += OnHighlightsAdded;
        Highlights.ElementsRemoving += OnHighlightsRemoving;
        Highlights.ElementsRemoved += (_, _, _) => PlanesChanged();
    }

    // A list does not raise a change pass on the component that holds it, so the planes report here. Through the
    // same once-a-frame change pass as the material's own fields: a driven plane moves every frame.
    internal void PlanesChanged() => NotifyChanged();

    private void OnSlicesAdded(SyncElementList<VolumeSlicePlane> list, int index, int count)
    {
        for (int i = index; i < index + count && i < list.Count; i++)
        {
            if (list[i] is { } plane)
                plane.Owner = this;
        }
        PlanesChanged();
    }

    private void OnSlicesRemoving(SyncElementList<VolumeSlicePlane> list, int index, int count)
    {
        for (int i = index; i < index + count && i < list.Count; i++)
        {
            if (list[i] is { } plane)
                plane.Owner = null;
        }
    }

    private void OnHighlightsAdded(SyncElementList<VolumeHighlight> list, int index, int count)
    {
        for (int i = index; i < index + count && i < list.Count; i++)
        {
            if (list[i] is { } highlight)
                highlight.Owner = this;
        }
        PlanesChanged();
    }

    private void OnHighlightsRemoving(SyncElementList<VolumeHighlight> list, int index, int count)
    {
        for (int i = index; i < index + count && i < list.Count; i++)
        {
            if (list[i] is { } highlight)
                highlight.Owner = null;
        }
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        var blend = BlendMode.Value;
        if (_blend != blend)
        {
            _blend = blend;
            _source = blend == Assets.BlendMode.Additive ? null : Source(blend);
        }
        SpecialMaterialSource.Wear(asset, _source, "res://Shaders/Mat_VolumeTexture.gdshader");
        asset.SetBlendMode(blend);
        asset.SetFloat("RenderQueue", RenderQueue.Value < 0 ? 3000 : RenderQueue.Value);

        asset.SetTexture("VolumeTexture", Volume.Asset);
        asset.SetInt("DisplayMode", (int)DisplayMode.Value);
        asset.SetFloat("StepSize", StepSize.Value);
        asset.SetFloat("Gain", Gain.Value);
        asset.SetFloat("Exponent", Exponent.Value);
        asset.SetFloat("AccumulationCutoff", AccumulationCutoff.Value);
        asset.SetFloat("HitThreshold", HitThreshold.Value);
        asset.SetBool("UseAlphaChannel", UseAlphaChannel.Value);

        float half = InputRange.Value * 0.5f;
        asset.SetFloat("LowClip", System.Math.Clamp(InputOffset.Value - half, 0f, 1f));
        asset.SetFloat("HighClip", System.Math.Clamp(InputOffset.Value + half, 0f, 1f));

        int slices = System.Math.Min(Slices.Count, MaxPlanes);
        asset.SetInt("SliceCount", slices);
        for (int i = 0; i < MaxPlanes; i++)
            asset.SetFloat4(SliceNames[i], i < slices && Slices[i] is { } plane ? plane.Plane : float4.Zero);

        int highlights = System.Math.Min(Highlights.Count, MaxPlanes);
        asset.SetInt("HighlightCount", highlights);
        for (int i = 0; i < MaxPlanes; i++)
        {
            var highlight = i < highlights ? Highlights[i] : null;
            asset.SetFloat4(HighlightNames[i], highlight != null ? highlight.Plane : float4.Zero);
            asset.SetColor(HighlightColorNames[i], highlight != null ? highlight.Color.Value : colorHDR.White);
        }
        asset.SetFloat4("HighlightRanges", new float4(RangeAt(0, highlights), RangeAt(1, highlights),
            RangeAt(2, highlights), RangeAt(3, highlights)));
    }

    private float RangeAt(int index, int count) => index < count && Highlights[index] is { } highlight ? highlight.Range.Value : 0f;

    // Blending is the only state that varies: the march always draws back faces with no depth test or write.
    private static string Source(BlendMode blend)
        => "shader_type spatial;\nrender_mode unshaded, fog_disabled, "
           + (blend == Assets.BlendMode.Multiply ? "blend_mul" : "blend_mix")
           + ", cull_front, depth_draw_never, depth_test_disabled;\n"
           + "#include \"res://Shaders/VolumeTexture_Core.gdshaderinc\"\n";
}

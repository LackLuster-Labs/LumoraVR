// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Components;

namespace Lumora.Core.Assets;

// The texture gathers and decodes itself through AssetManager; this component resolves the URL and
// supplies the load options (wrap/mipmaps/normal-map/resolution cap) as the variant descriptor, so
// textures with matching options for the same URL are shared.
//
// The resolution cap comes from the user's texture-quality setting unless this provider overrides
// it. Because the cap is part of the descriptor, changing the setting is enough to move every
// image onto a different variant: the descriptor stops matching, the provider re-requests, and the
// asset manager hands over the instance for the new cap. Nothing is torn down by hand and nothing
// reloads that is already resident. -xlinka
[ComponentCategory("Assets")]
public class ImageProvider : StaticAssetProvider<TextureAsset>, ICustomInspectorUI
{
    // affects compression/variant identity
    public readonly Sync<bool> IsNormalMap;

    public readonly Sync<TextureWrapMode> WrapModeU;

    public readonly Sync<TextureWrapMode> WrapModeV;

    public readonly Sync<bool> GenerateMipmaps;

    // pixels; 0 = follow the quality setting, -1 = pin to source resolution. Neither goes past this
    // machine's Max Texture Size: that setting is a memory ceiling, not a preference.
    public readonly Sync<int> MaxSizeOverride;

    public readonly Sync<bool> AllowCompression;

    // Every material slot this picture is bound to reads it the way this says, unless it says Slot.
    public readonly Sync<TextureProfile> Profile;

    // Point keeps every texel square; Slot leaves the filtering to the material slot. On, UseWrapModes has every
    // slot repeat or clamp the picture as WrapModeU/WrapModeV say; off, each slot does what it was made to.
    // A slot wraps both axes one way, so it clamps only when both say clamp, and mirroring repeats.
    public readonly Sync<TextureFilter> Filter;
    public readonly Sync<bool> UseWrapModes;

    public ImageProvider()
    {
        IsNormalMap = new Sync<bool>(this, false);
        WrapModeU = new Sync<TextureWrapMode>(this, TextureWrapMode.Repeat);
        WrapModeV = new Sync<TextureWrapMode>(this, TextureWrapMode.Repeat);
        GenerateMipmaps = new Sync<bool>(this, true);
        MaxSizeOverride = new Sync<int>(this, 0);
        AllowCompression = new Sync<bool>(this, true);
        Profile = new Sync<TextureProfile>(this, TextureProfile.Slot);
        Filter = new Sync<TextureFilter>(this, TextureFilter.Slot);
        UseWrapModes = new Sync<bool>(this, false);
    }

    public override void OnStart()
    {
        base.OnStart();
        EngineSettings.Changed += OnQualitySettingChanged;
    }

    public override void OnDestroy()
    {
        EngineSettings.Changed -= OnQualitySettingChanged;
        base.OnDestroy();
    }

    // The settings screen writes from ITS world's thread, which is not necessarily ours, so bounce
    // the re-resolve through our own world instead of re-requesting the asset from under it.
    private void OnQualitySettingChanged()
    {
        var world = World;
        if (world == null || IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!IsDestroyed)
                MarkChangeDirty();
        });
    }

    public int EffectiveMaxSize
    {
        get
        {
            int ceiling = EngineSettings.MaxTextureSize;
            int over = MaxSizeOverride.Value;
            if (over < 0)
                return ceiling;                             // pinned to source resolution, up to the ceiling
            if (over == 0)
                return ceiling;
            int asked = EngineSettings.SnapTextureSize(over);
            return ceiling > 0 && (asked <= 0 || asked > ceiling) ? ceiling : asked;
        }
    }

    protected override IAssetVariantDescriptor? GetVariantDescriptor() =>
        new TextureVariantDescriptor(
            GenerateMipmaps.Value,
            WrapModeU.Value,
            WrapModeV.Value,
            IsNormalMap.Value,
            EffectiveMaxSize,
            AllowCompression.Value ? TextureCompressionKind.Block : TextureCompressionKind.None,
            Profile.Value,
            Filter.Value,
            UseWrapModes.Value);

    // A cloud texture keeps its lumora address as the key it is loaded under, not the file or download
    // it would resolve to: that is how the texture knows to read the variants its uploader registered,
    // and the key stays the same once the original lands on this machine. A hash the build ships is
    // still served from the build.
    //
    // A local address stays one for the same reason. It names the record the sidecar and the ladder are
    // filed under; resolved to its file first, every texture this machine held loaded at source size
    // whatever the cap said, and never had a ladder made. The gather reads the same file either way.
    // -xlinka
    protected override System.Uri ProcessURL(System.Uri assetURL)
    {
        if (assetURL?.Scheme == Persistence.CloudAssetPacker.CloudScheme && CloudVariants.HashOf(assetURL) is { } hash
            && BuiltinAssetRegistry.ResUriFor(hash) == null)
            return new System.Uri($"{Persistence.CloudAssetPacker.CloudScheme}:///{hash}");
        if (assetURL?.Scheme == "local")
            return assetURL;
        return base.ProcessURL(assetURL!);
    }

    // DIAGNOSTICS
    //
    // Everything here is read back off the loaded asset, not recomputed from the component's
    // fields: the variant actually in use, the dimensions actually decoded, the format the renderer
    // actually reported, and VRAM computed from that format and those dimensions. When the renderer
    // has not reported a format yet the row says so instead of showing a plausible number. -xlinka
    public void BuildInspectorBody(UIBuilder ui)
    {
        var asset = Asset;
        if (asset == null)
        {
            InspectorStats.AddRow(ui, "Texture", URL.Value == null ? "no URL" : "not loaded");
            return;
        }

        var metadata = asset.Metadata;
        var variant = asset.LoadedVariant;

        InspectorStats.AddRow(ui, "Variant", variant is { IsOriginal: false }
            ? $"{variant.Value.MaxSize} px cap"
            : "source resolution");
        InspectorStats.AddRow(ui, "Quality cap", EngineSettings.DescribeTextureSize(EffectiveMaxSize)
            + (MaxSizeOverride.Value != 0 ? " (override)" : ""));
        InspectorStats.AddRow(ui, "Size", $"{asset.Width}x{asset.Height}");

        if (metadata == null)
        {
            InspectorStats.AddRow(ui, "Format", "not analyzed");
            return;
        }

        InspectorStats.AddRow(ui, "Format", metadata.DescribeFormat());
        InspectorStats.AddRow(ui, "Alpha", metadata.HasAlpha ? "present (scanned)" : "none (scanned)");
        InspectorStats.AddRow(ui, "Mips", metadata.MipCount.ToString());
        InspectorStats.AddRow(ui, "Color space", metadata.SRgb switch
        {
            true => "sRGB (declared)",
            false => "linear (declared)",
            _ => "not declared",
        });
        if (metadata.IsNormalMap)
            InspectorStats.AddRow(ui, "Marked", "normal map");
        if (metadata.IsHdr)
            InspectorStats.AddRow(ui, "Range", "HDR (half float, no resolution cap)");

        InspectorStats.AddRow(ui, "Stored", InspectorStats.Bytes(metadata.SourceBytes));
        InspectorStats.AddRow(ui, "Decoded", InspectorStats.Bytes(metadata.DecodedBytes));
        InspectorStats.AddRow(ui, "VRAM", metadata.GpuBytes is { } vram
            ? $"{InspectorStats.Bytes(vram)} ({metadata.GpuFormat!.Value.Label()})"
            : "renderer has not reported a format");

        var db = Engine.Current?.LocalDB;
        var url = URL.Value;
        if (db != null && url != null && url.Scheme == "local")
        {
            var cached = TextureVariantStore.ListCachedVariants(db, url.OriginalString);
            InspectorStats.AddRow(ui, "Cached variants", cached.Count == 0
                ? "none generated yet"
                : string.Join(", ", cached.ConvertAll(v => v.MaxSize + "px")));
        }
    }
}

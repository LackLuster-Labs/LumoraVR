// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Helio.UI;

// The materials of every canvas in a world that shares them (Canvas.SharesMaterials): one text material per
// atlas and text style, one image material per texture, one plain material, and one clone per material and
// surface priority, all on a local slot of the world's. A canvas whose content never scrolls draws the same
// from these as from its own. An open converted graph is a thousand small canvases, and each made a dozen
// and a half materials of its own, every one a component, a Godot material and a draw that could share
// nothing with the card beside it. -xlinka
internal sealed class SharedUIMaterials
{
    private readonly record struct TextKey(TextureAsset Atlas, bool Overlay, colorHDR OutlineColor, float OutlineThickness,
        float FaceDilate, float FaceSoftness, colorHDR UnderlayColor, float2 UnderlayOffset, float UnderlaySoftness);

    private static readonly ConditionalWeakTable<World, SharedUIMaterials> PerWorld = new();

    private readonly Slot _holder;
    private readonly Dictionary<TextKey, UITextMaterial> _text = new();
    private readonly Dictionary<(IAssetProvider<TextureAsset> Texture, bool Overlay, Culling Culling, bool Still), UIUnlitMaterial> _images = new();
    private readonly Dictionary<IAssetProvider<TextureAsset>, MainTexturePropertyBlock> _blocks = new();
    private readonly Dictionary<(bool Overlay, Culling Culling), UIUnlitMaterial> _plain = new();
    private readonly MaterialCloneCache _clones;

    private SharedUIMaterials(World world)
    {
        _holder = world.RootSlot.AddLocalSlot("Shared UI Materials");
        _clones = new MaterialCloneCache(_holder);
    }

    public static SharedUIMaterials Of(World world)
    {
        if (!PerWorld.TryGetValue(world, out var shared) || shared._holder.IsDestroyed)
        {
            shared = new SharedUIMaterials(world);
            PerWorld.AddOrUpdate(world, shared);
        }
        return shared;
    }

    public UITextMaterial Text(TextureAsset atlas, Canvas canvas)
    {
        var key = new TextKey(atlas, canvas.DrawsOverlay, canvas.TextOutlineColor.Value, canvas.TextOutlineThickness.Value,
            canvas.TextFaceDilate.Value, canvas.TextFaceSoftness.Value, canvas.TextUnderlayColor.Value,
            canvas.TextUnderlayOffset.Value, canvas.TextUnderlaySoftness.Value);
        if (!_text.TryGetValue(key, out var material) || material.IsDestroyed)
        {
            material = _holder.AddLocalSlot("TextMaterial").AttachComponent<UITextMaterial>();
            canvas.SetUpTextMaterial(material, atlas);
            _text[key] = material;
        }
        else if (Canvas.TextMaterialStale(material, atlas))
            canvas.SetUpTextMaterial(material, atlas);
        return material;
    }

    public UIUnlitMaterial Image(IAssetProvider<TextureAsset> texture, bool overlay, Culling culling, bool still = false)
    {
        if (!_images.TryGetValue((texture, overlay, culling, still), out var material) || material.IsDestroyed)
        {
            Sweep();
            material = _holder.AddLocalSlot("ImageMaterial").AttachComponent<UIUnlitMaterial>();
            material.Texture.Target = texture;
            material.PlayFrames.Value = !still;
            material.Culling.Value = culling;
            material.ZWrite.Value = ZWrite.Off;
            material.RenderQueue.Value = 3000;
            if (overlay)
                material.ZTest.Value = ZTest.Always;
            material.ForceUpdate();
            _images[(texture, overlay, culling, still)] = material;
        }
        return material;
    }

    public MainTexturePropertyBlock Block(IAssetProvider<TextureAsset> texture)
    {
        if (!_blocks.TryGetValue(texture, out var block) || block.IsDestroyed)
        {
            Sweep();
            block = _holder.AddLocalSlot("ImageBlock").AttachComponent<MainTexturePropertyBlock>();
            block.Texture.Target = texture;
            _blocks[texture] = block;
        }
        return block;
    }

    public UIUnlitMaterial Plain(bool overlay, Culling culling)
    {
        if (!_plain.TryGetValue((overlay, culling), out var material) || material.IsDestroyed)
        {
            material = _holder.AddLocalSlot("PlainMaterial").AttachComponent<UIUnlitMaterial>();
            material.Culling.Value = culling;
            material.ZWrite.Value = ZWrite.Off;
            material.RenderQueue.Value = 3000;
            if (overlay)
                material.ZTest.Value = ZTest.Always;
            _plain[(overlay, culling)] = material;
        }
        return material;
    }

    // Keyed by the priority itself rather than a chunk's surface index: every sharing chunk with a surface
    // at that priority from that material draws with the one clone. Nothing here scrolls, so no clip offset
    // is ever written to these, and none is evicted.
    public IAssetProvider<MaterialAsset>? Priority(IAssetProvider<MaterialAsset>? source, int renderPriority)
        => _clones.GetRenderPriorityMaterial(source, renderPriority, renderPriority);

    // A texture that is gone takes its material and block with it, so these stay the size of what is shown.
    private void Sweep()
    {
        List<(IAssetProvider<TextureAsset>, bool, Culling, bool)>? goneImages = null;
        foreach (var pair in _images)
        {
            if (pair.Key.Texture.IsDestroyed)
                (goneImages ??= new()).Add(pair.Key);
        }
        if (goneImages != null)
        {
            foreach (var key in goneImages)
            {
                if (!_images[key].IsDestroyed)
                    _images[key].Slot.Destroy();
                _images.Remove(key);
            }
        }
        List<IAssetProvider<TextureAsset>>? goneBlocks = null;
        foreach (var pair in _blocks)
        {
            if (pair.Key.IsDestroyed)
                (goneBlocks ??= new()).Add(pair.Key);
        }
        if (goneBlocks != null)
        {
            foreach (var key in goneBlocks)
            {
                if (!_blocks[key].IsDestroyed)
                    _blocks[key].Slot.Destroy();
                _blocks.Remove(key);
            }
        }
    }
}

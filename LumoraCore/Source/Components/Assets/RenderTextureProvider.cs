// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Components.Assets;

[ComponentCategory("Assets/Textures")]
public sealed class RenderTextureProvider : DynamicAssetProvider<RenderTexture>
{
    // Width and height in pixels, as one value so a button, a per-user choice or a script can set both at once.
    public readonly Sync<int2> Size;
    public readonly Sync<int> CullMask;
    public readonly Sync<color> ClearColor;
    public readonly Sync<float3> CameraPosition;
    public readonly Sync<floatQ> CameraRotation;
    public readonly Sync<float> OrthographicSize;

    public RenderTextureProvider()
    {
        Size = new Sync<int2>(this, new int2(1024, 1024));
        CullMask = new Sync<int>(this, 0);
        ClearColor = new Sync<color>(this, new color(0f, 0f, 0f, 0f));
        CameraPosition = new Sync<float3>(this, float3.Zero);
        CameraRotation = new Sync<floatQ>(this, floatQ.Identity);
        OrthographicSize = new Sync<float>(this, 1f);
    }

    public int Width => Size.Value.x;

    public int Height => Size.Value.y;

    // Saves from before Size kept the two numbers as Width and Height. -xlinka
    public override void Load(DataTreeNode node, LoadControl control)
    {
        base.Load(node, control);
        if (node is not DataTreeDictionary saved || saved.TryGetNode("Size") != null)
            return;
        int width = Stored(saved, "Width") ?? Size.Value.x;
        int height = Stored(saved, "Height") ?? Size.Value.y;
        Size.Value = new int2(width, height);
    }

    private static int? Stored(DataTreeDictionary saved, string member)
    {
        try
        {
            return saved.TryGetNode(member) is DataTreeValue { IsNull: false } leaf ? leaf.Extract<int>() : null;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnAssetCreated(RenderTexture asset) { }

    protected override void UpdateAsset(RenderTexture asset)
    {
        var size = Size.Value;
        asset.Configure(
            size.x,
            size.y,
            CullMask.Value,
            ClearColor.Value,
            CameraPosition.Value,
            CameraRotation.Value,
            OrthographicSize.Value);
    }

    protected override void OnAssetCleared() { }
}

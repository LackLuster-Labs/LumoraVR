// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Localization;

namespace Lumora.Core.Scripting;

// A material a graph could not take over in place (one of the built-in kinds): the graph material made in its
// stead, and the places the old one is worn that the new one would go, by the object they belong to. Nothing
// changes until Swap; then every one of them changes as one undo step. Kept on the machine of whoever dropped it.
public sealed class ShaderGraphSwapOffer
{
    private readonly List<AssetRef<MaterialAsset>> _uses;

    public IAssetProvider<MaterialAsset> Old { get; }
    public CustomShaderMaterial Made { get; }
    public IReadOnlyList<string> Objects { get; }
    public int Count => _uses.Count;

    internal ShaderGraphSwapOffer(IAssetProvider<MaterialAsset> old, CustomShaderMaterial made, List<AssetRef<MaterialAsset>> uses, List<string> objects)
    {
        Old = old;
        Made = made;
        _uses = uses;
        Objects = objects;
    }

    public bool IsGone => Made.IsDestroyed || Old.IsDestroyed;

    // The surfaces changed. Each refusal skips that one, not the rest.
    public int Swap()
    {
        if (IsGone)
            return 0;
        var records = new List<IUndoBatch>();
        foreach (var use in _uses)
        {
            if (use.IsDestroyed || !ReferenceEquals(use.Target, Old) || use is ILinkable { IsDriven: true })
                continue;
            try
            {
                if (MaterialSurfaces.AssignAt(use, Made) is { } record)
                    records.Add(record);
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        if (records.Count > 0 && Made.World is { } world)
            InspectorUndo.Record(world, CompositeUndoBatch.Combine(ShaderGraphOutput.SwapStep, records));
        return records.Count;
    }

    // "Cube", "Cube, Floor", "Cube, Floor and 3 more"
    public string ObjectList()
    {
        if (Objects.Count == 0)
            return string.Empty;
        if (Objects.Count <= 2)
            return string.Join(", ", Objects);
        return LocaleText.Keyed("Loom.Shader.AndMore", "{0}, {1} and {2} more", Objects[0], Objects[1], Objects.Count - 2).Resolve();
    }
}

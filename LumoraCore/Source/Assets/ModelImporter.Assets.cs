// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Import;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

public static partial class ModelImporter
{
    // Where an import's meshes, materials and textures go, and whether identical ones are shared.
    //
    // WithParts is the import as it always was: a part carries its own mesh and its materials sit on a "Material"
    // child of it. The other two gather them under one Assets slot, the model's or the world's, so a mesh or a
    // material can be found, swapped and reused without hunting through the model for it.
    //
    // Sharing turns everything that is the same into one asset: the same baked mesh (bakes are named by their
    // content, so two identical meshes are already one file and only ever needed one provider), the same texture,
    // and materials whose kind, colours and maps all agree whatever the file called them. -xlinka
    private sealed class AssetHome
    {
        private readonly ModelImportSettings _settings;
        private readonly Slot _model;
        private readonly bool _share;
        private readonly bool _gathered;
        private Slot? _root, _meshSlot, _materialSlot;

        private readonly Dictionary<string, MeshProvider> _sharedMeshes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MaterialProvider> _sharedMaterials = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImageProvider> _sharedTextures = new(StringComparer.Ordinal);
        private readonly Dictionary<int, MaterialProvider?> _partMaterials = new();
        private int _meshes, _materials, _textures, _meshReuse, _materialReuse, _textureReuse;

        public AssetHome(ModelImportSettings settings, Slot model)
        {
            _settings = settings;
            _model = model;
            // With nothing drawing them, an import's assets have nobody to be copied for: one of each.
            _share = settings.ShareAssets || settings.AssetsOnly;
            _gathered = settings.AssetsOnly || settings.AssetPlacement != ModelAssetPlacement.WithParts;
        }

        // The same scene material twice on one part has always been one material. Across parts it is one only
        // when sharing.
        public void BeginPart() => _partMaterials.Clear();

        public MeshProvider Mesh(Slot? part, Uri url, int meshIndex, string name)
        {
            string key = meshIndex >= 0 ? url.OriginalString + "#" + meshIndex.ToString(CultureInfo.InvariantCulture) : url.OriginalString;
            if (_share && _sharedMeshes.TryGetValue(key, out var known) && !known.IsDestroyed)
            {
                _meshReuse++;
                return known;
            }
            var host = _gathered || part == null ? MeshSlot().AddSlot(name) : part;
            var provider = host.AttachComponent<MeshProvider>();
            provider.URL.Value = url;
            if (meshIndex >= 0)
                provider.MeshIndex.Value = meshIndex;
            _meshes++;
            if (_share)
                _sharedMeshes[key] = provider;
            return provider;
        }

        public MaterialProvider? Material(Slot? part, int sceneIndex, ResolvedMaterial? rm, string? sourceName)
        {
            if (rm == null)
                return null;
            if (_partMaterials.TryGetValue(sceneIndex, out var onThisPart))
                return onThisPart;

            var type = _settings.MaterialTypeFor(sourceName);
            string? key = _share ? ContentKey(rm, type) : null;
            if (key != null && _sharedMaterials.TryGetValue(key, out var known) && !known.IsDestroyed)
            {
                _materialReuse++;
                _partMaterials[sceneIndex] = known;
                return known;
            }

            var host = _gathered || part == null
                ? MaterialSlot().AddSlot(string.IsNullOrEmpty(sourceName) ? $"Material {sceneIndex}" : sourceName)
                : part.AddSlot("Material");
            var material = BuildMaterialSync(host, rm, _settings, type, this);
            _materials++;
            _partMaterials[sceneIndex] = material;
            if (key != null)
                _sharedMaterials[key] = material;
            return material;
        }

        // On the slot of the first material that uses it; with sharing on, every later one points there.
        public ImageProvider Texture(Slot material, Uri uri, bool isNormal, bool noMips)
        {
            string key = uri.OriginalString + (isNormal ? "|normal" : "") + (noMips ? "|nomips" : "");
            if (_share && _sharedTextures.TryGetValue(key, out var known) && !known.IsDestroyed)
            {
                _textureReuse++;
                return known;
            }
            var tex = material.AttachComponent<ImageProvider>();
            tex.URL.Value = uri;
            if (isNormal) tex.IsNormalMap.Value = true;
            if (noMips) tex.GenerateMipmaps.Value = false;
            _textures++;
            if (_share)
                _sharedTextures[key] = tex;
            return tex;
        }

        public Slot Root()
        {
            if (_root is { IsDestroyed: false })
                return _root;
            _root = _settings.AssetPlacement == ModelAssetPlacement.World
                ? WorldAssets(_model).AddSlot(_model.SlotName.Value)
                : _settings.AssetsOnly ? _model : _model.AddSlot("Assets");
            return _root;
        }

        private Slot MeshSlot() => _meshSlot is { IsDestroyed: false } ? _meshSlot : _meshSlot = Root().AddSlot("Meshes");

        private Slot MaterialSlot() => _materialSlot is { IsDestroyed: false } ? _materialSlot : _materialSlot = Root().AddSlot("Materials");

        public string Describe()
            => $"assets {_settings.AssetPlacement}{(_share ? " shared" : "")}: {_meshes} mesh(es) +{_meshReuse} reused, "
               + $"{_materials} material(s) +{_materialReuse} reused, {_textures} texture(s) +{_textureReuse} reused";

        // Everything a built material is made of, so two scene materials that would build the same thing are
        // one. The file's name for it is left out on purpose: "Red" and "Red.001" are the usual duplicates.
        private string ContentKey(ResolvedMaterial rm, ModelMaterialType type)
        {
            var sb = new StringBuilder(160);
            sb.Append((int)type).Append('|').Append(_settings.MakeDualSided).Append('|').Append(rm.VertexColored).Append('|');
            Color(sb, rm.HasAlbedoColor && _settings.ImportAlbedoColor, rm.AlbedoColor);
            Color(sb, rm.HasEmissiveColor && _settings.ImportEmissive, rm.EmissiveColor);
            sb.Append(rm.AlbedoUri?.OriginalString).Append('|')
              .Append(rm.NormalUri?.OriginalString).Append('|')
              .Append(_settings.ImportEmissive ? rm.EmissiveUri?.OriginalString : null).Append('|')
              .Append(rm.MetallicUri?.OriginalString).Append('|')
              .Append(rm.OcclusionUri?.OriginalString);
            return sb.ToString();
        }

        private static void Color(StringBuilder sb, bool has, Lumora.Core.Math.colorHDR c)
        {
            if (has)
                sb.Append(c.r.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.g.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.b.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.a.ToString("R", CultureInfo.InvariantCulture));
            sb.Append('|');
        }

        // The world's Assets slot this user may build under: one they made, known by the allocation its id carries
        // (the model's own, both minted by this import), or a new one at the root. A child added under somebody
        // else's slot would be minted in their range. -xlinka
        private static Slot WorldAssets(Slot model)
        {
            var world = model.World;
            byte mine = model.ReferenceID.GetUserByte();
            foreach (var child in world.RootSlot.Children)
            {
                if (child is { IsDestroyed: false } && child.SlotName.Value == "Assets" && child.ReferenceID.GetUserByte() == mine)
                    return child;
            }
            return world.AddSpawnSlot(world.RootSlot, "Assets");
        }
    }

    // What the bake left for the world thread, for the assets-only build.
    private sealed record BakedMeshes(List<MeshGrouping.Unit> Units, MeshGrouping.Part[] Parts, Uri?[] UnitUris,
        int[]?[] UnitSubmeshParts, Dictionary<int, Uri> PartUris, bool[] MeshUsable, Uri SourceUri);

    // ASSETS ONLY: a slot per mesh, per material and per clip, nothing that draws, moves or collides. A mesh is
    // named after the node that first uses it; a mesh several nodes share is one asset. -xlinka
    private static async Task BuildAssetsOnlyAsync(World world, AssetHome home, NodeData? nodeTree, BakedMeshes bake,
        Dictionary<int, ResolvedMaterial?> resolvedMaterials, Dictionary<int, string> materialNames,
        List<(Animation.AnimationClip clip, Uri uri)> animations, ModelImportSettings settings, ModelImportResult result,
        IProgress<ImportProgress>? progress)
    {
        var unitNames = new string?[bake.Units.Count];
        NameUnits(nodeTree, unitNames);

        progress?.Report(new ImportProgress(ImportStage.Meshes, 0.70f, 0, bake.Units.Count));
        await OnWorldAsync(world, () =>
        {
            for (int u = 0; u < bake.Units.Count; u++)
            {
                string name = unitNames[u] ?? $"Mesh {u}";
                if (bake.UnitUris[u] is { } merged)
                {
                    home.Mesh(null, merged, -1, name);
                    continue;
                }
                var unitParts = bake.Units[u].Parts;
                foreach (int mi in unitParts)
                {
                    if (!bake.MeshUsable[mi])
                        continue;
                    string partName = unitParts.Length > 1 ? $"{name} {mi}" : name;
                    if (bake.PartUris.TryGetValue(mi, out var partUri))
                        home.Mesh(null, partUri, -1, partName);
                    else
                        home.Mesh(null, bake.SourceUri, mi, partName);
                }
            }

            var indices = new List<int>(resolvedMaterials.Keys);
            indices.Sort();
            foreach (int index in indices)
            {
                home.BeginPart();
                materialNames.TryGetValue(index, out var sourceName);
                home.Material(null, index, resolvedMaterials[index], sourceName);
            }

            if (animations.Count > 0)
            {
                var holder = home.Root().AddSlot("Animations");
                foreach (var (clip, uri) in animations)
                {
                    var provider = holder.AddSlot(string.IsNullOrEmpty(clip.Name) ? "Clip" : clip.Name).AttachComponent<AnimationProvider>();
                    provider.URL.Value = uri;
                    result.AnimationProviders.Add(provider);
                }
            }
        }).ConfigureAwait(false);

        if (settings.AfterBuild != null)
            await OnWorldAsync(world, () => settings.AfterBuild(result)).ConfigureAwait(false);
        progress?.Report(new ImportProgress(ImportStage.Complete, 1f));
    }

    private static void NameUnits(NodeData? node, string?[] names)
    {
        if (node == null)
            return;
        if (node.Units != null)
        {
            foreach (int u in node.Units)
                names[u] ??= string.IsNullOrEmpty(node.Name) ? null : node.Name;
        }
        if (node.Children != null)
        {
            foreach (var child in node.Children)
                NameUnits(child, names);
        }
    }

    // The studio over the model just built, markers already on its rig. A model with no rig to line up on
    // says so and gets none: the studio would only stand there waiting for a body that is not coming.
    private static void OpenStudioFor(Slot model)
    {
        if (model == null || model.IsDestroyed)
            return;
        if (AvatarStudio.OpenFor(model) == null)
            Logger.Log($"ModelImporter: '{model.SlotName.Value}' has no humanoid or four-legged rig; avatar studio not opened");
    }
}

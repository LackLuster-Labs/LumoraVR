// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

public static partial class ModelImporter
{
    // TEXTURES BY NAME.
    //
    // A file can name its materials and link none of their textures: an FBX exported without embedding them or
    // with the paths stripped, an OBJ whose .mtl names no maps. The pictures usually still sit beside it, named
    // after the material: Body.png, Body_BaseColor.png, body-normal.jpg, T_Body_AO.tga. This fills the map slots
    // the file left EMPTY from those, by the material's name plus a suffix that says which map. A texture the file
    // does link always wins, and a picture whose name is the material's plus anything else ("Body2", "BodyTrim")
    // is someone else's. -xlinka
    private sealed class TexturesByName
    {
        private enum Map { Albedo, Normal, Emissive, Metallic, Occlusion }

        private static readonly Dictionary<string, Map> Suffixes = new(StringComparer.Ordinal)
        {
            [""] = Map.Albedo, ["albedo"] = Map.Albedo, ["basecolor"] = Map.Albedo, ["basecolour"] = Map.Albedo,
            ["base"] = Map.Albedo, ["diffuse"] = Map.Albedo, ["diff"] = Map.Albedo, ["color"] = Map.Albedo,
            ["colour"] = Map.Albedo, ["col"] = Map.Albedo, ["maintex"] = Map.Albedo, ["albedotransparency"] = Map.Albedo,
            ["d"] = Map.Albedo, ["c"] = Map.Albedo,
            ["normal"] = Map.Normal, ["normals"] = Map.Normal, ["normalmap"] = Map.Normal, ["normalgl"] = Map.Normal,
            ["normalopengl"] = Map.Normal, ["normaldx"] = Map.Normal, ["nrm"] = Map.Normal, ["nor"] = Map.Normal,
            ["norm"] = Map.Normal, ["n"] = Map.Normal,
            ["emission"] = Map.Emissive, ["emissive"] = Map.Emissive, ["emit"] = Map.Emissive, ["glow"] = Map.Emissive,
            ["e"] = Map.Emissive,
            ["metallic"] = Map.Metallic, ["metalness"] = Map.Metallic, ["metal"] = Map.Metallic,
            ["metallicroughness"] = Map.Metallic, ["metallicsmoothness"] = Map.Metallic, ["roughness"] = Map.Metallic,
            ["rough"] = Map.Metallic, ["orm"] = Map.Metallic, ["m"] = Map.Metallic,
            ["ao"] = Map.Occlusion, ["occlusion"] = Map.Occlusion, ["ambientocclusion"] = Map.Occlusion, ["occ"] = Map.Occlusion,
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tif", ".tiff", ".webp", ".psd",
        };

        // Bounded like the linked-texture search: the model's folder and one level of folders under it.
        private const int MaxFolders = 32;
        private const int MaxImages = 4096;

        private readonly string _modelDir;
        private List<(string Path, string Stem)>? _images;
        private readonly Dictionary<string, Uri?> _imported = new(StringComparer.OrdinalIgnoreCase);

        public TexturesByName(string modelDir) => _modelDir = modelDir;

        public int Matched { get; private set; }

        public async Task FillAsync(ResolvedMaterial rm, string? materialName, LocalDB? localDB)
        {
            string key = Normalize(materialName);
            if (key.Length == 0)
                return;

            foreach (var (path, stem) in Images())
            {
                if (!stem.StartsWith(key, StringComparison.Ordinal)
                    || !Suffixes.TryGetValue(stem.Substring(key.Length), out var map)
                    || Filled(rm, map))
                    continue;
                var uri = await ImportAsync(path, localDB).ConfigureAwait(false);
                if (uri == null)
                    continue;
                switch (map)
                {
                    case Map.Albedo: rm.AlbedoUri = uri; break;
                    case Map.Normal: rm.NormalUri = uri; break;
                    case Map.Emissive: rm.EmissiveUri = uri; break;
                    case Map.Metallic: rm.MetallicUri = uri; break;
                    case Map.Occlusion: rm.OcclusionUri = uri; break;
                }
                Matched++;
                Logger.Debug($"ModelImporter: '{Path.GetFileName(path)}' is material '{materialName}' {map}, by name");
            }
        }

        private static bool Filled(ResolvedMaterial rm, Map map) => map switch
        {
            Map.Albedo => rm.AlbedoUri != null,
            Map.Normal => rm.NormalUri != null,
            Map.Emissive => rm.EmissiveUri != null,
            Map.Metallic => rm.MetallicUri != null,
            _ => rm.OcclusionUri != null,
        };

        private async Task<Uri?> ImportAsync(string path, LocalDB? localDB)
        {
            if (_imported.TryGetValue(path, out var known))
                return known;
            Uri? uri = null;
            try
            {
                if (localDB != null)
                {
                    var local = await localDB.ImportLocalAssetAsync(path, LocalDB.ImportLocation.Copy).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(local))
                        uri = new Uri(await TextureStorage.ForTransferAsync(localDB, local).ConfigureAwait(false));
                }
                uri ??= new Uri(path);
            }
            catch (Exception ex)
            {
                Logger.Warn($"ModelImporter: could not take '{Path.GetFileName(path)}' as a texture: {ex.Message}");
            }
            _imported[path] = uri;
            return uri;
        }

        // Sorted, so the same folder always gives the same answer when two pictures claim one map.
        private List<(string Path, string Stem)> Images()
        {
            if (_images != null)
                return _images;
            _images = new List<(string, string)>();
            if (string.IsNullOrEmpty(_modelDir) || !Directory.Exists(_modelDir))
                return _images;
            try
            {
                Collect(_modelDir);
                int folders = 0;
                foreach (var dir in Directory.EnumerateDirectories(_modelDir))
                {
                    if (++folders > MaxFolders || _images.Count >= MaxImages)
                        break;
                    Collect(dir);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"ModelImporter: looking for textures by name under '{_modelDir}' failed: {ex.Message}");
            }
            _images.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return _images;
        }

        private void Collect(string dir)
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (_images!.Count >= MaxImages)
                    return;
                if (!ImageExtensions.Contains(Path.GetExtension(file)))
                    continue;
                string stem = Path.GetFileNameWithoutExtension(file);
                if (stem.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
                    stem = stem.Substring(2);
                else if (stem.StartsWith("tex_", StringComparison.OrdinalIgnoreCase))
                    stem = stem.Substring(4);
                _images.Add((file, Normalize(stem)));
            }
        }

        // Case, spaces, dots, dashes and underscores carry nothing: "Body_Base Color" is "bodybasecolor".
        private static string Normalize(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether an asset has loaded on the machine running the script, for any kind of asset. loaded is true once
// it can be used. state says where it stands: created (nothing uses it yet, so it has not been asked to
// load), load started, partly loaded (a lower quality stand-in is up), fully loaded, failed, or unloaded
// (no address, or no provider the script can see). progress runs 0 to 1 while its bytes come from another
// machine in the session, is 1 once loaded, and -1 while nothing measures it, as for a web download or a
// decode. error says why a load failed. Each machine loads its own copy, so the answer is this machine's.
[Node("Assets/Load State", Capability.WorldRead, Description = "Reports whether an asset is ready on this machine, its loading state, available progress and any load error.",
    Keywords = "is loaded, loading progress, download progress, asset ready, load failed")]
public sealed class AssetLoadInfo : ReadNode
{
    [return: Port("loaded")]
    public static bool Run(ScriptContext ctx, IAssetProvider<TextureAsset>? asset, out AssetLoadState state, out float progress, out string error)
        => ScriptAssetLoads.Read(ctx, asset, out state, out progress, out error);

    [return: Port("loaded")]
    public static bool Run(ScriptContext ctx, IAssetProvider<MeshDataAsset>? asset, out AssetLoadState state, out float progress, out string error)
        => ScriptAssetLoads.Read(ctx, asset, out state, out progress, out error);

    [return: Port("loaded")]
    public static bool Run(ScriptContext ctx, IAssetProvider<MaterialAsset>? asset, out AssetLoadState state, out float progress, out string error)
        => ScriptAssetLoads.Read(ctx, asset, out state, out progress, out error);

    [return: Port("loaded")]
    public static bool Run(ScriptContext ctx, IAssetProvider<AudioClip>? asset, out AssetLoadState state, out float progress, out string error)
        => ScriptAssetLoads.Read(ctx, asset, out state, out progress, out error);

    [return: Port("loaded")]
    public static bool Run(ScriptContext ctx, IAssetProvider<FontSet>? asset, out AssetLoadState state, out float progress, out string error)
        => ScriptAssetLoads.Read(ctx, asset, out state, out progress, out error);

    [return: Port("loaded")]
    public static bool Run(ScriptContext ctx, IAssetProvider<AnimationAsset>? asset, out AssetLoadState state, out float progress, out string error)
        => ScriptAssetLoads.Read(ctx, asset, out state, out progress, out error);

    [return: Port("loaded")]
    public static bool Run(ScriptContext ctx, IAssetProvider<CubemapAsset>? asset, out AssetLoadState state, out float progress, out string error)
        => ScriptAssetLoads.Read(ctx, asset, out state, out progress, out error);
}

// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.Avatar;

// Where one avatar stands on this machine: what it has to have before it can be shown, and how much of that
// is here.
//
// THE PARTS are the fetched assets its renderers draw with: every mesh, and of every material the texture it
// cannot be shown without (the main texture where it names one, else every texture it holds). That is the
// same line the renderers draw: a surface leaves the loading skin when its material is display-ready, and a
// mesh draws when its geometry is in. Secondary maps finish after the avatar shows, exactly as they would on
// anything else. Generated assets are never parts, and neither is an address nothing asks for: a part counts
// once something references it, or once a renderer that would draw it is on.
//
// THE GATES are what comes after the bytes: each such material reporting itself display-ready, and each
// skinned mesh built by the platform. Ready is every part in and every gate open, and only Ready is 100%.
//
// PROGRESS weighs each part by its size once a transfer has told us the size, otherwise by the average of
// the sizes known. Bytes come from the session's transfers for local addresses (a texture's variants count
// toward the texture) and from the content service's for cloud and web ones; a part loading off this
// machine's own disk only moves when it lands. -xlinka
internal sealed class AvatarArrivalCensus
{
    public enum Stage { Downloading, Preparing, Building, Ready, Failed }

    private sealed class Part
    {
        public IAssetProvider Provider = null!;
        public IUrlAssetProvider Url = null!;
        public Uri Source = null!;
        public string Address = string.Empty;
        public string? DownloadKey;
        public MeshRenderer? Renderer;
        public bool IsMesh;
        public long Total;
        public long Received;
        public bool Done;
        public bool Failed;
        public bool Broken;
        public bool Moving;
        public int Pass;
    }

    // A part nobody has given a size to yet weighs this until one is known.
    private const long UnknownWeight = 1L << 20;

    // A transfer is most of a part; the rest is the decode and the upload after it.
    private const float TransferShare = 0.85f;
    private const float ArrivedShare = 0.9f;

    private readonly Dictionary<IAssetProvider, Part> _byProvider = new(ReferenceEqualityComparer.Instance);
    private readonly List<Part> _parts = new();
    private readonly List<Part> _dropped = new();
    private readonly List<MaterialProvider> _materials = new();
    private readonly HashSet<MaterialProvider> _materialSet = new(ReferenceEqualityComparer.Instance);
    private readonly List<SkinnedMeshRenderer> _skinned = new();
    private readonly List<MeshRenderer> _meshRenderers = new();
    private readonly List<(string Address, long Received, long Total)> _downloads = new();
    private int _pass;

    public Stage State { get; private set; } = Stage.Ready;
    public bool Ready => State == Stage.Ready;
    public bool BlockingFailure => State == Stage.Failed;

    // 0 to 1, and 1 only when Ready.
    public float Progress { get; private set; } = 1f;

    public int Parts { get; private set; }
    public int PartsDone { get; private set; }
    public int MissingMeshes { get; private set; }
    public int BrokenMeshes { get; private set; }
    public long BytesReceived { get; private set; }
    public bool AssetsIn { get; private set; }

    public void Clear()
    {
        _byProvider.Clear();
        _parts.Clear();
        _materials.Clear();
        _materialSet.Clear();
        _skinned.Clear();
        _meshRenderers.Clear();
        State = Stage.Ready;
        Progress = 1f;
        Parts = PartsDone = MissingMeshes = BrokenMeshes = 0;
        BytesReceived = 0;
        AssetsIn = true;
    }

    // Walks the avatar for its parts and gates. What a part has learned (its size, that it arrived) is kept
    // across walks, so walking again as the avatar changes shape never sends the readout backwards.
    public void Take(Slot avatar)
    {
        _pass++;
        _parts.Clear();
        _materials.Clear();
        _materialSet.Clear();
        _skinned.Clear();
        _meshRenderers.Clear();
        if (avatar == null || avatar.IsDestroyed)
            return;

        avatar.GetComponentsInChildren(_meshRenderers);
        foreach (var renderer in _meshRenderers)
        {
            if (renderer.IsDestroyed)
                continue;
            if (renderer.Mesh.Target is IAssetProvider mesh)
                AddPart(mesh, isMesh: true, renderer);
            for (int i = 0; i < renderer.Materials.Count; i++)
                AddMaterial(renderer.Materials[i] as MaterialProvider);
        }
        _meshRenderers.Clear();

        avatar.GetComponentsInChildren(_skinned);
        for (int i = _skinned.Count - 1; i >= 0; i--)
        {
            var skinned = _skinned[i];
            if (skinned.IsDestroyed)
            {
                _skinned.RemoveAt(i);
                continue;
            }
            if (skinned.MeshAsset.Target is IAssetProvider mesh)
                AddPart(mesh, isMesh: true, null);
            AddMaterial(skinned.Material.Target as MaterialProvider);
            for (int m = 0; m < skinned.Materials.Count; m++)
                AddMaterial(skinned.Materials[m] as MaterialProvider);
        }

        _dropped.Clear();
        foreach (var part in _byProvider.Values)
            if (part.Pass != _pass)
                _dropped.Add(part);
        foreach (var part in _dropped)
            _byProvider.Remove(part.Provider);
        _dropped.Clear();
    }

    private void AddMaterial(MaterialProvider? material)
    {
        if (material == null || material.IsDestroyed || !material.UseLoadingPlaceholder || !_materialSet.Add(material))
            return;
        _materials.Add(material);
        if (material is ICommonMaterial common && common.MainTexture != null)
        {
            AddPart(common.MainTexture, isMesh: false, null);
            return;
        }
        int count = material.SyncMemberCount;
        for (int i = 0; i < count; i++)
        {
            if (material.GetSyncMember(i) is IAssetRef reference && reference.Target != null)
                AddPart(reference.Target, isMesh: false, null);
        }
    }

    private void AddPart(IAssetProvider provider, bool isMesh, MeshRenderer? renderer)
    {
        if (provider is not IUrlAssetProvider url || url.SourceUrl is not { } source || provider is Component { IsDestroyed: true })
            return;

        if (!_byProvider.TryGetValue(provider, out var part) || !ReferenceEquals(part.Source, source))
        {
            part = new Part
            {
                Provider = provider,
                Url = url,
                Source = source,
                Address = source.OriginalString,
                DownloadKey = DownloadKeyOf(source),
            };
            _byProvider[provider] = part;
        }
        if (isMesh)
            part.IsMesh = true;
        if (renderer != null)
            part.Renderer = renderer;
        if (part.Pass == _pass)
            return;
        part.Pass = _pass;
        _parts.Add(part);
    }

    // The key the content service files a download of this address under: its hash for a cloud address, the
    // hash of the link for any other. Local addresses come over the session instead.
    private static string? DownloadKeyOf(Uri source)
    {
        try
        {
            if (source.Scheme == ContentHash.Scheme)
                return TransferRegistry.DownloadKey(ContentHash.ParseHash(source).ToLowerInvariant());
            if (source.Scheme == Uri.UriSchemeHttp || source.Scheme == Uri.UriSchemeHttps)
                return TransferRegistry.DownloadKey(ContentHash.FromString(source.ToString()));
        }
        catch (Exception)
        {
        }
        return null;
    }

    public void Evaluate(World world)
    {
        _downloads.Clear();
        world?.Session?.AssetTransferer?.CollectDownloads(_downloads);

        int wanted = 0, done = 0, missing = 0, broken = 0;
        long bytes = 0, known = 0;
        int sized = 0;
        bool moving = false, pending = false;

        foreach (var part in _parts)
        {
            // A cloud part that was missing and has arrived since loads again (LateCloudParts): no longer
            // failed, it is waited for like any other.
            if (part.Failed && part.Url.LoadFailure == null)
                part.Failed = part.Broken = false;
            if (!part.Done && !part.Failed && !IsWanted(part))
                continue;
            wanted++;

            if (!part.Done && !part.Failed)
            {
                if (part.Url.LoadFailure is { } why)
                {
                    part.Failed = true;
                    // A fetch that came back empty never arrived; anything else was read and refused.
                    part.Broken = !why.StartsWith("No ", StringComparison.Ordinal);
                }
                else if (!AssetReadiness.IsPending(part.Provider))
                {
                    part.Done = true;
                    if (part.Total > 0)
                        part.Received = part.Total;
                }
                else
                {
                    Measure(part);
                    pending = true;
                    moving |= part.Moving;
                }
            }

            if (part.Done || part.Failed)
                done++;
            if (part.Failed && part.IsMesh)
            {
                if (part.Broken) broken++;
                else missing++;
            }
            bytes += part.Received;
            if (part.Total > 0)
            {
                known += part.Total;
                sized++;
            }
        }

        long fallback = sized > 0 ? System.Math.Max(known / sized, 1) : UnknownWeight;
        double weightSum = 0, doneSum = 0;
        foreach (var part in _parts)
        {
            if (!part.Done && !part.Failed && !IsWanted(part))
                continue;
            double weight = part.Total > 0 ? part.Total : fallback;
            double share = part.Done || part.Failed ? 1.0
                : part.Moving && part.Total > 0 ? TransferShare * System.Math.Clamp((double)part.Received / part.Total, 0, 1)
                : part.Total > 0 ? ArrivedShare
                : 0.0;
            weightSum += weight;
            doneSum += weight * share;
        }

        int gates = 0, open = 0;
        foreach (var material in _materials)
        {
            if (material.IsDestroyed)
                continue;
            gates++;
            if (material.DisplayReady)
                open++;
        }
        foreach (var skinned in _skinned)
        {
            if (skinned.IsDestroyed || !skinned.Enabled.Value || !skinned.Slot.IsActive
                || (skinned.MeshAsset.Target == null && skinned.Vertices.Count == 0))
                continue;
            gates++;
            // No platform side on this machine (a headless peer) builds nothing, so there is nothing to wait on.
            if (skinned.Hook == null || skinned.HookBindingComplete)
                open++;
        }

        Parts = wanted;
        PartsDone = done;
        MissingMeshes = missing;
        BrokenMeshes = broken;
        BytesReceived = bytes;
        AssetsIn = !pending;

        float assets = weightSum > 0 ? (float)(doneSum / weightSum) : 1f;
        float built = gates > 0 ? (float)open / gates : 1f;
        float progress = 0.9f * assets + 0.1f * built;

        if (missing + broken > 0)
            State = Stage.Failed;
        else if (pending)
            State = moving ? Stage.Downloading : Stage.Preparing;
        else if (open < gates)
            State = Stage.Building;
        else
            State = Stage.Ready;

        Progress = State == Stage.Ready ? 1f : System.Math.Min(progress, 0.99f);
    }

    // Something references it, or a renderer that would draw it is on: its platform side pins the mesh only
    // once it runs, and the first look at a just-arrived avatar can come before that. A renderer with no
    // platform side (a headless peer) never pins anything, so it waits for nothing.
    private static bool IsWanted(Part part)
    {
        if (part.Provider.AssetReferenceCount > 0)
            return true;
        var renderer = part.Renderer;
        return renderer != null && !renderer.IsDestroyed && renderer.Hook != null && renderer.Enabled.Value
            && renderer.Slot.IsActive;
    }

    // How far its bytes are. A texture comes as its variants and their sidecar, all filed under its address
    // with a suffix, and counts as one; a size, once learned, is kept, so a finished file dropping off the
    // list leaves the part arrived rather than empty.
    private void Measure(Part part)
    {
        long received = 0, total = 0;
        bool listed = false;
        string address = part.Address;
        foreach (var download in _downloads)
        {
            var other = download.Address;
            if (other == null || !other.StartsWith(address, StringComparison.Ordinal)
                || (other.Length != address.Length && other[address.Length] != '~'))
                continue;
            listed = true;
            received += download.Received;
            total += download.Total;
        }
        if (part.DownloadKey != null && TransferRegistry.TryGetDownload(part.DownloadKey, out long cloudReceived, out long cloudTotal))
        {
            listed = true;
            received += cloudReceived;
            total += cloudTotal;
        }

        part.Moving = listed;
        if (total > part.Total)
            part.Total = total;
        if (received > part.Received)
            part.Received = System.Math.Min(received, part.Total > 0 ? part.Total : received);
        if (!listed && part.Total > 0)
            part.Received = part.Total;
    }
}

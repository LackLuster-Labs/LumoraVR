// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core;

// What the inventory and its sync need from the service, and nothing more. The sync runs against this,
// so a check can stand something in memory in for the service; LumoraInventoryService is the real one.
public interface IInventoryService
{
    bool SignedIn { get; }

    // The signed in account when it is already known here, without asking anyone.
    string? Account { get; }

    // The signed in account, asking the service when it is not known yet. Null when that fails.
    Task<string?> ResolveAccountAsync(CancellationToken ct);

    // An account signed in or out.
    event Action? AccountChanged;

    // The service answers again after a spell without a connection.
    event Action? BackOnline;

    // ownerId null is the account's own inventory, else a group's or a sharer's.
    Task<ApiResponse<FolderContents>> ListAsync(string folderId, string? ownerId = null);

    Task<ApiResponse<FolderContents>> SearchAsync(string query, string? ownerId = null);

    // Which of these blobs the store does not have.
    Task<ApiResponse<List<string>>> MissingAsync(IReadOnlyList<string> hashes, CancellationToken ct);

    // A blob stored under its own hash, nothing sent when the store has it already. Progress counts bytes.
    Task<ApiResponse> UploadAsync(string hash, byte[] bytes, string extension, IProgress<long>? sent, CancellationToken ct);

    // The variants registered for a blob, an empty list when there are none.
    Task<ApiResponse<List<VariantInfo>>> ListVariantsAsync(string hash, CancellationToken ct);

    Task<ApiResponse> RegisterVariantAsync(string baseHash, string variantId, string resultHash, long size, CancellationToken ct);

    // The item made or replaced whole under the id this machine chose for it, in ownerId's inventory (null for
    // the account's own).
    Task<ApiResponse> PutItemAsync(string itemId, string name, string kind, string folderId, string fileHash, string? thumbnailHash,
        long fileBytes, string? worldMode, IReadOnlyList<AssetManifestEntry> manifest, string? ownerId, CancellationToken ct);

    Task<ApiResponse> DeleteItemAsync(string itemId);
}

// The service the inventory really talks to, through the client the engine signs in with.
public sealed class LumoraInventoryService : IInventoryService
{
    private readonly LumoraClient _client;

    public LumoraInventoryService(LumoraClient client)
    {
        _client = client;
        client.Authenticated += _ => AccountChanged?.Invoke();
        client.SignedOut += () => AccountChanged?.Invoke();
        Connectivity.StatusChanged += online =>
        {
            if (online)
                BackOnline?.Invoke();
        };
    }

    public event Action? AccountChanged;
    public event Action? BackOnline;

    public bool SignedIn => _client.IsAuthenticated;
    public string? Account => _client.IsAuthenticated ? _client.AccountUserId : null;

    public async Task<string?> ResolveAccountAsync(CancellationToken ct)
    {
        if (!_client.IsAuthenticated)
            return null;
        var id = await _client.ResolveAccountUserId().ConfigureAwait(false);
        return id.Success && !string.IsNullOrEmpty(id.Data) ? id.Data : null;
    }

    public Task<ApiResponse<FolderContents>> ListAsync(string folderId, string? ownerId = null) => _client.GetFolderContents(folderId, ownerId);

    public Task<ApiResponse<FolderContents>> SearchAsync(string query, string? ownerId = null) => _client.SearchInventory(query, ownerId);

    public Task<ApiResponse<List<string>>> MissingAsync(IReadOnlyList<string> hashes, CancellationToken ct) => _client.MissingContent(hashes, ct);

    public async Task<ApiResponse> UploadAsync(string hash, byte[] bytes, string extension, IProgress<long>? sent, CancellationToken ct)
    {
        var progress = sent == null ? null : new Bytes(sent);
        string? extensionOrNull = string.IsNullOrEmpty(extension) ? null : extension;
        return await _client.UploadContent(bytes, hash, extensionOrNull, extensionOrNull == null ? null : MimeTypes.FromExtension(extensionOrNull),
            progress, ct).ConfigureAwait(false);
    }

    public Task<ApiResponse<List<VariantInfo>>> ListVariantsAsync(string hash, CancellationToken ct) => _client.ListVariants(hash, ct);

    public async Task<ApiResponse> RegisterVariantAsync(string baseHash, string variantId, string resultHash, long size, CancellationToken ct)
        => await _client.RegisterVariant(baseHash, variantId, resultHash, size, ct).ConfigureAwait(false);

    public async Task<ApiResponse> PutItemAsync(string itemId, string name, string kind, string folderId, string fileHash, string? thumbnailHash,
        long fileBytes, string? worldMode, IReadOnlyList<AssetManifestEntry> manifest, string? ownerId, CancellationToken ct)
        => await _client.UpsertInventoryItem(itemId, fileHash, name, kind, folderId, thumbnailHash, fileBytes, worldMode, manifest, ct, ownerId).ConfigureAwait(false);

    public Task<ApiResponse> DeleteItemAsync(string itemId) => _client.DeleteInventoryItem(itemId);

    private sealed class Bytes : IProgress<TransferProgress>
    {
        private readonly IProgress<long> _sent;
        public Bytes(IProgress<long> sent) => _sent = sent;
        public void Report(TransferProgress value) => _sent.Report(value.TransferredBytes);
    }
}

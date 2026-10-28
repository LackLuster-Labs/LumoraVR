// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// handles all the cloud shit with compression and parallel uploads

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Cloud.Cdn;

public sealed class LumoraClient : IDisposable, IProtectionRoutes
{
    private readonly HttpClient _api;
    private readonly HttpClient _content;
    private readonly JsonSerializerOptions _json;
    private readonly string _deviceId;
    private Session? _session;

    // Per-login ACCOUNT keypair for join verification. Generated fresh each sign-in, private half
    // stays in memory here, public half is published to the backend so a game host can fetch it and verify
    // we are who we claim. Cleared on sign-out. -xlinka
    private RSA? _accountKey;
    private string? _accountUserId;
    private string? _accountSessionId;
    // The build identity the account key went out with, so a host's fetch can be refused for another build's session.
    private string? _publishedBuild;

    public const int DefaultChunkSize = 4 * 1024 * 1024; // 4MB chunks

    public static TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public static TimeSpan ContentTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public static bool EnableCompression { get; set; } = false;

    public string DeviceId => _deviceId;
    public Session? CurrentSession => _session;
    public bool IsAuthenticated => _session != null && !string.IsNullOrEmpty(_session.Token);

    // JWT jti once signed in, else null
    public string? AccountSessionId => _accountSessionId;
    public string? AccountUserId => _accountUserId;
    // DER SubjectPublicKeyInfo, else null
    public byte[]? AccountPublicKey => _accountKey?.ExportSubjectPublicKeyInfo();
    public bool HasAccountIdentity => _accountKey != null && !string.IsNullOrEmpty(_accountUserId) && !string.IsNullOrEmpty(_accountSessionId);

    // This game's build identity, reported with the account key; null until it is known. The engine sets it.
    public Func<string?>? BuildProvider { get; set; }

    // signs a host's join challenge nonce
    public byte[]? SignWithAccountKey(byte[] nonce)
        => _accountKey?.SignData(nonce, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    public event Action<Session>? Authenticated;
    public event Action? SignedOut;
    // After a sign in has its account key published (or failed to), and after a sign out. Authenticated
    // fires before the key exists, so anything that proves the account to a host waits for this one.
    public event Action? AccountIdentityChanged;

    public LumoraClient(string deviceId, string appName = "LumoraVR", string version = "0.1.0")
    {
        _deviceId = deviceId;
        var userAgent = new ProductInfoHeaderValue(appName, version);

        var apiHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        _api = new HttpClient(apiHandler) { Timeout = ApiTimeout };
        _api.DefaultRequestHeaders.UserAgent.Add(userAgent);
        _api.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        _api.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        _api.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));

        var contentHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        _content = new HttpClient(contentHandler) { Timeout = ContentTimeout };
        _content.DefaultRequestHeaders.UserAgent.Add(userAgent);
        _content.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        _content.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public void Dispose()
    {
        _accountKey?.Dispose();
        _api.Dispose();
        _content.Dispose();
    }

    #region Auth

    public async Task<ApiResponse<Session>> SignIn(string username, string password, bool remember = false, string? twoFactorCode = null)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<Session>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        object payload = string.IsNullOrEmpty(twoFactorCode)
            ? new { Username = username, Password = password, Remember = remember, DeviceId = _deviceId }
            : new { Username = username, Password = password, Remember = remember, DeviceId = _deviceId, TwoFactorCode = twoFactorCode };

        var result = await PostAsync<Session>($"{ServiceConfig.Current.ApiBase}/api/user/login", payload);
        if (result.Failed)
            return ApiResponse<Session>.Fail(result.Status, SignInError(result.Status, !string.IsNullOrEmpty(twoFactorCode)));

        if (result.Success && result.Data != null)
        {
            ApplySession(result.Data);
            await EstablishAccountIdentityAsync(result.Data);
        }

        return result;
    }

    // Login failures use fixed wording: neither response bodies nor account-specific details belong
    // in the dialog. The login endpoint uses 403 to request a two-factor code after checking credentials.
    private static string SignInError(HttpStatusCode status, bool sentCode) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized => sentCode
            ? "Invalid login credentials or two-factor code."
            : "Invalid login credentials.",
        HttpStatusCode.Forbidden => "Enter your two-factor code.",
        HttpStatusCode.Locked => "This account is suspended.",
        HttpStatusCode.TooManyRequests => "Too many sign-in attempts. Please wait and try again.",
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "Sign-in timed out. Please try again.",
        HttpStatusCode.ServiceUnavailable => "Unable to reach the sign-in service. Please try again.",
        _ when (int)status >= 500 => "Unable to sign in right now. Please try again later.",
        _ => "Unable to sign in. Please try again."
    };

    public async Task<ApiResponse<Session>> SignInWithToken(string userId, string token)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<Session>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        var payload = new { UserId = userId, Token = token, DeviceId = _deviceId };
        var result = await PostAsync<Session>($"{ServiceConfig.Current.ApiBase}/api/user/token", payload);

        if (result.Success && result.Data != null)
        {
            ApplySession(result.Data);
            await EstablishAccountIdentityAsync(result.Data);
        }

        return result;
    }

    public async Task<ApiResponse> SignOut()
    {
        if (!IsAuthenticated)
            return ApiResponse.Ok();

        if (!Connectivity.IsOnline)
        {
            ClearSession();
            return ApiResponse.Ok("Signed out locally (offline)");
        }

        var result = await PostAsync($"{ServiceConfig.Current.ApiBase}/api/user/logout", null);
        ClearSession();
        return result;
    }

    public void SetSession(Session session) => ApplySession(session);

    // A sign in kept from before, applied without asking the accounts service: every service call checks
    // the token anyway, so a stale one comes back as a refusal there. The account id comes with it, so
    // nothing needs a round trip to know who is signed in. -xlinka
    public void RestoreSession(string userId, string token)
    {
        _accountUserId = userId;
        ApplySession(new Session { Token = token });
    }

    private void ApplySession(Session session)
    {
        _session = session;
        _api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        Authenticated?.Invoke(session);
    }

    private void ClearSession()
    {
        _session = null;
        _accountKey?.Dispose();
        _accountKey = null;
        _accountUserId = null;
        _accountSessionId = null;
        _publishedBuild = null;
        _api.DefaultRequestHeaders.Authorization = null;
        // Signing out takes the group card off this machine's nametag immediately rather than leaving the
        // last account's group hanging over whoever plays next. -xlinka
        SetRepresentedGroup(null);
        SignedOut?.Invoke();
        AccountIdentityChanged?.Invoke();
    }

    // Generate this login's account keypair and publish the public half so a game host can fetch it and
    // verify our account when we join. Best-effort: if it can't complete (no session id, offline, etc.) we
    // just have no account identity and join falls back to machine-key only (guest). -xlinka
    private async Task EstablishAccountIdentityAsync(Session session)
    {
        try
        {
            await PublishAccountKeyAsync(session);
        }
        finally
        {
            AccountIdentityChanged?.Invoke();
        }
    }

    private async Task PublishAccountKeyAsync(Session session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.SessionId))
                return; // backend didn't hand us a session id, can't bind a key to it

            var me = await GetCurrentUser();
            if (me.Failed || me.Data == null || string.IsNullOrEmpty(me.Data.Id))
                return;

            _accountKey?.Dispose();
            _accountKey = RSA.Create(2048);
            _accountUserId = me.Data.Id;
            _accountSessionId = session.SessionId;

            var publicKey = Convert.ToBase64String(_accountKey.ExportSubjectPublicKeyInfo());
            var build = BuildProvider?.Invoke();
            var publish = await PutAsync(
                $"{ServiceConfig.Current.ApiBase}/api/user/{_accountUserId}/rsa/{_accountSessionId}",
                new { PublicKey = publicKey, Build = build });
            _publishedBuild = publish.Failed ? null : build;

            if (publish.Failed)
            {
                // couldn't publish, so the key is useless to a host. Drop it so HasAccountIdentity is honest.
                _accountKey?.Dispose();
                _accountKey = null;
                _accountUserId = null;
                _accountSessionId = null;
            }
        }
        catch
        {
            _accountKey?.Dispose();
            _accountKey = null;
            _accountUserId = null;
            _accountSessionId = null;
        }

        // Pick up the group this account wears, so the very first world we join already carries the card.
        // Deliberately outside the try above: a failure here is not a reason to throw the account key away.
        try { await RefreshRepresentedGroupAsync(); } catch { /* no card is a fine outcome */ }
    }

    // The build identity changed (it became known after the sign in, or something was loaded into the game): the same
    // key goes out again with it. Nothing to do signed out or when the service already has this one.
    public async Task RepublishBuildAsync()
    {
        var key = _accountKey;
        string? userId = _accountUserId, sessionId = _accountSessionId;
        var build = BuildProvider?.Invoke();
        if (key == null || string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(build)
            || string.Equals(build, _publishedBuild, StringComparison.Ordinal))
            return;
        try
        {
            var publish = await PutAsync(
                $"{ServiceConfig.Current.ApiBase}/api/user/{userId}/rsa/{sessionId}",
                new { PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Build = build });
            if (!publish.Failed)
                _publishedBuild = build;
        }
        catch
        {
            // the next change tries again; a host's own handshake check still stands meanwhile
        }
    }

    // DER SubjectPublicKeyInfo bytes, or null with the service's code for why when it gave one ("email_not_verified":
    // it holds that account back from other people's worlds; "build_mismatch": the account reported other game files
    // than worldSessionId was listed with).
    public async Task<(byte[]? Key, string? Refusal)> GetSessionPublicKeyAsync(string userId, string sessionId, string? worldSessionId = null)
    {
        var query = string.IsNullOrEmpty(worldSessionId) ? string.Empty : "?world=" + Uri.EscapeDataString(worldSessionId);
        var result = await SendAsync<SessionKeyInfo>(_api, HttpMethod.Get,
            $"{ServiceConfig.Current.ApiBase}/api/user/{userId}/rsa/{sessionId}{query}", null, auth: false, CancellationToken.None);
        if (result.Failed || result.Data == null || string.IsNullOrEmpty(result.Data.PublicKey))
            return (null, result.Error);
        try { return (Convert.FromBase64String(result.Data.PublicKey), null); }
        catch { return (null, null); }
    }

    // Fetch a user's public record, including PLATFORM moderation ban status, so a game host can gate a
    // join. (World bans are separate and live host-side in BanManager.) Null if the call fails. -xlinka
    public Task<ApiResponse<PublicUserInfo>> GetPlatformUserAsync(string userId)
        => GetAsync<PublicUserInfo>($"{ServiceConfig.Current.ApiBase}/api/user/{userId}");

    #endregion

    #region User Profile

    public Task<ApiResponse<UserProfile>> GetCurrentUser()
        => GetAsync<UserProfile>($"{ServiceConfig.Current.ApiBase}/api/user/me");

    // Fetch another user's public profile (bio, status, badges, ban status) by id. -xlinka
    public Task<ApiResponse<PublicUserInfo>> GetPublicUser(string userId)
        => GetAsync<PublicUserInfo>($"{ServiceConfig.Current.ApiBase}/api/user/{userId}");

    // The settings that follow the account: all of them, and a merge that keeps the newer side of each key
    // and answers with what the account holds afterwards.
    public Task<ApiResponse<AccountSettingsResponse>> GetAccountSettings()
        => GetAsync<AccountSettingsResponse>($"{ServiceConfig.Current.ApiBase}/api/user/settings");

    public Task<ApiResponse<AccountSettingsResponse>> MergeAccountSettings(IReadOnlyList<AccountSettingEntry> entries)
        => PostAsync<AccountSettingsResponse>($"{ServiceConfig.Current.ApiBase}/api/user/settings", new { entries });

    // This client is running; the answer is how many are, by platform.
    public Task<ApiResponse<OnlineCounts>> ReportPresence(string clientId, string platform)
        => PostAsync<OnlineCounts>($"{ServiceConfig.Current.ApiBase}/api/presence", new { clientId, platform });

    public async Task<ApiResponse> UpdateProfile(string bio, string status)
    {
        if (!IsAuthenticated)
            return ApiResponse.Fail(HttpStatusCode.Unauthorized, "Not authenticated");

        var payload = new { Bio = bio, Status = status };
        return await PutAsync($"{ServiceConfig.Current.ApiBase}/api/user/profile", payload);
    }

    public async Task<ApiResponse> ChangePassword(string currentPassword, string newPassword)
    {
        if (!IsAuthenticated)
            return ApiResponse.Fail(HttpStatusCode.Unauthorized, "Not authenticated");

        var payload = new { CurrentPassword = currentPassword, NewPassword = newPassword };
        return await PostAsync($"{ServiceConfig.Current.ApiBase}/api/user/reset-password", payload);
    }

    public async Task<ApiResponse<TwoFactorSetup>> Enable2FA()
    {
        if (!IsAuthenticated)
            return ApiResponse<TwoFactorSetup>.Fail(HttpStatusCode.Unauthorized, "Not authenticated");

        return await PostAsync<TwoFactorSetup>($"{ServiceConfig.Current.ApiBase}/api/user/enable2fa", null);
    }

    public async Task<ApiResponse> Verify2FA(string code)
    {
        if (!IsAuthenticated)
            return ApiResponse.Fail(HttpStatusCode.Unauthorized, "Not authenticated");

        var payload = new { Code = code };
        return await PostAsync($"{ServiceConfig.Current.ApiBase}/api/user/verify2fa", payload);
    }

    public async Task<ApiResponse> Disable2FA(string code)
    {
        if (!IsAuthenticated)
            return ApiResponse.Fail(HttpStatusCode.Unauthorized, "Not authenticated");

        var payload = new { Code = code };
        return await PostAsync($"{ServiceConfig.Current.ApiBase}/api/user/disable2fa", payload);
    }

    // A new account. Nothing comes back to sign in with, so the caller signs in with what it sent. A refusal keeps the
    // body, so a caller can tell which field it was about.
    public Task<ApiResponse<UserProfile>> Register(string username, string email, string password)
        => SendAsync<UserProfile>(_api, HttpMethod.Post, $"{ServiceConfig.Current.ApiBase}/api/user/register",
            CreateJsonContent(new { Username = username, Email = email, Password = password }), auth: false, CancellationToken.None);

    // The confirmation mail again, to the address the signed-in account has. A refusal names why in Error: too_soon (with
    // retryAfter in the body), mail_unavailable or already_verified.
    public async Task<ApiResponse> ResendVerificationEmail()
    {
        var id = await ResolveUserIdAsync();
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        return await SendAsync<JsonElement>(_api, HttpMethod.Post,
            $"{ServiceConfig.Current.ApiBase}/api/user/resend-verification?userId={Uri.EscapeDataString(id.Data)}", null, auth: true,
            CancellationToken.None);
    }

    #endregion

    #region Inventory

    // Resolve our own user id for the inventory/asset calls. We learn it once at sign-in (cached as
    // _accountUserId), so normally this is free; only if that didn't run do we fall back to a single
    // /api/user/me round-trip and cache the result - instead of paying for that round-trip on EVERY call
    // like the old per-method copy-paste did. -xlinka
    private async Task<ApiResponse<string>> ResolveUserIdAsync()
    {
        if (!IsAuthenticated)
            return ApiResponse<string>.Fail(HttpStatusCode.Unauthorized, "Not authenticated");

        if (!string.IsNullOrEmpty(_accountUserId))
            return ApiResponse<string>.Ok(_accountUserId!);

        var me = await GetCurrentUser();
        if (me.Failed || me.Data == null || string.IsNullOrEmpty(me.Data.Id))
            return ApiResponse<string>.Fail(me.Status, me.Message);

        _accountUserId = me.Data.Id;
        return ApiResponse<string>.Ok(_accountUserId!);
    }

    public Task<ApiResponse<string>> ResolveAccountUserId() => ResolveUserIdAsync();

    public async Task<ApiResponse<UserQuotaResponse>> GetQuota()
    {
        var id = await ResolveUserIdAsync();
        if (id.Failed || id.Data == null)
            return ApiResponse<UserQuotaResponse>.Fail(id.Status, id.Message);

        return await GetAsync<UserQuotaResponse>($"{ServiceConfig.Current.ContentBase}/api/inventory/users/{id.Data}/quota");
    }
    // Makes the folder under the id this client chose, or renames it when it is there already, so asking
    // twice never makes two.
    public async Task<ApiResponse<FolderSummary>> UpsertFolder(string folderId, string name, string? parentId, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse<FolderSummary>.Fail(id.Status, id.Message);

        var payload = new { Name = name, ParentId = string.IsNullOrEmpty(parentId) ? InventoryRoot : parentId };
        var answer = await SendAsync<JsonElement>(_api, HttpMethod.Put,
            $"{InventoryOf(id.Data)}/folders/{Uri.EscapeDataString(folderId)}",
            CreateJsonContent(payload), auth: true, CancellationToken.None);
        return answer.Success ? ApiResponse<FolderSummary>.Ok(Read<FolderSummary>(answer.Data)!) : Refused<FolderSummary>(answer);
    }

    // The folder browsing shape the inventory screen works with: one folder's direct children, or a
    // search across the whole tree. "root" names the top of the inventory.
    public const string InventoryRoot = "root";

    // The game's own library, a group the services keep under this id. Every signed-in account finds its top as one
    // folder at the top of its own inventory; staff save into it.
    public const string InventoryLibrary = "G-00000000-0000-4000-8000-00000000f11b";

    // Every inventory call names whose inventory it is: this account's own when ownerId is empty, else a group's
    // or the account that shared something; the service decides what this account may do there.
    private async Task<ApiResponse<string>> OwnerOrSelfAsync(string? ownerId)
    {
        if (string.IsNullOrEmpty(ownerId))
            return await ResolveUserIdAsync();
        return IsAuthenticated
            ? ApiResponse<string>.Ok(ownerId!)
            : ApiResponse<string>.Fail(HttpStatusCode.Unauthorized, "Not authenticated");
    }

    private static string InventoryOf(string ownerId)
        => $"{ServiceConfig.Current.ContentBase}/api/inventory/users/{Uri.EscapeDataString(ownerId)}";

    public async Task<ApiResponse<FolderContents>> GetFolderContents(string? folderId, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse<FolderContents>.Fail(id.Status, id.Message);
        var folder = string.IsNullOrEmpty(folderId) ? InventoryRoot : folderId;
        return await GetAsync<FolderContents>($"{InventoryOf(id.Data)}/folders/{Uri.EscapeDataString(folder)}/contents");
    }

    public async Task<ApiResponse<FolderContents>> SearchInventory(string query, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse<FolderContents>.Fail(id.Status, id.Message);
        return await GetAsync<FolderContents>($"{InventoryOf(id.Data)}/search?q={Uri.EscapeDataString(query)}");
    }

    // A group's storage as this member sees it: the group's use and their own slice of it.
    public Task<ApiResponse<GroupInventoryQuota>> GetGroupInventoryQuota(string groupId)
        => GetAsync<GroupInventoryQuota>($"{InventoryOf(groupId)}/quota");

    // The item under the id this client chose, made or replaced whole, so sending it again after an answer
    // that never arrived changes nothing. A folder that is gone comes back as 404 with Error "folder", an id
    // another account holds as 409 with Error "id-taken", a save that does not fit as 413 with Error
    // "quota", a folder this account may only look at as 403 "read-only". -xlinka
    public async Task<ApiResponse<InventoryItemInfo>> UpsertInventoryItem(string itemId, string fileHash, string name, string kind,
        string? folderId, string? thumbnailHash, long fileBytes, string? worldMode, IReadOnlyList<AssetManifestEntry> manifest,
        CancellationToken ct = default, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse<InventoryItemInfo>.Fail(id.Status, id.Message);
        var payload = new
        {
            Name = name,
            Kind = kind,
            FolderId = string.IsNullOrEmpty(folderId) ? InventoryRoot : folderId,
            FileHash = fileHash,
            FileBytes = fileBytes,
            ThumbnailHash = thumbnailHash,
            WorldMode = worldMode,
            Manifest = manifest,
        };
        var answer = await SendAsync<JsonElement>(_api, HttpMethod.Put,
            $"{InventoryOf(id.Data)}/items/{Uri.EscapeDataString(itemId)}",
            CreateJsonContent(payload), auth: true, ct);
        return answer.Success ? ApiResponse<InventoryItemInfo>.Ok(Read<InventoryItemInfo>(answer.Data)!) : Refused<InventoryItemInfo>(answer);
    }

    public async Task<ApiResponse> RenameInventoryItem(string itemId, string name, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        var result = await PatchAsync<InventoryItemInfo>($"{InventoryOf(id.Data)}/items/{Uri.EscapeDataString(itemId)}", new { Name = name });
        return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Status, result.Message);
    }

    // A refusal keeps the service's code: "needs-approval" is a group builder, who asks instead.
    public async Task<ApiResponse> DeleteInventoryItem(string itemId, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        return await SendAsync<JsonElement>(_api, HttpMethod.Delete, $"{InventoryOf(id.Data)}/items/{Uri.EscapeDataString(itemId)}",
            null, auth: true, CancellationToken.None);
    }

    // One item of ownerId's, when this account reaches it; 403 whether it is hidden or missing.
    public Task<ApiResponse<InventoryItemInfo>> GetInventoryItem(string ownerId, string itemId)
        => SendAsync<InventoryItemInfo>(_api, HttpMethod.Get, $"{InventoryOf(ownerId)}/items/{Uri.EscapeDataString(itemId)}",
            null, auth: true, CancellationToken.None);

    // A name another folder beside it holds is 409 with Error "name-taken", the body naming that folder (folderId,
    // name); the same goes for MoveFolder and for a new folder through UpsertFolder.
    public async Task<ApiResponse> RenameFolder(string folderId, string name, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        return await SendAsync<JsonElement>(_api, HttpMethod.Post, $"{InventoryOf(id.Data)}/folders/{Uri.EscapeDataString(folderId)}/rename",
            CreateJsonContent(new { Name = name }), auth: true, CancellationToken.None);
    }

    public async Task<ApiResponse> MoveFolder(string folderId, string? targetFolderId, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        var target = string.IsNullOrEmpty(targetFolderId) || targetFolderId == InventoryRoot ? null : targetFolderId;
        return await SendAsync<JsonElement>(_api, HttpMethod.Post, $"{InventoryOf(id.Data)}/folders/{Uri.EscapeDataString(folderId)}/move",
            CreateJsonContent(new { TargetFolderId = target }), auth: true, CancellationToken.None);
    }

    // A move that merges the folder into the one of the same name in the target, or a plain move when there is none.
    // Answers the folder that holds everything now and each folder that went into another. A folder that would go
    // with a grant on it is 409 "shared", one with a delete request waiting 409 "delete-requested", either naming
    // that folder (folderId, name). -xlinka
    public async Task<ApiResponse<FolderMergeResult>> MergeFolder(string folderId, string? targetFolderId, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse<FolderMergeResult>.Fail(id.Status, id.Message);
        var target = string.IsNullOrEmpty(targetFolderId) || targetFolderId == InventoryRoot ? null : targetFolderId;
        var answer = await SendAsync<JsonElement>(_api, HttpMethod.Post, $"{InventoryOf(id.Data)}/folders/{Uri.EscapeDataString(folderId)}/merge",
            CreateJsonContent(new { TargetFolderId = target }), auth: true, CancellationToken.None);
        return answer.Success ? ApiResponse<FolderMergeResult>.Ok(Read<FolderMergeResult>(answer.Data) ?? new FolderMergeResult()) : Refused<FolderMergeResult>(answer);
    }

    public async Task<ApiResponse> DeleteFolder(string folderId, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        return await SendAsync<JsonElement>(_api, HttpMethod.Delete, $"{InventoryOf(id.Data)}/folders/{Uri.EscapeDataString(folderId)}",
            null, auth: true, CancellationToken.None);
    }

    // DELETE REQUESTS: a group's builders ask, its moderators and up decide.

    // kind is "item" or "folder". Asking again for the same thing answers the request already waiting.
    public Task<ApiResponse<InventoryDeleteRequest>> RequestInventoryDelete(string groupId, string kind, string targetId)
        => SendAsync<InventoryDeleteRequest>(_api, HttpMethod.Post, $"{InventoryOf(groupId)}/delete-requests",
            CreateJsonContent(new { Kind = kind, Id = targetId }), auth: true, CancellationToken.None);

    // Every waiting request for a moderator and up; a builder's own for a builder.
    public Task<ApiResponse<List<InventoryDeleteRequest>>> GetInventoryDeleteRequests(string groupId)
        => SendAsync<List<InventoryDeleteRequest>>(_api, HttpMethod.Get, $"{InventoryOf(groupId)}/delete-requests",
            null, auth: true, CancellationToken.None);

    public Task<ApiResponse<JsonElement>> ApproveInventoryDelete(string groupId, string requestId)
        => SendAsync<JsonElement>(_api, HttpMethod.Post, $"{InventoryOf(groupId)}/delete-requests/{Uri.EscapeDataString(requestId)}/approve",
            CreateJsonContent(new { }), auth: true, CancellationToken.None);

    // A moderator's deny, or the asker taking it back.
    public Task<ApiResponse<JsonElement>> DropInventoryDeleteRequest(string groupId, string requestId)
        => SendAsync<JsonElement>(_api, HttpMethod.Delete, $"{InventoryOf(groupId)}/delete-requests/{Uri.EscapeDataString(requestId)}",
            null, auth: true, CancellationToken.None);

    public async Task<ApiResponse> MoveItemToFolder(string itemId, string? targetFolderId, string? ownerId = null)
    {
        var id = await OwnerOrSelfAsync(ownerId);
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        var target = string.IsNullOrEmpty(targetFolderId) || targetFolderId == InventoryRoot ? null : targetFolderId;
        return await PostAsync($"{InventoryOf(id.Data)}/items/{Uri.EscapeDataString(itemId)}/move",
            new { TargetFolderId = target });
    }

    // SHARING

    // Who can reach one folder or item of ownerId's, for an account allowed to share it.
    public Task<ApiResponse<InventoryShareListing>> GetInventoryShares(string ownerId, string targetId)
        => SendAsync<InventoryShareListing>(_api, HttpMethod.Get, $"{InventoryOf(ownerId)}/shares/{Uri.EscapeDataString(targetId)}",
            null, auth: true, CancellationToken.None);

    // kind "user" or "group" with that account's or group's id, or "public" for anyone holding the link; access
    // "read" or "write" (the link is always read). Sharing again with the same one changes its access.
    public Task<ApiResponse<InventoryShareListing>> SetInventoryShare(string ownerId, string targetId, string kind, string? granteeId, string access)
        => SendAsync<InventoryShareListing>(_api, HttpMethod.Put, $"{InventoryOf(ownerId)}/shares/{Uri.EscapeDataString(targetId)}",
            CreateJsonContent(new { Kind = kind, Id = granteeId ?? string.Empty, Access = access }), auth: true, CancellationToken.None);

    // One grant taken back, or with no kind every grant on it.
    public Task<ApiResponse<InventoryShareListing>> RemoveInventoryShare(string ownerId, string targetId, string? kind = null, string? granteeId = null)
    {
        var url = $"{InventoryOf(ownerId)}/shares/{Uri.EscapeDataString(targetId)}";
        if (!string.IsNullOrEmpty(kind))
            url += $"?kind={Uri.EscapeDataString(kind)}&id={Uri.EscapeDataString(granteeId ?? string.Empty)}";
        return SendAsync<InventoryShareListing>(_api, HttpMethod.Delete, url, null, auth: true, CancellationToken.None);
    }

    // Something shared with this account by name, taken out of its inventory. Group shares stay.
    public async Task<ApiResponse> DeclineInventoryShare(string shareId)
    {
        var id = await ResolveUserIdAsync();
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        return await DeleteAsync($"{InventoryOf(id.Data)}/incoming/{Uri.EscapeDataString(shareId)}");
    }

    // A link to someone's shared folder or item, kept in this account's own folder. kind is "folder" or "item".
    public async Task<ApiResponse> AddInventoryLink(string targetOwnerId, string kind, string targetId, string? folderId, string? name = null)
    {
        var id = await ResolveUserIdAsync();
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        var answer = await SendAsync<JsonElement>(_api, HttpMethod.Post, $"{InventoryOf(id.Data)}/links",
            CreateJsonContent(new
            {
                OwnerId = targetOwnerId,
                Kind = kind,
                Id = targetId,
                FolderId = string.IsNullOrEmpty(folderId) ? InventoryRoot : folderId,
                Name = name,
            }), auth: true, CancellationToken.None);
        return answer.Success ? ApiResponse.Ok() : Refused<object>(answer);
    }

    // A kept link renamed or moved; either may be null to leave it.
    public async Task<ApiResponse> UpdateInventoryLink(string linkId, string? name, string? folderId)
    {
        var id = await ResolveUserIdAsync();
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        var result = await PatchAsync<JsonElement>($"{InventoryOf(id.Data)}/links/{Uri.EscapeDataString(linkId)}",
            new { Name = name, FolderId = folderId });
        return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Status, result.Message);
    }

    public async Task<ApiResponse> DeleteInventoryLink(string linkId)
    {
        var id = await ResolveUserIdAsync();
        if (id.Failed || id.Data == null)
            return ApiResponse.Fail(id.Status, id.Message);
        return await DeleteAsync($"{InventoryOf(id.Data)}/links/{Uri.EscapeDataString(linkId)}");
    }

    #endregion

    #region Variants

    public const string WorkerKeyHeader = "X-Lumora-Worker-Key";

    // Every variant registered for a blob; an empty list when there are none. Anyone may ask.
    public Task<ApiResponse<List<VariantInfo>>> ListVariants(string hash) => ListVariants(hash, CancellationToken.None);

    public async Task<ApiResponse<List<VariantInfo>>> ListVariants(string hash, CancellationToken ct)
    {
        var listed = await SendAsync<List<VariantInfo>>(_content, HttpMethod.Get,
            $"{ServiceConfig.Current.ContentBase}/api/variants/{Uri.EscapeDataString(hash)}", null, auth: false, ct);
        return listed.Success && listed.Data == null ? ApiResponse<List<VariantInfo>>.Ok(new List<VariantInfo>()) : listed;
    }

    // Records that a variant of a blob is another stored blob, made by this client. Both blobs have to be
    // stored, and only whoever first uploaded the base blob may say what its variants are.
    public Task<ApiResponse> RegisterVariant(string hash, string variantId, string resultHash, long size)
        => RegisterVariant(hash, variantId, resultHash, size, CancellationToken.None);

    public async Task<ApiResponse> RegisterVariant(string hash, string variantId, string resultHash, long size, CancellationToken ct)
    {
        if (!IsAuthenticated)
            return ApiResponse.Fail(HttpStatusCode.Unauthorized, "Not authenticated");
        return await SendAsync<JsonElement>(_api, HttpMethod.Put,
            $"{ServiceConfig.Current.ContentBase}/api/variants/{Uri.EscapeDataString(hash)}/{Uri.EscapeDataString(variantId)}",
            CreateJsonContent(new { ResultHash = resultHash, Size = size }), auth: true, ct);
    }

    // A raw blob into the store under the system owner, by worker key. The publisher uses it for the
    // engine's built-in assets.
    public async Task<ApiResponse<string>> WorkerStoreBlob(string workerKey, byte[] bytes, string extension)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{ServiceConfig.Current.ContentBase}/api/variants/blob?extension={Uri.EscapeDataString(extension)}");
            request.Headers.Add(WorkerKeyHeader, workerKey);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await _content.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return ApiResponse<string>.Fail(response.StatusCode, ErrorMessage(body));
            using var doc = JsonDocument.Parse(body);
            var hash = doc.RootElement.TryGetProperty("hash", out var h) ? h.GetString() : null;
            return string.IsNullOrEmpty(hash)
                ? ApiResponse<string>.Fail(HttpStatusCode.InternalServerError, "No hash in the answer")
                : ApiResponse<string>.Ok(hash!);
        }
        catch (Exception ex)
        {
            return ApiResponse<string>.Fail(HttpStatusCode.ServiceUnavailable, ex.Message);
        }
    }

    #endregion

    #region Protection

    public Task<ApiResponse<ProtectionKeySet>> GetProtectionKeys(string userId, CancellationToken ct = default)
        => SendAsync<ProtectionKeySet>(_api, HttpMethod.Get,
            $"{ServiceConfig.Current.ApiBase}/api/users/{Uri.EscapeDataString(userId)}/protection-keys", null, auth: false, ct);

    public Task<ApiResponse<SealGrant>> RequestSeal(string itemId, int rules, CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return Task.FromResult(ApiResponse<SealGrant>.Fail(HttpStatusCode.Unauthorized, "Not authenticated"));
        return SendAsync<SealGrant>(_api, HttpMethod.Post, $"{ServiceConfig.Current.ApiBase}/api/protection/seal",
            CreateJsonContent(new { ItemId = itemId, Rules = rules }), auth: true, ct);
    }

    #endregion

    #region Groups

    // biggest groups first; optional name search
    public Task<ApiResponse<List<GroupInfo>>> GetGroups(string? query = null, int skip = 0, int take = 25)
    {
        var url = $"{ServiceConfig.Current.ApiBase}/api/groups?skip={skip}&take={take}";
        if (!string.IsNullOrWhiteSpace(query))
            url += $"&query={Uri.EscapeDataString(query)}";
        return GetAsync<List<GroupInfo>>(url);
    }

    public Task<ApiResponse<List<GroupInfo>>> GetMyGroups()
        => GetAsync<List<GroupInfo>>($"{ServiceConfig.Current.ApiBase}/api/groups/mine");

    // MyRole is null if the caller isn't a member
    public Task<ApiResponse<GroupInfo>> GetGroup(string groupId)
        => GetAsync<GroupInfo>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}");

    // visibility: Public | Private | Hidden; needs a free group allocation
    public async Task<ApiResponse<GroupInfo>> CreateGroup(string name, string description = "", string visibility = "Public")
    {
        if (!IsAuthenticated)
            return ApiResponse<GroupInfo>.Fail(HttpStatusCode.Unauthorized, "Not authenticated");
        var payload = new { Name = name, Description = description, Visibility = visibility };
        return await PostAsync<GroupInfo>($"{ServiceConfig.Current.ApiBase}/api/groups", payload);
    }

    // Public joins outright, private files a request, hidden needs an invite. An invite to either lets the
    // caller straight in, so the answer says which of the two happened rather than the page guessing from
    // the group's visibility. Accepting an invite IS this call. -xlinka
    public Task<ApiResponse<GroupJoinResult>> JoinGroup(string groupId)
        => PostAsync<GroupJoinResult>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/join", null);

    // owners must transfer or delete the group instead of leaving
    public Task<ApiResponse> LeaveGroup(string groupId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/membership");

    public Task<ApiResponse<List<GroupMemberInfo>>> GetGroupMembers(string groupId)
        => GetAsync<List<GroupMemberInfo>>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/members");

    // Admin+; role is one rung below the caller: Moderator | Builder | Member (the Owner may also set Admin).
    // Owner is not settable here, transfer is its own route.
    public Task<ApiResponse> SetGroupRole(string groupId, string userId, string role)
        => PostAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/members/{userId}/role", new { Role = role });

    // moderator+ only
    public Task<ApiResponse> KickGroupMember(string groupId, string userId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/members/{userId}");

    // moderator+ only
    public Task<ApiResponse<List<GroupJoinRequestInfo>>> GetGroupRequests(string groupId)
        => GetAsync<List<GroupJoinRequestInfo>>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/requests");

    // moderator+ only
    public Task<ApiResponse> ApproveGroupRequest(string groupId, string userId)
        => PostAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/requests/{userId}/approve", null);

    // moderator+ only
    public Task<ApiResponse> DenyGroupRequest(string groupId, string userId)
        => PostAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/requests/{userId}/deny", null);

    // owner only
    public Task<ApiResponse> TransferGroup(string groupId, string newOwnerUserId)
        => PostAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/transfer/{newOwnerUserId}", null);

    // owner only
    public Task<ApiResponse> DeleteGroup(string groupId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}");

    // Admin+. Send only what changed: every field is optional server-side and a null is left alone, so a
    // patch built from the edit form never has to carry the values nobody touched. -xlinka
    public Task<ApiResponse<GroupInfo>> UpdateGroup(string groupId, GroupPatch patch)
        => PatchAsync<GroupInfo>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}", patch);

    // Admin+
    public Task<ApiResponse<List<GroupBanInfo>>> GetGroupBans(string groupId)
        => GetAsync<List<GroupBanInfo>>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/bans");

    // Admin+; also drops the membership, the join request and any invite in the same call
    public Task<ApiResponse> BanGroupMember(string groupId, string userId, string? reason = null)
        => PostAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/bans/{userId}", new { Reason = reason ?? "" });

    // Admin+
    public Task<ApiResponse> UnbanGroupMember(string groupId, string userId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/bans/{userId}");

    // Admin+; the sum of every slice has to stay inside the pool or this comes back 409
    public Task<ApiResponse> SetGroupMemberStorage(string groupId, string userId, long bytes)
        => PutAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/members/{userId}/storage", new { Bytes = bytes });

    // The caller's own pool as an owner, with every owned group's share of it. No tier and no groups is a
    // total of 0 and an empty list, not a failure.
    public Task<ApiResponse<GroupStorageInfo>> GetGroupStorage()
        => GetAsync<GroupStorageInfo>($"{ServiceConfig.Current.ApiBase}/api/groups/storage");

    // Owner only; null puts the group back on an even share. 409 when the pins would pass the pool, the share
    // would drop under what members hold, or the pool is locked and this raises it.
    public Task<ApiResponse> SetGroupPool(string groupId, long? bytes)
        => PutAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/pool", new { Bytes = bytes });

    // moderator+ only
    public Task<ApiResponse<List<GroupInviteInfo>>> GetGroupInvites(string groupId)
        => GetAsync<List<GroupInviteInfo>>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/invites");

    // moderator+ only
    public Task<ApiResponse> InviteToGroup(string groupId, string userId)
        => PostAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/invites/{userId}", null);

    // Moderator+ withdrawing someone else's, or the invited user declining their own.
    public Task<ApiResponse> WithdrawGroupInvite(string groupId, string userId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/invites/{userId}");

    // A different shape from a group's own invite list: this one is about the groups, that one is about
    // the people.
    public Task<ApiResponse<List<GroupInviteSummary>>> GetMyGroupInvites()
        => GetAsync<List<GroupInviteSummary>>($"{ServiceConfig.Current.ApiBase}/api/groups/invites/mine");

    // members only; upcoming drops the ones that already ended
    public Task<ApiResponse<List<GroupEventInfo>>> GetGroupEvents(string groupId, bool upcoming = true)
        => GetAsync<List<GroupEventInfo>>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/events?upcoming={(upcoming ? "true" : "false")}");

    // builder+ only
    public Task<ApiResponse<GroupEventInfo>> CreateGroupEvent(string groupId, string title, string description,
        string worldName, DateTime startsAt, DateTime? endsAt = null)
    {
        var payload = new
        {
            Title = title,
            Description = description,
            WorldName = worldName,
            StartsAt = startsAt,
            EndsAt = endsAt,
        };
        return PostAsync<GroupEventInfo>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/events", payload);
    }

    // the event's host, or moderator+
    public Task<ApiResponse> DeleteGroupEvent(string groupId, string eventId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/events/{eventId}");

    // members only, newest first
    public Task<ApiResponse<List<GroupAnnouncementInfo>>> GetGroupAnnouncements(string groupId, int take = 25)
        => GetAsync<List<GroupAnnouncementInfo>>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/announcements?take={take}");

    // builder+ only
    public Task<ApiResponse<GroupAnnouncementInfo>> PostGroupAnnouncement(string groupId, string text)
        => PostAsync<GroupAnnouncementInfo>($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/announcements", new { Text = text });

    // the author, or moderator+
    public Task<ApiResponse> DeleteGroupAnnouncement(string groupId, string announcementId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/announcements/{announcementId}");

    // REPRESENTATION
    //
    // The group a user wears. The account service stores one id per account; what the nametag needs is the
    // resolved card (tag, colour, icon, the wearer's role), and the only place that comes back assembled is
    // the caller's own public profile - so a successful represent call is followed by a profile read rather
    // than by guessing the card from whatever list row was on screen. -xlinka

    public RepresentedGroupInfo? RepresentedGroup { get; private set; }

    public event Action<RepresentedGroupInfo?>? RepresentedGroupChanged;

    // member only
    public async Task<ApiResponse> RepresentGroup(string groupId)
    {
        if (!IsAuthenticated)
            return ApiResponse.Fail(HttpStatusCode.Unauthorized, "Not authenticated");
        var result = await PutAsync($"{ServiceConfig.Current.ApiBase}/api/groups/{groupId}/represent", null);
        if (result.Success)
            await RefreshRepresentedGroupAsync();
        return result;
    }

    public async Task<ApiResponse> ClearRepresentedGroup()
    {
        if (!IsAuthenticated)
            return ApiResponse.Fail(HttpStatusCode.Unauthorized, "Not authenticated");
        var result = await DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/groups/represent");
        if (result.Success)
            await RefreshRepresentedGroupAsync();
        return result;
    }

    // Re-read the card off our own public profile. Best effort: a failed read leaves the last known card
    // alone rather than blanking somebody's nametag because one request timed out.
    public async Task RefreshRepresentedGroupAsync()
    {
        var id = await ResolveUserIdAsync();
        if (id.Failed || string.IsNullOrEmpty(id.Data))
            return;

        var me = await GetPublicUser(id.Data!);
        if (me.Failed || me.Data == null)
            return;

        SetRepresentedGroup(me.Data.RepresentedGroup);
    }

    private void SetRepresentedGroup(RepresentedGroupInfo? group)
    {
        var current = RepresentedGroup;
        if (current == null && group == null)
            return;
        if (current != null && group != null && current == group)
            return; // records compare by value, so an unchanged profile read raises nothing
        RepresentedGroup = group;
        RepresentedGroupChanged?.Invoke(group);
    }

    #endregion

    #region Social

    // Profiles, friends, requests, blocks, notes, presence and chat. Every route answers 400, 403, 404 or 409
    // with { "message": "..." } for a refusal, which lands in Message. Lists are never null on success.

    // Anonymous callers get the public view; signed in, it says where the caller stands with the account.
    public Task<ApiResponse<ProfileInfo>> GetProfile(string userId)
        => GetAsync<ProfileInfo>($"{ServiceConfig.Current.ApiBase}/api/user/{Uri.EscapeDataString(userId)}/profile");

    // A null field is left alone. The answer is the caller's own view, with their real StatusMode.
    public Task<ApiResponse<ProfileInfo>> UpdateMyProfile(ProfileUpdate update)
        => SendAsync<ProfileInfo>(_api, HttpMethod.Put, $"{ServiceConfig.Current.ApiBase}/api/user/me/profile",
            CreateJsonContent(update), auth: true, CancellationToken.None);

    // A prefix of the username, two characters at least; take is 1 to 50.
    public Task<ApiResponse<List<UserSearchResult>>> SearchUsers(string query, int skip = 0, int take = 20)
        => GetAsync<List<UserSearchResult>>(
            $"{ServiceConfig.Current.ApiBase}/api/users/search?q={Uri.EscapeDataString(query ?? "")}&skip={skip}&take={take}");

    // The caller's own private note on an account; empty or null clears it.
    public Task<ApiResponse> SetNote(string userId, string? text)
    {
        var url = $"{ServiceConfig.Current.ApiBase}/api/user/{Uri.EscapeDataString(userId)}/note";
        return string.IsNullOrWhiteSpace(text) ? DeleteAsync(url) : PutAsync(url, new { Text = text });
    }

    // The badge catalogue, granted or not.
    public Task<ApiResponse<List<BadgeInfo>>> GetBadges()
        => GetAsync<List<BadgeInfo>>($"{ServiceConfig.Current.ApiBase}/api/badges");

    // pinned first, then online, then by name
    public Task<ApiResponse<List<FriendInfo>>> GetFriends()
        => GetAsync<List<FriendInfo>>($"{ServiceConfig.Current.ApiBase}/api/friends");

    // incoming and outgoing, newest first
    public Task<ApiResponse<List<FriendRequestInfo>>> GetFriendRequests()
        => GetAsync<List<FriendRequestInfo>>($"{ServiceConfig.Current.ApiBase}/api/friends/requests");

    public Task<ApiResponse<FriendRequestResult>> SendFriendRequest(string userId)
        => PostAsync<FriendRequestResult>($"{ServiceConfig.Current.ApiBase}/api/friends/requests", new { UserId = userId });

    // the recipient only
    public Task<ApiResponse<FriendInfo>> AcceptFriendRequest(string requestId)
        => PostAsync<FriendInfo>($"{ServiceConfig.Current.ApiBase}/api/friends/requests/{Uri.EscapeDataString(requestId)}/accept", null);

    // the recipient only
    public Task<ApiResponse> DeclineFriendRequest(string requestId)
        => PostAsync($"{ServiceConfig.Current.ApiBase}/api/friends/requests/{Uri.EscapeDataString(requestId)}/decline", null);

    // the sender only
    public Task<ApiResponse> CancelFriendRequest(string requestId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/friends/requests/{Uri.EscapeDataString(requestId)}");

    public Task<ApiResponse> RemoveFriend(string userId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/friends/{Uri.EscapeDataString(userId)}");

    // the caller's side only
    public Task<ApiResponse> SetFriendPinned(string userId, bool pinned)
    {
        var url = $"{ServiceConfig.Current.ApiBase}/api/friends/{Uri.EscapeDataString(userId)}/pin";
        return pinned ? PutAsync(url, null) : DeleteAsync(url);
    }

    public Task<ApiResponse<List<BlockInfo>>> GetBlocks()
        => GetAsync<List<BlockInfo>>($"{ServiceConfig.Current.ApiBase}/api/blocks");

    // Also ends the friendship both ways and any request between the two.
    public Task<ApiResponse> BlockUser(string userId)
        => PutAsync($"{ServiceConfig.Current.ApiBase}/api/blocks/{Uri.EscapeDataString(userId)}", null);

    public Task<ApiResponse> UnblockUser(string userId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/blocks/{Uri.EscapeDataString(userId)}");

    // An account reported to the moderators, as one multipart form with the picture as a file part. Sent
    // through the content client for its long timeout: a 5 MB picture on a slow line outlasts the api
    // client's 30 s. The bearer rides on the request itself. Answers 201 { reportId }; 400, 404 (no such
    // account), 409 (yourself) and 429 (too many this hour) are refusals. -xlinka
    public async Task<ApiResponse<ReportReceipt>> SubmitReport(ReportSubmission report, IProgress<float>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return ApiResponse<ReportReceipt>.Fail(HttpStatusCode.Unauthorized, "Not authenticated");

        byte[] body;
        MediaTypeHeaderValue? type;
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent(report.TargetUserId), "targetUserId");
            form.Add(new StringContent(report.Category), "category");
            form.Add(new StringContent(report.Reason), "reason");
            if (!string.IsNullOrEmpty(report.WorldName))
                form.Add(new StringContent(report.WorldName), "worldName");
            if (!string.IsNullOrEmpty(report.SessionId))
                form.Add(new StringContent(report.SessionId), "sessionId");
            if (report.Image is { Length: > 0 } image)
            {
                var file = new ByteArrayContent(image);
                file.Headers.ContentType = new MediaTypeHeaderValue(report.ImageType ?? "application/octet-stream");
                form.Add(file, "image", string.IsNullOrEmpty(report.ImageName) ? "picture" : report.ImageName);
            }
            type = form.Headers.ContentType;
            body = await form.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        return await SendAsync<ReportReceipt>(_content, HttpMethod.Post, $"{ServiceConfig.Current.ApiBase}/api/reports",
            new UploadProgressContent(body, type, progress), auth: true, ct).ConfigureAwait(false);
    }

    // Where the signed-in account is, for its friends. Lives about two and a half minutes on the service, so
    // it is sent every minute as well as on a change.
    public Task<ApiResponse> ReportMyPresence(PresenceReportBody report)
        => PutAsync($"{ServiceConfig.Current.ApiBase}/api/presence/me", report);

    // newest last message first
    public Task<ApiResponse<List<ChatSummaryInfo>>> GetChats()
        => GetAsync<List<ChatSummaryInfo>>($"{ServiceConfig.Current.ApiBase}/api/chats");

    // Newest last; before is a message id to page back from, take is 1 to 100.
    public Task<ApiResponse<List<ChatMessageInfo>>> GetChatMessages(string userId, string? before = null, int take = 50)
    {
        var url = $"{ServiceConfig.Current.ApiBase}/api/chats/{Uri.EscapeDataString(userId)}/messages?take={take}";
        if (!string.IsNullOrEmpty(before))
            url += $"&before={Uri.EscapeDataString(before)}";
        return GetAsync<List<ChatMessageInfo>>(url);
    }

    // Friends only. Body for text, Item for an item, never both.
    public Task<ApiResponse<ChatMessageInfo>> SendChatMessage(string userId, SendMessageBody message)
        => PostAsync<ChatMessageInfo>($"{ServiceConfig.Current.ApiBase}/api/chats/{Uri.EscapeDataString(userId)}/messages", message);

    public Task<ApiResponse> MarkChatRead(string userId)
        => PostAsync($"{ServiceConfig.Current.ApiBase}/api/chats/{Uri.EscapeDataString(userId)}/read", null);

    // Hides every message so far, for the caller only.
    public Task<ApiResponse> ClearChat(string userId)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/chats/{Uri.EscapeDataString(userId)}");

    // One message: hidden for the caller only, or with forBoth one of the caller's own taken back from both sides.
    // One not in the chat or already gone answers as done.
    public Task<ApiResponse> DeleteChatMessage(string userId, string messageId, bool forBoth)
        => DeleteAsync($"{ServiceConfig.Current.ApiBase}/api/chats/{Uri.EscapeDataString(userId)}/messages"
            + $"?id={Uri.EscapeDataString(messageId)}&for={(forBoth ? "both" : "me")}");

    // Whether the sessions some invites in the chat name still run: one answer per id, up to 20 at a time.
    public Task<ApiResponse<List<ChatInviteStateInfo>>> GetChatInviteStates(string userId, IReadOnlyList<string> messageIds)
    {
        var escaped = new string[messageIds.Count];
        for (int i = 0; i < escaped.Length; i++)
            escaped[i] = Uri.EscapeDataString(messageIds[i]);
        var ids = string.Join(",", escaped);
        return GetAsync<List<ChatInviteStateInfo>>($"{ServiceConfig.Current.ApiBase}/api/chats/{Uri.EscapeDataString(userId)}/invites?ids={ids}");
    }

    #endregion

    #region Content

    // Which of these blobs the store does not have, asked at most 512 at a time.
    public async Task<ApiResponse<List<string>>> MissingContent(IReadOnlyList<string> hashes, CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return ApiResponse<List<string>>.Fail(HttpStatusCode.Unauthorized, "Not authenticated");
        const int Batch = 512;
        var missing = new List<string>();
        for (int start = 0; start < hashes.Count; start += Batch)
        {
            var batch = new List<string>(System.Math.Min(Batch, hashes.Count - start));
            for (int i = start; i < hashes.Count && i < start + Batch; i++)
                batch.Add(hashes[i]);
            var answer = await SendAsync<MissingContentResponse>(_content, HttpMethod.Post, $"{ServiceConfig.Current.ContentBase}/content/missing",
                CreateJsonContent(new { Hashes = batch }), auth: true, ct);
            if (answer.Failed || answer.Data == null)
                return Refused<List<string>>(answer);
            missing.AddRange(answer.Data.Missing);
        }
        return ApiResponse<List<string>>.Ok(missing);
    }

    public async Task<ApiResponse<byte[]>> FetchContent(string hash, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var fetched = await FetchContentTo(hash, buffer, progress, ct).ConfigureAwait(false);
        return fetched.Success ? ApiResponse<byte[]>.Ok(buffer.ToArray()) : ApiResponse<byte[]>.Fail(fetched.Status, fetched.Message);
    }

    // A blob streamed into the caller's stream as it arrives, so a download bound for disk never sits in
    // memory whole. Answers the length written. A body that stops short of the length the service declared
    // is a failed fetch, not a smaller blob.
    public async Task<ApiResponse<long>> FetchContentTo(string hash, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<long>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        var url = ServiceConfig.Current.GetContentUrl(hash);
        var state = new TransferProgress { Hash = hash, State = TransferState.Active };

        try
        {
            using var response = await _content.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                ReportDownload(progress, state with { State = TransferState.Error });
                return ApiResponse<long>.Fail(response.StatusCode);
            }

            Connectivity.ReportSuccess();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            state = state with { TotalBytes = totalBytes };

            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            // pooled buffer so we dont shit on the gc
            var chunk = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long received = 0;
                int read;

                while ((read = await stream.ReadAsync(chunk.AsMemory(0, 81920), ct)) > 0)
                {
                    await destination.WriteAsync(chunk.AsMemory(0, read), ct);
                    received += read;
                    ReportDownload(progress, state with { TransferredBytes = received });
                }

                if (totalBytes > 0 && received != totalBytes)
                {
                    ReportDownload(progress, state with { TransferredBytes = received, State = TransferState.Error });
                    return ApiResponse<long>.Fail(HttpStatusCode.BadGateway, $"The download stopped at {received} of {totalBytes} bytes");
                }

                ReportDownload(progress, state with { TransferredBytes = received, State = TransferState.Completed });
                return ApiResponse<long>.Ok(received);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            ReportDownload(progress, state with { State = TransferState.Cancelled });
            return ApiResponse<long>.Fail(HttpStatusCode.RequestTimeout, "Cancelled");
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            ReportDownload(progress, state with { State = TransferState.Error });
            return ApiResponse<long>.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            ReportDownload(progress, state with { State = TransferState.Error });
            return ApiResponse<long>.FromException(ex);
        }
    }

    // A blob into the store under the hash it was declared with: begin, then the chunks in order, then
    // finish, which checks the whole against the hash. The service says at begin when it has the blob
    // already and nothing is sent; a session it has forgotten, or chunks it lost, start the blob over once.
    // Every request may take the content client's long timeout, a slow line included. -xlinka
    public async Task<ApiResponse<ContentInfo>> UploadContent(byte[] data, string hash, string? extension, string? contentType,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return ApiResponse<ContentInfo>.Fail(HttpStatusCode.Unauthorized, "Not authenticated");

        var state = new TransferProgress { Hash = hash, TotalBytes = data.LongLength, State = TransferState.Queued };
        var contentBase = ServiceConfig.Current.ContentBase;
        for (int round = 0; ; round++)
        {
            var begun = await SendAsync<UploadHandle>(_content, HttpMethod.Post, $"{contentBase}/content/upload/begin",
                CreateJsonContent(new { Hash = hash, Size = data.LongLength, Extension = extension, ContentType = contentType }), auth: true, ct);
            if (begun.Failed || begun.Data == null)
            {
                ReportUpload(progress, state with { State = TransferState.Error });
                return Refused<ContentInfo>(begun);
            }

            var handle = begun.Data;
            if (handle.AlreadyStored || string.IsNullOrEmpty(handle.UploadId))
            {
                ReportUpload(progress, state with { TransferredBytes = data.LongLength, State = TransferState.Completed });
                return ApiResponse<ContentInfo>.Ok(new ContentInfo { Hash = hash, Size = data.LongLength });
            }

            int chunkSize = handle.ChunkSize > 0 ? handle.ChunkSize : DefaultChunkSize;
            int chunks = System.Math.Max(handle.TotalChunks, (int)((data.LongLength + chunkSize - 1) / chunkSize));
            var session = $"{contentBase}/content/upload/{Uri.EscapeDataString(handle.UploadId!)}";
            state = state with { State = TransferState.Active, TransferredBytes = 0 };
            ReportUpload(progress, state);

            ApiResponse? refused = null;
            long sent = 0;
            for (int index = 0; index < chunks; index++)
            {
                int offset = (int)System.Math.Min((long)index * chunkSize, data.LongLength);
                int length = (int)System.Math.Min(chunkSize, data.LongLength - offset);
                var body = new ByteArrayContent(data, offset, length);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var put = await SendAsync<JsonElement>(_content, HttpMethod.Put, $"{session}/chunk/{index}", body, auth: true, ct);
                if (put.Failed)
                {
                    refused = put;
                    break;
                }
                sent += length;
                ReportUpload(progress, state with { TransferredBytes = sent });
            }

            if (refused == null)
            {
                var finished = await SendAsync<JsonElement>(_content, HttpMethod.Post, $"{session}/finish", null, auth: true, ct);
                if (finished.Success)
                {
                    ReportUpload(progress, state with { TransferredBytes = data.LongLength, State = TransferState.Completed });
                    return ApiResponse<ContentInfo>.Ok(new ContentInfo { Hash = hash, Size = data.LongLength });
                }
                refused = finished;
            }

            bool again = round == 0 && (refused.Error == "no-session" || refused.Error == "missing-chunks");
            if (!again)
            {
                ReportUpload(progress, state with { State = ct.IsCancellationRequested ? TransferState.Cancelled : TransferState.Error });
                return Refused<ContentInfo>(refused);
            }
        }
    }

    #endregion

    #region HTTP

    private async Task<ApiResponse<T>> GetAsync<T>(string url)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<T>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        try
        {
            using var response = await _api.GetAsync(url);
            Connectivity.ReportSuccess();

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return ApiResponse<T>.Fail(response.StatusCode, ErrorMessage(body));

            // A route that answers "done" with no payload (204, or a 200 with an empty body) is a success,
            // not a parse failure: deserialising an empty string throws and used to come back as a 500.
            // -xlinka
            if (string.IsNullOrWhiteSpace(body))
                return ApiResponse<T>.Ok(default!);

            var data = JsonSerializer.Deserialize<T>(body, _json);
            return ApiResponse<T>.Ok(data!);
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            return ApiResponse<T>.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            return ApiResponse<T>.FromException(ex);
        }
    }

    private async Task<ApiResponse> PostAsync(string url, object? payload)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        try
        {
            using var content = CreateJsonContent(payload);
            using var response = await _api.PostAsync(url, content);
            Connectivity.ReportSuccess();
            var body = await response.Content.ReadAsStringAsync();
            return new ApiResponse
            {
                Success = response.IsSuccessStatusCode,
                Status = response.StatusCode,
                RawBody = body,
                Message = response.IsSuccessStatusCode ? null : ErrorMessage(body)
            };
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            return ApiResponse.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            return ApiResponse.FromException(ex);
        }
    }

    private async Task<ApiResponse<T>> PostAsync<T>(string url, object? payload)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<T>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        try
        {
            using var content = CreateJsonContent(payload);
            using var response = await _api.PostAsync(url, content);
            Connectivity.ReportSuccess();

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return ApiResponse<T>.Fail(response.StatusCode, ErrorMessage(body));

            // A route that answers "done" with no payload (204, or a 200 with an empty body) is a success,
            // not a parse failure: deserialising an empty string throws and used to come back as a 500.
            // -xlinka
            if (string.IsNullOrWhiteSpace(body))
                return ApiResponse<T>.Ok(default!);

            var data = JsonSerializer.Deserialize<T>(body, _json);
            return ApiResponse<T>.Ok(data!);
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            return ApiResponse<T>.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            return ApiResponse<T>.FromException(ex);
        }
    }

    private async Task<ApiResponse> DeleteAsync(string url)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        try
        {
            using var response = await _api.DeleteAsync(url);
            Connectivity.ReportSuccess();
            var body = await response.Content.ReadAsStringAsync();
            return new ApiResponse
            {
                Success = response.IsSuccessStatusCode,
                Status = response.StatusCode,
                RawBody = body,
                Message = response.IsSuccessStatusCode ? null : ErrorMessage(body)
            };
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            return ApiResponse.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            return ApiResponse.FromException(ex);
        }
    }

    private async Task<ApiResponse> PutAsync(string url, object? payload)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        try
        {
            using var content = CreateJsonContent(payload);
            using var response = await _api.PutAsync(url, content);
            Connectivity.ReportSuccess();
            var body = await response.Content.ReadAsStringAsync();
            return new ApiResponse
            {
                Success = response.IsSuccessStatusCode,
                Status = response.StatusCode,
                RawBody = body,
                Message = response.IsSuccessStatusCode ? null : ErrorMessage(body)
            };
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            return ApiResponse.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            return ApiResponse.FromException(ex);
        }
    }

    private async Task<ApiResponse<T>> PatchAsync<T>(string url, object? payload)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<T>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        try
        {
            using var content = CreateJsonContent(payload);
            using var response = await _api.PatchAsync(url, content);
            Connectivity.ReportSuccess();

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return ApiResponse<T>.Fail(response.StatusCode, ErrorMessage(body));

            // A route that answers "done" with no payload (204, or a 200 with an empty body) is a success,
            // not a parse failure: deserialising an empty string throws and used to come back as a 500.
            // -xlinka
            if (string.IsNullOrWhiteSpace(body))
                return ApiResponse<T>.Ok(default!);

            var data = JsonSerializer.Deserialize<T>(body, _json);
            return ApiResponse<T>.Ok(data!);
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            return ApiResponse<T>.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            return ApiResponse<T>.FromException(ex);
        }
    }

    // Services return either { "message": "..." } or ProblemDetails { "title": "..." }.
    // Unwrap the summary once for callers; leave diagnostic details out of the message. -xlinka
    private static string? ErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var trimmed = body!.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return body;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "message", "title" })
                {
                    if (document.RootElement.TryGetProperty(key, out var message)
                        && message.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(message.GetString()))
                        return message.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // not JSON after all
        }
        return body;
    }

    // The code half of a refusal, { "error": "quota", ... }, for a caller that acts on which one it was.
    private static string? ErrorCode(string? body)
    {
        var trimmed = body?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{')
            return null;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The body of a write that went through, when it reads as T. A shape this client does not know never
    // turns a done write into a failure.
    private T? Read<T>(JsonElement body) where T : class
    {
        if (body.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            return body.Deserialize<T>(_json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ApiResponse<T> Refused<T>(ApiResponse answer) => new()
    {
        Success = false,
        Status = answer.Status,
        Message = answer.Message,
        Error = answer.Error,
        RawBody = answer.RawBody,
    };

    // One request with the answer read into T. An authenticated one carries the bearer on the request
    // itself, so the content client, which anonymous reads share, never holds the token. A refusal keeps
    // the service's code in Error; a request the caller called off comes back 408 "Cancelled", and one
    // the timeout ended 408 "Timed out". -xlinka
    private async Task<ApiResponse<T>> SendAsync<T>(HttpClient http, HttpMethod method, string url, HttpContent? content, bool auth,
        CancellationToken ct)
    {
        if (!Connectivity.IsOnline)
        {
            content?.Dispose();
            return ApiResponse<T>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");
        }

        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            var token = _session?.Token;
            if (auth && !string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            Connectivity.ReportSuccess();

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return new ApiResponse<T>
                {
                    Success = false,
                    Status = response.StatusCode,
                    Message = ErrorMessage(body),
                    Error = ErrorCode(body),
                    RawBody = body,
                };
            }
            if (string.IsNullOrWhiteSpace(body))
                return ApiResponse<T>.Ok(default!);
            return ApiResponse<T>.Ok(JsonSerializer.Deserialize<T>(body, _json)!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ApiResponse<T>.Fail(HttpStatusCode.RequestTimeout, "Cancelled");
        }
        catch (OperationCanceledException)
        {
            Connectivity.ReportFailure();
            return ApiResponse<T>.Fail(HttpStatusCode.RequestTimeout, "Timed out");
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            return ApiResponse<T>.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            return ApiResponse<T>.FromException(ex);
        }
    }

    private HttpContent CreateJsonContent(object? payload)
    {
        if (payload == null)
            return new StringContent("", Encoding.UTF8, "application/json");

        var json = JsonSerializer.Serialize(payload, _json);

        if (!EnableCompression)
            return new StringContent(json, Encoding.UTF8, "application/json");

        var jsonBytes = Encoding.UTF8.GetBytes(json);
        using var outputStream = new MemoryStream();
        using (var gzip = new GZipStream(outputStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(jsonBytes, 0, jsonBytes.Length);
        }

        var compressedBytes = outputStream.ToArray();
        var content = new ByteArrayContent(compressedBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    // Every step goes to the caller's reporter when it gave one, and to the registry always, so the
    // Debug screen and the in-world readout see every transfer without anyone wiring them up. -xlinka
    private static void ReportDownload(IProgress<TransferProgress>? progress, TransferProgress state)
    {
        TransferRegistry.Report(state, upload: false);
        progress?.Report(state);
    }

    private static void ReportUpload(IProgress<TransferProgress>? progress, TransferProgress state)
    {
        TransferRegistry.Report(state, upload: true);
        progress?.Report(state);
    }

    #endregion
}

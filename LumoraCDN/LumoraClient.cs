// handles all the cloud shit with compression and parallel uploads

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.CDN;

public sealed class LumoraClient : IDisposable
{
    private readonly HttpClient _api;
    private readonly HttpClient _content;
    private readonly JsonSerializerOptions _json;
    private readonly string _deviceId;
    private Session? _session;

    public const int DefaultChunkSize = 4 * 1024 * 1024; // 4MB chunks
    public const int MaxParallelChunks = 4; // dont go too crazy here

    public static TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public static TimeSpan ContentTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public static bool EnableCompression { get; set; } = true;

    public string DeviceId => _deviceId;
    public Session? CurrentSession => _session;
    public bool IsAuthenticated => _session != null && DateTime.UtcNow < _session.Expires;

    public event Action<Session>? Authenticated;
    public event Action? SignedOut;

    public LumoraClient(string deviceId, string appName = "LumoraVR", string version = "0.1.0")
    {
        _deviceId = deviceId;
        var userAgent = new ProductInfoHeaderValue(appName, version);

        // api client with gzip support
        var apiHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _api = new HttpClient(apiHandler) { Timeout = ApiTimeout };
        _api.DefaultRequestHeaders.UserAgent.Add(userAgent);
        _api.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        _api.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));

        // content client for big ass files
        var contentHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _content = new HttpClient(contentHandler) { Timeout = ContentTimeout };
        _content.DefaultRequestHeaders.UserAgent.Add(userAgent);
        _content.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public void Dispose()
    {
        _api.Dispose();
        _content.Dispose();
    }

    #region Auth

    public async Task<ApiResponse<Session>> SignIn(string username, string password, bool remember = false)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<Session>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        var payload = new { Username = username, Password = password, Remember = remember, DeviceId = _deviceId };
        var result = await PostAsync<Session>($"{ServiceConfig.Current.ApiBase}/auth/login", payload);

        if (result.Success && result.Data != null)
            ApplySession(result.Data);

        return result;
    }

    public async Task<ApiResponse<Session>> SignInWithToken(string userId, string token)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<Session>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        var payload = new { UserId = userId, Token = token, DeviceId = _deviceId };
        var result = await PostAsync<Session>($"{ServiceConfig.Current.ApiBase}/auth/token", payload);

        if (result.Success && result.Data != null)
            ApplySession(result.Data);

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

        var result = await PostAsync($"{ServiceConfig.Current.ApiBase}/auth/logout", null);
        ClearSession();
        return result;
    }

    public void SetSession(Session session) => ApplySession(session);

    private void ApplySession(Session session)
    {
        _session = session;
        _api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        Authenticated?.Invoke(session);
    }

    private void ClearSession()
    {
        _session = null;
        _api.DefaultRequestHeaders.Authorization = null;
        SignedOut?.Invoke();
    }

    #endregion

    #region Content

    public Task<ApiResponse<ContentInfo>> GetContentInfo(string hash)
        => GetAsync<ContentInfo>($"{ServiceConfig.Current.ApiBase}/content/{hash}");

    public async Task<bool> ContentExists(string hash)
    {
        if (!Connectivity.IsOnline)
            return false;

        var url = ServiceConfig.Current.GetContentUrl(hash);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _content.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            Connectivity.ReportFailure();
            return false;
        }
    }

    // download content by hash
    public async Task<ApiResponse<byte[]>> FetchContent(string hash, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<byte[]>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        var url = ServiceConfig.Current.GetContentUrl(hash);
        var state = new TransferProgress { Hash = hash, State = TransferState.Active };

        try
        {
            using var response = await _content.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                ReportProgress(progress, state with { State = TransferState.Error });
                return ApiResponse<byte[]>.Fail(response.StatusCode);
            }

            Connectivity.ReportSuccess();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            state = state with { TotalBytes = totalBytes };

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();

            // pooled buffer so we dont shit on the gc
            var chunk = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long received = 0;
                int read;

                while ((read = await stream.ReadAsync(chunk.AsMemory(0, 81920), ct)) > 0)
                {
                    await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
                    received += read;
                    ReportProgress(progress, state with { TransferredBytes = received });
                }

                ReportProgress(progress, state with { TransferredBytes = received, State = TransferState.Completed });
                return ApiResponse<byte[]>.Ok(buffer.ToArray());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            ReportProgress(progress, state with { State = TransferState.Cancelled });
            return ApiResponse<byte[]>.Fail(HttpStatusCode.RequestTimeout, "Cancelled");
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            ReportProgress(progress, state with { State = TransferState.Error });
            return ApiResponse<byte[]>.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            ReportProgress(progress, state with { State = TransferState.Error });
            return ApiResponse<byte[]>.FromException(ex);
        }
    }

    public Task<ApiResponse<byte[]>> FetchContent(Uri uri, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var hash = ContentHash.ParseHash(uri);
        return FetchContent(hash, progress, ct);
    }

    // upload from file path
    public async Task<ApiResponse<ContentInfo>> StoreContent(string filePath, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return ApiResponse<ContentInfo>.Fail(HttpStatusCode.NotFound, "File not found");

        var fileInfo = new FileInfo(filePath);
        var mime = MimeTypes.FromExtension(fileInfo.Extension);

        await using var stream = File.OpenRead(filePath);
        var hash = ContentHash.FromStream(stream);
        stream.Position = 0;

        return await StoreContentInternal(stream, hash, mime, fileInfo.Extension, progress, ct);
    }

    // upload from bytes
    public async Task<ApiResponse<ContentInfo>> StoreContent(byte[] data, string mimeType, string? extension = null, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var hash = ContentHash.FromBytes(data);
        extension ??= MimeTypes.ToExtension(mimeType);

        using var stream = new MemoryStream(data);
        return await StoreContentInternal(stream, hash, mimeType, extension, progress, ct);
    }

    // the actual upload logic with parallel chunks
    private async Task<ApiResponse<ContentInfo>> StoreContentInternal(Stream stream, string hash, string mimeType, string? extension, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse<ContentInfo>.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        var state = new TransferProgress { Hash = hash, TotalBytes = stream.Length, State = TransferState.Queued };

        try
        {
            // check if already exists so we dont waste bandwidth
            if (await ContentExists(hash))
            {
                var existing = await GetContentInfo(hash);
                if (existing.Success)
                {
                    ReportProgress(progress, state with { TransferredBytes = state.TotalBytes, State = TransferState.Completed });
                    return existing;
                }
            }

            // start the upload
            var initPayload = new { Hash = hash, ContentType = mimeType, Extension = extension, Size = stream.Length };
            var initResult = await PostAsync<UploadHandle>($"{ServiceConfig.Current.ApiBase}/content/upload/begin", initPayload);

            if (!initResult.Success || initResult.Data == null)
                return ApiResponse<ContentInfo>.Fail(initResult.Status, initResult.Message);

            var handle = initResult.Data;
            var chunkSize = handle.ChunkSize > 0 ? handle.ChunkSize : DefaultChunkSize;

            state = state with { State = TransferState.Active };
            ReportProgress(progress, state);

            // read all chunks first
            var chunks = new List<(int Index, byte[] Data)>();
            var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);

            try
            {
                int chunkIndex = 0;
                while (stream.Position < stream.Length)
                {
                    int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), ct);
                    if (bytesRead == 0) break;

                    var chunkData = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, chunkData, 0, bytesRead);
                    chunks.Add((chunkIndex++, chunkData));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // upload chunks in parallel - this is the good shit
            long transferred = 0;
            var semaphore = new SemaphoreSlim(MaxParallelChunks);
            var uploadTasks = new List<Task<bool>>();

            foreach (var (index, chunkData) in chunks)
            {
                await semaphore.WaitAsync(ct);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        using var chunkContent = new ByteArrayContent(chunkData);
                        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                        var chunkUrl = $"{ServiceConfig.Current.ApiBase}/content/upload/{handle.Id}/chunk/{index}";
                        using var chunkResponse = await _api.PutAsync(chunkUrl, chunkContent, ct);

                        if (chunkResponse.IsSuccessStatusCode)
                        {
                            Interlocked.Add(ref transferred, chunkData.Length);
                            ReportProgress(progress, state with { TransferredBytes = Interlocked.Read(ref transferred) });
                            return true;
                        }
                        return false;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);

                uploadTasks.Add(task);
            }

            var results = await Task.WhenAll(uploadTasks);
            if (Array.Exists(results, r => !r))
            {
                ReportProgress(progress, state with { State = TransferState.Error });
                return ApiResponse<ContentInfo>.Fail(HttpStatusCode.InternalServerError, "Chunk upload failed");
            }

            Connectivity.ReportSuccess();

            // finalize
            var finishResult = await PostAsync<ContentInfo>($"{ServiceConfig.Current.ApiBase}/content/upload/{handle.Id}/finish", null);

            ReportProgress(progress, state with
            {
                TransferredBytes = stream.Length,
                State = finishResult.Success ? TransferState.Completed : TransferState.Error
            });

            return finishResult;
        }
        catch (OperationCanceledException)
        {
            ReportProgress(progress, state with { State = TransferState.Cancelled });
            return ApiResponse<ContentInfo>.Fail(HttpStatusCode.RequestTimeout, "Cancelled");
        }
        catch (HttpRequestException)
        {
            Connectivity.ReportFailure();
            ReportProgress(progress, state with { State = TransferState.Error });
            return ApiResponse<ContentInfo>.Fail(HttpStatusCode.ServiceUnavailable, "Network error");
        }
        catch (Exception ex)
        {
            ReportProgress(progress, state with { State = TransferState.Error });
            return ApiResponse<ContentInfo>.FromException(ex);
        }
    }

    #endregion

    #region HTTP

    private async Task<ApiResponse> GetAsync(string url)
    {
        if (!Connectivity.IsOnline)
            return ApiResponse.Fail(HttpStatusCode.ServiceUnavailable, "Offline");

        try
        {
            using var response = await _api.GetAsync(url);
            Connectivity.ReportSuccess();
            return new ApiResponse
            {
                Success = response.IsSuccessStatusCode,
                Status = response.StatusCode,
                RawBody = await response.Content.ReadAsStringAsync()
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
                return ApiResponse<T>.Fail(response.StatusCode, body);

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
            return new ApiResponse
            {
                Success = response.IsSuccessStatusCode,
                Status = response.StatusCode,
                RawBody = await response.Content.ReadAsStringAsync()
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
                return ApiResponse<T>.Fail(response.StatusCode, body);

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

    // gzip compress json payloads
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

    private static void ReportProgress(IProgress<TransferProgress>? progress, TransferProgress state)
        => progress?.Report(state);

    #endregion
}

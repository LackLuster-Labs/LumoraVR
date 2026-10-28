// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Threading.Tasks;
using Lumora.Audio;

namespace Lumora.Core.Assets;

// A decoded sound, gathered from its URL and shared by every requester for that URL.
//
// There is no platform-side counterpart and no asset hook. The mixer is ours and it reads plain float
// PCM, so a clip is numbers the engine plays itself: nothing about it needs a renderer object or a
// frame boundary to upload on, and the whole asset works headless. -xlinka
public sealed class AudioClip : LoadableAsset
{
    public AudioClipData? Data { get; private set; }

    public bool IsReady { get; private set; }

    public string? FailureReason { get; private set; }

    public double Duration => Data?.Duration ?? 0.0;

    public int Channels => Data?.Channels ?? 0;

    public int SampleRate => Data?.SampleRate ?? 0;

    // Raised on the loading thread. Anything world-side marshals before touching sync state.
    public event Action<AudioClip>? Ready;
    public event Action<AudioClip, string>? Failed;

    protected override async Task LoadSelf()
    {
        var bytes = await AssetManager.RequestGather(AssetURL).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
        {
            Fail($"no audio data arrived for {AssetURL}");
            return;
        }

        string? hint = ExtensionOf(AssetURL);

        // The decode is the expensive half and the gather can complete on the world update loop, so
        // it is put somewhere a stall costs nothing rather than inheriting whatever thread finished
        // the fetch.
        var result = await DecodeOffThread(() =>
        {
            AudioDecoders.TryDecode(new ReadOnlyMemory<byte>(bytes), hint, out var decoded, out var error);
            return (decoded, error);
        }).ConfigureAwait(false);

        if (result.decoded == null)
        {
            Fail(string.IsNullOrEmpty(result.error)
                ? $"the audio at {AssetURL} could not be decoded"
                : result.error!);
            return;
        }

        Data = result.decoded;
        FailureReason = null;
        IsReady = true;
        Version++;

        try
        {
            Ready?.Invoke(this);
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"AudioClip: ready handler threw for {AssetURL}: {ex.Message}");
        }
    }

    // Samples made in memory by a procedural provider rather than gathered from a URL. World thread.
    public void SetGenerated(AudioClipData data)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        FailureReason = null;
        IsReady = true;
        Version++;
    }

    public override void Unload()
    {
        Data = null;
        IsReady = false;
    }

    private void Fail(string reason)
    {
        FailureReason = reason;
        IsReady = false;
        FailLoad(reason);
        try
        {
            Failed?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"AudioClip: failure handler threw for {AssetURL}: {ex.Message}");
        }
    }

    // Only a hint. A content-addressed address carries no extension at all, and the decoder sniffs
    // the bytes anyway, so a wrong or missing answer here costs nothing.
    internal static string? ExtensionOf(Uri? url)
    {
        if (url == null)
            return null;
        if (url.IsAbsoluteUri && url.Scheme == "local"
            && Engine.Current?.LocalDB?.GetExtension(url.OriginalString) is { Length: > 0 } filed)
            return filed;
        try
        {
            string path = url.IsAbsoluteUri && url.IsFile ? url.LocalPath
                : url.IsAbsoluteUri ? url.AbsolutePath
                : url.OriginalString;
            var extension = Path.GetExtension(path);
            return string.IsNullOrEmpty(extension) ? null : extension;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

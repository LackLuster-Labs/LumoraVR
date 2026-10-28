// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Cloud.Cdn;

// A body already in memory, written to the connection in slices so the caller can watch it go: progress is
// the share written, 0 to 1. Its length is known up front, so the request never goes chunked.
internal sealed class UploadProgressContent : HttpContent
{
    private const int Slice = 64 * 1024;

    private readonly byte[] _body;
    private readonly IProgress<float>? _progress;

    public UploadProgressContent(byte[] body, MediaTypeHeaderValue? type, IProgress<float>? progress)
    {
        _body = body;
        _progress = progress;
        if (type != null)
            Headers.ContentType = MediaTypeHeaderValue.Parse(type.ToString());
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        _progress?.Report(0f);
        for (int at = 0; at < _body.Length; at += Slice)
        {
            int count = System.Math.Min(Slice, _body.Length - at);
            await stream.WriteAsync(_body.AsMemory(at, count), cancellationToken).ConfigureAwait(false);
            _progress?.Report((float)(at + count) / _body.Length);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _body.Length;
        return true;
    }
}

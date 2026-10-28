// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Assimp;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

// WHAT A MODEL FILE MAY OPEN WHILE IT IS PARSED.
//
// A model can be more than one file: an OBJ names its .mtl, a .gltf names its .bin buffers. The parse used to run
// from a memory stream, and there the parser looks for those companions relative to whatever folder the game was
// started in, so an OBJ came in all grey and a .gltf with its buffers beside it did not come in at all. Worse, the
// same lookup took any path a file cared to write, absolute or climbing out with "..", from anywhere on the disk.
//
// This hands the parser the model itself from memory (the bytes the importer already read and prepared) and
// anything else only from inside the model's own folder: a plain relative name, or a subfolder of it, no drive,
// no root, no "..", nothing reached through a link, read-only, and capped in size one by one and all together.
// With no folder at all (a model that is only bytes) the model is all there is. A refused companion is said once
// in the log and the parse carries on without it, which is what a missing one did before. -xlinka
internal sealed class ModelFileAccess : IOSystem
{
    public const long MaxFileBytes = 512L * 1024 * 1024;
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    private readonly long _maxFile;
    private readonly long _maxTotal;

    private readonly byte[] _main;
    private readonly string? _folder;
    private long _served;

    public string MainName { get; }

    // mainName is what the parser asks for and how it picks the format, so it carries the extension and no folder.
    public ModelFileAccess(byte[] main, string mainName, string? folder, long maxFile = MaxFileBytes, long maxTotal = MaxTotalBytes)
    {
        _main = main;
        _maxFile = maxFile;
        _maxTotal = maxTotal;
        MainName = Path.GetFileName(mainName);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            var full = Path.GetFullPath(folder);
            _folder = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        }
    }

    public override IOStream OpenFile(string pathToFile, FileIOMode fileMode)
    {
        if (fileMode is not (FileIOMode.Read or FileIOMode.ReadBinary or FileIOMode.ReadText))
            return Refuse(pathToFile, "not a read");

        string name = Clean(pathToFile);
        if (string.Equals(name, MainName, StringComparison.OrdinalIgnoreCase))
            return new ReadStream(pathToFile, fileMode, new MemoryStream(_main, writable: false));

        if (_folder == null)
            return Refuse(pathToFile, "the model has no folder");
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || Path.IsPathRooted(name))
            return Refuse(pathToFile, "not a path inside the model's folder");
        foreach (var part in name.Split('/'))
        {
            if (part == "..")
                return Refuse(pathToFile, "climbs out of the model's folder");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_folder, name.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception)
        {
            return Refuse(pathToFile, "not a path");
        }
        if (!full.StartsWith(_folder, StringComparison.OrdinalIgnoreCase))
            return Refuse(pathToFile, "outside the model's folder");

        var info = new FileInfo(full);
        if (!info.Exists)
            return Refuse(pathToFile, "not there", quiet: true);
        if (ThroughLink(info))
            return Refuse(pathToFile, "reached through a link");
        if (info.Length > _maxFile || _served + info.Length > _maxTotal)
            return Refuse(pathToFile, $"too big ({info.Length / (1024 * 1024)} MB)");

        try
        {
            var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            _served += info.Length;
            return new ReadStream(pathToFile, fileMode, stream);
        }
        catch (Exception ex)
        {
            return Refuse(pathToFile, ex.Message);
        }
    }

    // The parser's own spelling of a name next to the model: separators one way, and the "./" (or ".\\") it puts
    // in front of a name it looks for beside the model gone. A path that starts at a root without that prefix
    // stays rooted and is refused.
    private static string Clean(string path)
    {
        var name = (path ?? string.Empty).Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal))
            name = name.Substring(2).TrimStart('/');
        return name;
    }

    // The file, or any folder between it and the model's, being a link to somewhere else.
    private bool ThroughLink(FileInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return true;
        for (var dir = info.Directory; dir != null; dir = dir.Parent)
        {
            var path = dir.FullName.EndsWith(Path.DirectorySeparatorChar) ? dir.FullName : dir.FullName + Path.DirectorySeparatorChar;
            if (path.Length <= _folder!.Length)
                break;
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    // The parser retries a missing companion under several spellings, so each refusal is said once and the log
    // stops after a few.
    private readonly HashSet<string> _said = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxSaid = 4;

    private IOStream Refuse(string path, string why, bool quiet = false)
    {
        if (!quiet && _said.Count < MaxSaid && _said.Add(Clean(path)))
            Logger.Warn($"ModelFileAccess: '{MainName}' asked for '{path}', refused: {why}");
        return null!;
    }

    private sealed class ReadStream : IOStream
    {
        private readonly Stream _stream;

        public ReadStream(string path, FileIOMode mode, Stream stream) : base(path, mode)
        {
            _stream = stream;
        }

        public override bool IsValid => _stream.CanRead;

        public override long Read(byte[] dataRead, long count)
        {
            int want = (int)System.Math.Min(count, dataRead.Length);
            int total = 0;
            while (total < want)
            {
                int got = _stream.Read(dataRead, total, want - total);
                if (got <= 0)
                    break;
                total += got;
            }
            return total;
        }

        public override long Write(byte[] dataToWrite, long count) => 0;

        public override ReturnCode Seek(long offset, Origin seekOrigin)
        {
            long target = seekOrigin switch
            {
                Origin.Set => offset,
                Origin.Current => _stream.Position + offset,
                _ => _stream.Length + offset,
            };
            if (target < 0 || target > _stream.Length)
                return ReturnCode.Failure;
            _stream.Position = target;
            return ReturnCode.Success;
        }

        public override long GetPosition() => _stream.Position;

        public override long GetFileSize() => _stream.Length;

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _stream.Dispose();
            base.Dispose(disposing);
        }
    }
}
